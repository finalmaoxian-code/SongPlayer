using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Library;
using SongPlayer.Services.Storage;

namespace SongPlayer.ViewModels.Pages;

/// <summary>
/// Local Music page: the imported tracks, folder import, removal and the saved-library restore.
/// </summary>
/// <remarks>
/// The view model owns the collection — the service only moves data in and out of it — so the
/// page is the single place that knows what the library currently contains. Playback is not
/// decided here: selecting a track raises <see cref="TrackActivated"/> and the shell mediator
/// hands it to the player bar, which keeps the playback position single-sourced (see
/// <see cref="MainWindowViewModel"/>).
/// </remarks>
public partial class LibraryPageViewModel : ObservableObject
{
    private readonly IMusicLibraryService _library;
    private readonly IFolderPicker _folderPicker;

    /// <summary>
    /// Creates the page.
    /// </summary>
    /// <param name="library">Library facade used to scan, restore and persist.</param>
    /// <param name="folderPicker">Asks the user which folder to import.</param>
    public LibraryPageViewModel(IMusicLibraryService library, IFolderPicker folderPicker)
    {
        _library = library;
        _folderPicker = folderPicker;
    }

    /// <summary>
    /// Raised when a track should start playing.
    /// </summary>
    /// <remarks>
    /// An event instead of a direct call keeps this page free of any reference to the player bar.
    /// </remarks>
    public event EventHandler<Track>? TrackActivated;

    /// <summary>
    /// Imported tracks, in the order they were imported.
    /// </summary>
    public ObservableCollection<Track> Tracks { get; } = [];

    /// <summary>
    /// True while a scan or a restore is running; disables the import buttons.
    /// </summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>
    /// Last user-facing status: what was imported, or why nothing could be imported.
    /// </summary>
    /// <remarks>
    /// A status line instead of a modal dialog: import failures are frequent and ordinary
    /// (permissions, empty folders, unsupported formats) and should not interrupt the user.
    /// </remarks>
    [ObservableProperty]
    private string statusMessage = "Import a folder to build your library.";

    /// <summary>
    /// True when at least one track is loaded, used to switch between the table and the empty state.
    /// </summary>
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>
    /// True when the collection is empty, bound to the empty-state card.
    /// </summary>
    public bool HasNoTracks => !HasTracks;

    /// <summary>
    /// Toolbar summary, e.g. <c>12 tracks · 2 albums</c>.
    /// </summary>
    public string LibrarySummary
    {
        get
        {
            if (Tracks.Count == 0)
            {
                return "No tracks yet";
            }

            int albums = Tracks
                .Where(track => !string.IsNullOrWhiteSpace(track.Album))
                .Select(track => track.Album!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            string trackLabel = Tracks.Count == 1 ? "1 track" : $"{Tracks.Count} tracks";
            string albumLabel = albums switch
            {
                0 => "no album tags",
                1 => "1 album",
                _ => $"{albums} albums",
            };

            return $"{trackLabel} · {albumLabel}";
        }
    }

    /// <summary>
    /// True when the summary is worth showing, i.e. once something has been imported.
    /// </summary>
    public bool HasSummary => HasTracks;

    /// <summary>
    /// Restores the library that a previous session saved.
    /// </summary>
    /// <remarks>
    /// Called once by the composition root after the shell exists, deliberately not from the
    /// constructor: the restore touches the disk, and a constructor that awaits IO makes the
    /// object graph awkward to build and impossible to preview.
    /// </remarks>
    public async Task RestoreAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Restoring your library…";

        try
        {
            IReadOnlyList<Track> tracks = await _library.LoadAsync();

            ReplaceTracks(tracks);

            StatusMessage = tracks.Count == 0
                ? "Import a folder to build your library."
                : $"Restored {tracks.Count} {Plural(tracks.Count, "track")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The saved file list could not be read. Not fatal: the user can simply import again.
            StatusMessage = "The saved library could not be read. Import a folder again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Asks for a folder and imports every supported audio file it contains.
    /// </summary>
    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (IsBusy)
        {
            return;
        }

        string? folder = _folderPicker.PickFolder("Choose a folder to import");

        if (folder is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Scanning…";

        try
        {
            IReadOnlyList<Track> scanned = await _library.ScanAsync(folder);

            if (scanned.Count == 0)
            {
                StatusMessage = "No supported audio files in that folder.";
                return;
            }

            // Importing the same folder twice must not duplicate rows, so new paths are merged
            // into the existing collection instead of replacing it.
            var knownPaths = new HashSet<string>(
                Tracks.Select(track => track.FilePath ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;

            foreach (Track track in scanned)
            {
                if (track.FilePath is { } path && knownPaths.Add(path))
                {
                    Tracks.Add(track);
                    added++;
                }
            }

            OnTracksChanged();

            if (added == 0)
            {
                StatusMessage = "Those tracks are already in your library.";
                return;
            }

            await _library.SaveAsync(Tracks);

            StatusMessage = $"Imported {added} {Plural(added, "track")} "
                + $"from {Path.GetFileName(folder)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "That folder could not be read.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Removes every imported track and deletes the saved library.
    /// </summary>
    /// <remarks>
    /// Only the index is deleted; the audio files on disk are never touched.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasTracks))]
    private async Task ClearLibraryAsync()
    {
        Tracks.Clear();
        OnTracksChanged();

        await _library.SaveAsync(Tracks);

        StatusMessage = "Library cleared. Import a folder to start again.";
    }

    /// <summary>
    /// Replaces the collection contents, used by the restore path.
    /// </summary>
    /// <param name="tracks">Tracks to show.</param>
    private void ReplaceTracks(IReadOnlyList<Track> tracks)
    {
        Tracks.Clear();

        foreach (Track track in tracks)
        {
            Tracks.Add(track);
        }

        OnTracksChanged();
    }

    /// <summary>
    /// Plays a track. Bound to the row click / double-click in the table.
    /// </summary>
    /// <param name="track">Row that was activated.</param>
    [RelayCommand]
    private void PlayTrack(Track? track)
    {
        if (track is null)
        {
            return;
        }

        TrackActivated?.Invoke(this, track);
    }

    /// <summary>
    /// Raises everything derived from the collection.
    /// </summary>
    private void OnTracksChanged()
    {
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(HasNoTracks));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(HasSummary));
        ClearLibraryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Picks the singular or plural form without a lookup table.
    /// </summary>
    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
}
