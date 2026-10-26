using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SongPlayer.Models;
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
    /// Clearing the selection when the track is not in the library (after "Clear library") matters
    /// as much as selecting it: a stale highlighted row would suggest a track that is gone.
    /// </remarks>
    private void SyncSelection()
    {
        if (_shell is null || TrackList is null)
        {
            return;
        }

        string? path = _shell.PlayerBar.CurrentTrack.FilePath;

        _isSyncingSelection = true;

        try
        {
            TrackList.SelectedItem = path is null
                ? null
                : _shell.Library.Tracks.FirstOrDefault(track => track.FilePath == path);
        }
        finally
        {
            _isSyncingSelection = false;
        }
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
}
