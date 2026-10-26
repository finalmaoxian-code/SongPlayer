using System.Windows.Controls;

namespace SongPlayer.Views.Shell;

/// <summary>
/// Shared page header: title, subtitle and the library search field. It sits above the page
/// host so both pages read the same heading area.
/// </summary>
public partial class PageHeaderView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PageHeaderView() => InitializeComponent();
}
