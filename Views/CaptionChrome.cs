using System.Windows;
using System.Windows.Controls;

namespace SongPlayer.Views;

/// <summary>
/// Window chrome behaviour shared by every view that draws its own caption buttons.
/// </summary>
/// <remarks>
/// The caption strip lives inside a <see cref="UserControl"/> now, so the buttons cannot call
/// handlers on the window directly and the window cannot push the restore glyph into them by
/// name. Instead each view attaches its maximize button here: the helper finds the hosting
/// window, follows its <see cref="Window.StateChanged"/> event and keeps the glyph in sync on
/// its own. That keeps the two captions (shell + Now Playing overlay) independent — neither
/// has to know the other exists.
/// </remarks>
internal static class CaptionChrome
{
    /// <summary>
    /// Maximize glyph of <c>Segoe MDL2 Assets</c>.
    /// </summary>
    private const string MaximizeGlyph = "\uE922";

    /// <summary>
    /// Restore glyph of <c>Segoe MDL2 Assets</c>, shown while the window is maximized.
    /// </summary>
    private const string RestoreGlyph = "\uE923";

    /// <summary>
    /// Keeps <paramref name="maximizeButton"/> showing the glyph that matches the window state.
    /// </summary>
    /// <param name="maximizeButton">Button that toggles maximize/restore.</param>
    public static void Attach(Button maximizeButton)
    {
        ArgumentNullException.ThrowIfNull(maximizeButton);

        maximizeButton.Loaded += (_, _) => Subscribe();
        maximizeButton.Unloaded += (_, _) => Unsubscribe();

        // The button is usually already loaded by the time the view is constructed from
        // code-behind, so hook immediately as well; subscribing twice is harmless because
        // the handler is removed before it is added.
        if (maximizeButton.IsLoaded)
        {
            Subscribe();
        }

        void Subscribe()
        {
            if (Window.GetWindow(maximizeButton) is not { } window)
            {
                return;
            }

            window.StateChanged -= OnStateChanged;
            window.StateChanged += OnStateChanged;
            Apply(maximizeButton, window.WindowState);
        }

        void Unsubscribe()
        {
            if (Window.GetWindow(maximizeButton) is { } window)
            {
                window.StateChanged -= OnStateChanged;
            }
        }

        void OnStateChanged(object? sender, EventArgs e)
        {
            if (sender is Window window)
            {
                Apply(maximizeButton, window.WindowState);
            }
        }
    }

    /// <summary>Minimizes the window that hosts <paramref name="sender"/>.</summary>
    /// <param name="sender">Element inside the window to minimize.</param>
    public static void Minimize(object sender)
    {
        if (Window.GetWindow(sender as DependencyObject) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    /// <summary>Toggles the hosting window between maximized and restored.</summary>
    /// <param name="sender">Element inside the window to resize.</param>
    public static void ToggleMaximize(object sender)
    {
        if (Window.GetWindow(sender as DependencyObject) is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>Closes the window that hosts <paramref name="sender"/>.</summary>
    /// <param name="sender">Element inside the window to close.</param>
    public static void Close(object sender) => Window.GetWindow(sender as DependencyObject)?.Close();

    /// <summary>
    /// Swaps the glyph and the tooltip to match <paramref name="state"/>.
    /// </summary>
    private static void Apply(Button button, WindowState state)
    {
        var isMaximized = state == WindowState.Maximized;

        button.Content = isMaximized ? RestoreGlyph : MaximizeGlyph;
        button.ToolTip = isMaximized ? "Restore" : "Maximize";
    }
}
