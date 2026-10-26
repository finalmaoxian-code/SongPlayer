using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SongPlayer.ViewModels;

namespace SongPlayer.Views.Overlays;

/// <summary>
/// Full-window Now Playing overlay: artwork, transport and the karaoke lyric list.
/// </summary>
/// <remarks>
/// The view model only publishes which lyric line is active; keeping that line visible is a
/// pure view concern, so it lives here. Escape handling stays on the window, where preview
/// input reaches it before a focused lyric row can swallow the key.
/// </remarks>
public partial class NowPlayingView : UserControl
{
    private MainWindowViewModel? _viewModel;

    /// <summary>Creates the view and starts tracking the shell view model.</summary>
    public NowPlayingView()
    {
        InitializeComponent();

        // The overlay carries its own caption buttons because it covers the real chrome.
        CaptionChrome.Attach(OverlayMaximizeButton);

        DataContextChanged += OnDataContextChanged;
        AttachToViewModel(DataContext as MainWindowViewModel);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.Minimize(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.ToggleMaximize(this);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.Close(this);
}
