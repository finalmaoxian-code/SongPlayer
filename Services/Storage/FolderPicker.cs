using Microsoft.Win32;

namespace SongPlayer.Services.Storage;

/// <summary>
/// <see cref="IFolderPicker"/> backed by the standard Windows folder dialog.
/// </summary>
/// <remarks>
/// The modern <c>OpenFolderDialog</c> (available since .NET 8) is used rather than
/// <c>FolderBrowserDialog</c> from WinForms: it is the same picker Explorer shows, it supports the
/// address bar, favourites and multi-monitor DPI, and it needs no extra assembly reference.
/// </remarks>
public sealed class FolderPicker : IFolderPicker
{
    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,

            // Opening on the music folder is almost always what the user wants first time round.
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
            ? dialog.FolderName
            : null;
    }
}
