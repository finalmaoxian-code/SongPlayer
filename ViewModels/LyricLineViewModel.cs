using CommunityToolkit.Mvvm.ComponentModel;
using SongPlayer.Models;

namespace SongPlayer.ViewModels;

/// <summary>
/// One row of the lyric list on the Now Playing page.
/// </summary>
/// <remarks>
/// <see cref="IsActive"/> is observable so the row can highlight itself through a
/// <c>DataTrigger</c>. Keeping the flag on the row (instead of reselecting the whole list)
/// means a playback tick only touches two rows: the one losing the highlight and the one
/// gaining it.
/// </remarks>
public partial class LyricLineViewModel : ObservableObject
{
    /// <summary>
    /// Creates a row from a timed lyric line.
    /// </summary>
    /// <param name="line">Source line produced by the lyric source.</param>
    public LyricLineViewModel(LyricLine line)
    {
        Time = line.Time;
        Text = line.Text;
    }

    /// <summary>
    /// Moment inside the track at which this line starts. Also the seek target when the
    /// user clicks the row.
    /// </summary>
    public TimeSpan Time { get; }

    /// <summary>
    /// Text shown to the user.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// True while this is the line currently being sung.
    /// </summary>
    [ObservableProperty]
    private bool isActive;
}
