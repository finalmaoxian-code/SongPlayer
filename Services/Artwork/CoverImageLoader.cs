using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace SongPlayer.Services.Artwork;

/// <summary>
/// Downloads track artwork and hands back a frozen bitmap the UI thread can bind to.
/// </summary>
/// <remarks>
/// WPF can be handed a remote URL directly (<c>Image.Source="https://…"</c>), but that path is
/// opaque: the binding silently renders nothing when the request fails, which is exactly what a
/// dead thumbnail URL does. Loading the bytes here makes a missing cover an explicit
/// <see langword="null"/> the view can react to by showing its placeholder, and it keeps the
/// network call off the UI thread.
/// <para>
/// Failures are swallowed on purpose: artwork is decoration, and a slow or blocked image host must
/// never turn into an error next to the track title.
/// </para>
/// </remarks>
public sealed class CoverImageLoader
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Downloads and decodes <paramref name="uri"/>.
    /// </summary>
    /// <param name="uri">Absolute http(s) URL of the artwork.</param>
    /// <param name="cancellationToken">Cancellation token, cancelled when the track changes again.</param>
    /// <returns>
    /// A frozen bitmap, or <see langword="null"/> when the URL is missing, the host refuses the
    /// request, or the payload is not an image.
    /// </returns>
    /// <remarks>
    /// The bitmap is frozen so it can be created on a background thread and still be bound from the
    /// UI thread.
    /// </remarks>
    public async Task<BitmapImage?> LoadAsync(string? uri, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? source))
        {
            return null;
        }

        if (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        try
        {
            // These hosts serve a poster frame to anything that asks, but identify themselves
            // anyway: an anonymous request is the first thing a CDN starts blocking.
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            request.Headers.Referrer = new Uri("https://www.bilibili.com/");

            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);


            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var image = new BitmapImage();

            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();


            return image;
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or TaskCanceledException
                or IOException
                or NotSupportedException
                or ArgumentException)
        {
            return null;
        }
    }
}
