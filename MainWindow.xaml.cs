using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SongPlayer.Interop;
using SongPlayer.ViewModels;

namespace SongPlayer;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// The window is a thin shell: navigation and player state live in
/// <see cref="ViewModels.MainWindowViewModel"/>, so this file only contains the few
/// behaviours that genuinely belong to the window chrome or to pure view concerns.
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // A borderless window would otherwise be maximised past the work area, which pushes
        // the player bar underneath the taskbar.
        WindowMaximizeHelper.Attach(this);

        // Keep the maximize/restore glyph in sync with the actual window state.
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        UpdateMaximizeGlyph();

        // The lyric list is a pure view concern: the view model only publishes which line is
        // active, the window decides how to keep it visible.
        DataContextChanged += OnShellDataContextChanged;
        AttachToViewModel(DataContext as MainWindowViewModel);
    }

    /// <summary>
    /// Escape closes the immersive Now Playing view, matching the usual expectation for a
    /// full-window overlay. Handled at preview time so a focused lyric row cannot swallow it.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape && _viewModel is { IsNowPlayingOpen: true })
        {
            _viewModel.CloseNowPlayingCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnShellDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachToViewModel(e.NewValue as MainWindowViewModel);

    /// <summary>
    /// Swaps the property-change subscription over to the current view model.
    /// </summary>
    private void AttachToViewModel(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// Scrolls the active lyric line into view whenever the karaoke highlight moves.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.ActiveLyricIndex) || _viewModel is null)
        {
            return;
        }

        var index = _viewModel.ActiveLyricIndex;

        // Defer to Loaded priority so newly realised containers have a valid position.
        Dispatcher.BeginInvoke(
            new Action(() => ScrollActiveLyricIntoView(index)),
            DispatcherPriority.Loaded);
    }

    private void ScrollActiveLyricIntoView(int index)
    {
        if (index < 0 || index >= LyricList.Items.Count)
        {
            return;
        }

        if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    /// <summary>
    /// Seeks the track to the moment the clicked lyric line starts.
    /// </summary>
    private void LyricLine_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: LyricLineViewModel line } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SeekToLyricCommand.Execute(line);
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>
    /// Toggles between maximized and restored, matching the behaviour of the native caption.
    /// </summary>
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Swaps the caption glyph between "maximize" (\uE922) and "restore" (\uE923).
    /// The immersive overlay carries its own caption buttons, so both are kept in sync.
    /// </summary>
    private void UpdateMaximizeGlyph()
    {
        var glyph = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        var tooltip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";

        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = glyph;
            MaximizeButton.ToolTip = tooltip;
        }

        if (OverlayMaximizeButton is not null)
        {
            OverlayMaximizeButton.Content = glyph;
            OverlayMaximizeButton.ToolTip = tooltip;
        }
    }
}
