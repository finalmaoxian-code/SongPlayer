using System.Windows;
using System.Windows.Controls;
using SongPlayer.ViewModels;

namespace SongPlayer.Views.Shell;

/// <summary>
/// Bottom player bar: now playing summary, transport controls, seek bar and volume.
/// </summary>
/// <remarks>
/// The one piece of behaviour here exists because drag state belongs to the control, not to the
/// playback model: while the user holds the seek bar, the engine's position reports have to stop
/// moving the thumb, and on release the engine has to seek once to the chosen position.
/// </remarks>
public partial class PlayerBarView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PlayerBarView()
    {
        InitializeComponent();

        SeekBar.DraggingChanged += OnSeekBarDraggingChanged;
    }

    /// <summary>
    /// Forwards the drag state to the view model, which suspends engine updates while dragging
    /// and issues a single seek when the user lets go.
    /// </summary>
    private void OnSeekBarDraggingChanged(object? sender, bool isDragging)
    {
        if (DataContext is MainWindowViewModel shell)
        {
            shell.PlayerBar.SetSeeking(isDragging);
        }
    }
}
