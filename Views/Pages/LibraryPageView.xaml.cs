using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SongPlayer.Models;
using SongPlayer.Services.Bilibili;
using SongPlayer.ViewModels;
using SongPlayer.ViewModels.Shell;

namespace SongPlayer.Views.Pages;

/// <summary>
/// Local Music page. The bindings do almost all of the work; the code-behind only translates the
/// list selection into playback and keeps that selection in sync with the loaded track.
/// </summary>
public partial class LibraryPageView : UserControl
{
    private MainWindowViewModel? _shell;
    private bool _isSyncingSelection;

    /// <summary>
    /// Creates the view.
    /// </summary>
    public LibraryPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Subscribes to the shell so the selection follows the loaded track, no matter how the track
    /// was loaded (row selection, or any other entry point added later).
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PlayerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
        }

        _shell = e.NewValue as MainWindowViewModel;

        if (_shell is not null)
        {
            _shell.PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
            SyncSelection();
        }
    }

    /// <summary>
    /// Reflects the loaded track into the list selection.
    /// </summary>
    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.CurrentTrack))
        {
            SyncSelection();
        }
    }

    /// <summary>
    /// Selects the row of the loaded track without starting playback a second time.
    /// </summary>
    /// <remarks>
    /// Matching is by identity rather than by object reference, because the mediator can replace
    /// the track object it hands to the player bar: an online track is re-resolved before it is
    /// loaded, so the row in the list and the loaded track are two instances of the same source.
    /// A file is matched by path and an online source by stream key, which is the same rule the
    /// store uses to de-duplicate.
    /// <para>
    /// Clearing the selection when the track is not in the library (after "Clear library") matters
    /// as much as selecting it: a stale highlighted row would suggest a track that is gone.
    /// </para>
    /// </remarks>
    private void SyncSelection()
    {
        if (_shell is null || TrackList is null)
        {
            return;
        }

        _isSyncingSelection = true;

        try
        {
            TrackList.SelectedItem = FindRow(_shell.PlayerBar.CurrentTrack);
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    /// <summary>
    /// Finds the row that represents <paramref name="track"/>.
    /// </summary>
    /// <param name="track">Loaded track.</param>
    /// <returns>The matching row, or <see langword="null"/> when the track is not in the list.</returns>
    private Track? FindRow(Track track)
    {
        if (_shell is null)
        {
            return null;
        }

        foreach (Track row in _shell.Library.Tracks)
        {
            bool matches = track.FilePath is { Length: > 0 } path
                ? string.Equals(row.FilePath, path, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(track.StreamKey)
                    && string.Equals(row.StreamKey, track.StreamKey, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the row the user picked. Selection is the single gesture, so mouse clicks and keyboard
    /// navigation behave identically.
    /// </summary>
    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        if (TrackList.SelectedItem is Track track)
        {
            shell.Library.PlayTrackCommand.Execute(track);
        }
    }

    /// <summary>
    /// Resolves and plays the candidate the user picked from the search list.
    /// </summary>
    /// <remarks>
    /// Selecting a candidate is the confirmation step: the list exists precisely because the app
    /// cannot tell which upload is the song, so a click here means "that one, play it". The row is
    /// deselected immediately afterwards so the same upload can be picked again after it fails —
    /// a stale selection would swallow the second click.
    /// </remarks>
    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        if (sender is not ListBox list || list.SelectedItem is not BilibiliSearchResult candidate)
        {
            return;
        }

        list.SelectedItem = null;

        if (shell.Bilibili.PlayCandidateCommand.CanExecute(candidate))
        {
            shell.Bilibili.PlayCandidateCommand.Execute(candidate);
        }
    }
}
