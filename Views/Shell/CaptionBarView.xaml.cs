using System.Windows;
using System.Windows.Controls;

namespace SongPlayer.Views.Shell;

/// <summary>
/// Custom title bar of the shell: brand on the left, window buttons on the right.
/// </summary>
/// <remarks>
/// The view draws the buttons because the window has no native caption
/// (<c>WindowStyle=None</c>). Hiding it while the Now Playing overlay is open is done here
/// rather than by the host, so the shell layout stays free of overlay knowledge.
/// </remarks>
public partial class CaptionBarView : UserControl
{
    /// <summary>Creates the view and hooks its maximize button to the window chrome.</summary>
    public CaptionBarView()
    {
        InitializeComponent();
        CaptionChrome.Attach(MaximizeButton);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.Minimize(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.ToggleMaximize(this);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CaptionChrome.Close(this);
}
