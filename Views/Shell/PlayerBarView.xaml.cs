using System.Windows.Controls;

namespace SongPlayer.Views.Shell;

/// <summary>
/// Bottom player bar: now playing summary, transport controls, seek bar and volume.
/// </summary>
public partial class PlayerBarView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PlayerBarView() => InitializeComponent();
}
