using System.Windows.Controls;

namespace SongPlayer.Views.Shell;

/// <summary>
/// Sidebar navigation. The checked state of each item is bound two-way to the shell view
/// model, so activating an item navigates and the highlight can never drift from the page.
/// </summary>
public partial class SidebarView : UserControl
{
    /// <summary>Creates the view.</summary>
    public SidebarView() => InitializeComponent();
}
