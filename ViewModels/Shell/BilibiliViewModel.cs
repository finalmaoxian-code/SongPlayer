using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Bilibili;
using VideoId = SongPlayer.Services.Bilibili.Bvid;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.ViewModels.Shell;

/// <summary>
/// Bilibili input in the Local Music toolbar: takes a video id, asks the local bridge for the
/// audio stream and hands the result to the player bar.
/// </summary>
/// <remarks>
/// Online playback is not a second player. The resolved stream becomes a normal
/// <see cref="Track"/>, so the seek slider, the position label, the lyrics clock and the transport
/// buttons all keep working through the same code path a local file uses. This view model only
/// knows how to turn an id into a track; whether that track plays is the player bar's business.
/// <para>
/// The CDN URL is resolved per play and never stored: Bilibili signs these links with a deadline,
/// so a saved one would fail later in a way the user could not explain.
/// </para>
/// </remarks>
public partial class BilibiliViewModel : ObservableObject
{
    /// <summary>
    /// Number of candidates requested per search. Twenty is one screenful of rows and keeps the
    /// request inside what the endpoint serves without pagination.
    /// </summary>
    private const int SearchLimit = 20;

    private readonly IBilibiliSource _source;
    private readonly CaptionStore _captions;

    /// <summary>
    /// Creates the input.
    /// </summary>
    /// <param name="source">Bridge client that resolves ids into audio URLs.</param>
    /// <param name="captions">Archive the readable subtitles are stored in.</param>
    public BilibiliViewModel(IBilibiliSource source, CaptionStore captions)
    {
        _source = source;
        _captions = captions;
    }

    /// <summary>
    /// Raised when a stream has been resolved and should start playing.
    /// </summary>
    /// <remarks>
    /// An event instead of a call keeps this type free of any reference to the player bar, matching
    /// how the library page reports a track activation.
    /// </remarks>
    public event EventHandler<Track>? TrackResolved;

    /// <summary>
    /// Raised when a resolved stream should be remembered by the library.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TrackResolved"/> because playing and remembering are different
    /// concerns: the library owns persistence, and a stream that fails to save is still a stream
    /// the user can listen to right now.
    /// </remarks>
    public event EventHandler<Track>? TrackPersisted;

    /// <summary>
    /// Result of the last attempt, shown under the toolbar.
    /// </summary>
    [ObservableProperty]
    private string statusMessage = "Search Bilibili for a song, or paste a video id.";

    /// <summary>
    /// True while a resolve is running; disables the button.
    /// </summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>
    /// True once the last attempt failed, used to colour the status line.
    /// </summary>
    [ObservableProperty]
    private bool hasError;

    /// <summary>
    /// Free text typed into the search box: a song name, an artist, or both.
    /// </summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>
    /// Candidates returned by the last search, in the order the endpoint ranked them.
    /// </summary>
    /// <remarks>
    /// A list rather than a single "best match". Bilibili search mixes official uploads, remasters,
    /// resolution badges, live cuts and outright unrelated videos, and the payload never says which
    /// one is the song. Picking the first hit would be right often enough to look intentional and
    /// wrong often enough to be infuriating, so the user chooses.
    /// </remarks>
    public ObservableCollection<BilibiliSearchResult> SearchResults { get; } = new();

    /// <summary>
    /// True while a search is running.
    /// </summary>
    [ObservableProperty]
    private bool isSearching;

    /// <summary>
    /// Line under the search box describing the last search.
    /// </summary>
    [ObservableProperty]
    private string searchSummary = "Search by song or artist, then pick the upload you want.";

    /// <summary>
    /// True when the last search produced candidates.
    /// </summary>
    public bool HasSearchResults => SearchResults.Count > 0;

    /// <summary>
    /// True when the text in the search box is a Bilibili video id.
    /// </summary>
    /// <remarks>
    /// One box serves both purposes. A listener who already has a BV number would otherwise have to
    /// use a second input for it, and pasting an id into a box that says "search" is the obvious
    /// thing to try — so the id path announces itself instead of hiding behind a separate field.
    /// </remarks>
    public bool LooksLikeBvid => VideoId.IsValid(SearchText.Trim());

    /// <summary>
    /// Runs the keyword search and fills <see cref="SearchResults"/>.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        string query = SearchText.Trim();

        if (query.Length == 0)
        {
            HasError = true;
            SearchSummary = "Type a song or artist name first.";
            return;
        }

        IsSearching = true;
        HasError = false;
        StatusMessage = $"Searching Bilibili for “{query}”…";

        try
        {
            if (!await _source.ProbeAsync())
            {
                throw new BilibiliSourceException(
                    "The local bridge is not running. Start it with “node bridge.cjs” and try again."
                );
            }

            IReadOnlyList<BilibiliSearchResult> found = await _source.SearchAsync(
                query,
                SearchLimit
            );

            SearchResults.Clear();

            foreach (BilibiliSearchResult candidate in found)
            {
                SearchResults.Add(candidate);
            }

            SearchSummary = SearchResults.Count switch
            {
                0 => $"Nothing on Bilibili for “{query}”.",
                1 => "1 upload found. Check it is the song before playing.",
                var count => $"{count} uploads found. Check which one is the song.",
            };

            StatusMessage = SearchSummary;
        }
        catch (BilibiliSourceException exception)
        {
            HasError = true;
            SearchSummary = exception.Message;
            StatusMessage = exception.Message;
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    /// <summary>
    /// Resolves and plays one of the search candidates.
    /// </summary>
    /// <param name="candidate">Row the user picked.</param>
    [RelayCommand]
    private async Task PlayCandidateAsync(BilibiliSearchResult? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        // Reuses the id path rather than a second resolve: the row already carries the id, and only
        // the resolution step differs from typing it by hand.
        SearchText = candidate.Bvid;
        await PlayAsync();
    }

    /// <summary>
    /// Re-announces the id path when the box starts or stops holding a video id.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(LooksLikeBvid));
    }

    /// <summary>
    /// Drops the current candidate list and the keyword.
    /// </summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchResults.Clear();
        SearchText = string.Empty;
        SearchSummary = "Search by song or artist, then pick the upload you want.";
        OnPropertyChanged(nameof(HasSearchResults));
    }

    /// <summary>
    /// Resolves the typed id and starts playing it.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        string candidate = SearchText.Trim();

        if (!VideoId.IsValid(candidate))
        {
            HasError = true;
            StatusMessage =
                candidate.Length == 0
                    ? "Type a song, an artist or a Bilibili video id first."
                    : "That does not look like a Bilibili video id (BV…).";
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusMessage = "Asking the bridge for the audio stream…";

        try
        {
            Track track = await ResolveTrackAsync(candidate);

            StatusMessage = $"Playing {track.Title}.";

            TrackResolved?.Invoke(this, track);
            TrackPersisted?.Invoke(this, track);
        }
        catch (BilibiliSourceException exception)
        {
            HasError = true;
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-resolves the track that is currently loaded, used when a stream outlived its URL.
    /// </summary>
    /// <param name="track">Stream track that failed.</param>
    /// <returns>The freshly resolved replacement, or <see langword="null"/> when it cannot be refreshed.</returns>
    public async Task<Track?> TryRefreshAsync(Track track)
    {
        if (!track.IsStream)
        {
            return null;
        }

        if (VideoId.FromStreamKey(track.StreamKey) is not { } bvid)
        {
            return null;
        }

        try
        {
            Track refreshed = await ResolveTrackAsync(bvid);

            StatusMessage = $"Reloaded {refreshed.Title} after the link expired.";
            HasError = false;

            TrackPersisted?.Invoke(this, refreshed);

            return refreshed;
        }
        catch (BilibiliSourceException exception)
        {
            HasError = true;
            StatusMessage = exception.Message;

            return null;
        }
    }

    /// <summary>
    /// Resolves <paramref name="bvid"/> into a playable stream track.
    /// </summary>
    /// <param name="bvid">Video id to resolve.</param>
    /// <returns>A track carrying the video title, uploader, thumbnail and length.</returns>
    /// <exception cref="BilibiliSourceException">The bridge is missing, the id is wrong, or the CDN refused.</exception>
    private async Task<Track> ResolveTrackAsync(string bvid)
    {
        if (!await _source.ProbeAsync())
        {
            throw new BilibiliSourceException(
                "The local bridge is not running. Start it with “node bridge.cjs” and try again."
            );
        }

        ResolvedMedia media = await _source.ResolveAsync(bvid);

        string streamKey = $"bilibili:{media.Bvid}";

        // Archive whatever subtitles came back before building the track. The store ignores an
        // empty list, so a video whose captions are hidden behind a login keeps the copy that was
        // archived on an earlier play.
        await _captions.SaveAsync(streamKey, media.Captions);

        // The metadata endpoint reports the video length, which is the length of the audio the user
        // is about to hear. The segment probe is only a fallback: a fragmented stream declares a
        // zero duration in its header.
        TimeSpan duration =
            media.Duration > TimeSpan.Zero
                ? media.Duration
                : await DashSegmentProbe.TryReadDurationAsync(
                    media.AudioUri,
                    referer: "https://www.bilibili.com/"
                ) ?? TimeSpan.Zero;

        // Title and uploader are shown as they are: a video title is not a song title and the
        // uploader is not necessarily the artist, so the UI labels them with a source chip rather
        // than pretending they are tags.
        return new Track(
            media.Title,
            media.Uploader,
            duration,
            Album: null,
            StreamKey: streamKey,
            StreamUri: media.AudioUri.AbsoluteUri,
            CoverUri: media.CoverUri?.AbsoluteUri
        );
    }
}
