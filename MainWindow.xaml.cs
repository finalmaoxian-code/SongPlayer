using System.Windows;
using System.Windows.Input;
using SongPlayer.Interop;
using SongPlayer.ViewModels;

namespace SongPlayer;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// The window is a thin shell: navigation and player state live in
/// <see cref="ViewModels.MainWindowViewModel"/>, the composed views own their own chrome
/// behaviour, so this file only carries what genuinely belongs to the window itself.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // A borderless window would otherwise be maximised past the work area, which pushes
        // the player bar underneath the taskbar.
        WindowMaximizeHelper.Attach(this);
    }

    /// <summary>
    /// Escape closes the immersive Now Playing view, matching the usual expectation for a
    /// full-window overlay. Handled at preview time so a focused lyric row cannot swallow it.
    /// </summary>
    /// <remarks>
    /// The state is read through <see cref="Window.DataContext"/> rather than a cached field:
    /// the shell view model is supplied by the window itself or by the composition root, and
    /// the window should not care which.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape &&
            DataContext is MainWindowViewModel { IsNowPlayingOpen: true } viewModel)
        {
            viewModel.CloseNowPlayingCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Clicking the caption bar must not steal focus from whatever the user was typing in.
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // WindowChrome already handles dragging, this only prevents a focus jump.
        if (e.Handled)
        {
            return;
        }

        Focus();
    }
}
