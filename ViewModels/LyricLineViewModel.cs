using CommunityToolkit.Mvvm.ComponentModel;
using SongPlayer.Models;

namespace SongPlayer.ViewModels;

/// <summary>
/// One row of the lyric list: a timed line and whether it is the line being sung.
/// </summary>
/// <remarks>
/// The row is a view model rather than the model itself because "this line is active" changes dozens of
/// times per song, and rebuilding the collection for every change would throw away the list's scroll
/// position and its item containers.
/// </remarks>
public partial class LyricLineViewModel : ObservableObject
{
    /// <summary>Creates a row from a parsed line.</summary>
    /// <param name="line">Timed line to show.</param>
    public LyricLineViewModel(LyricLine line)
    {
        Time = line.Time;
        Text = line.Text;
    }

    /// <summary>Time the line starts, as written in the file.</summary>
    public TimeSpan Time { get; }

    /// <summary>Line text, which may hold an original plus its translation.</summary>
    public string Text { get; }

    /// <summary>True while the playback position is inside this line.</summary>
    [ObservableProperty]
    private bool isActive;
}

/// <summary>
/// One row of the "choose the right lyrics" list.
/// </summary>
/// <remarks>
/// A candidate is a whole record, so it carries enough detail to tell two versions of the same song
/// apart: performer, album, length and whether the timing exists at all.
/// </remarks>
public sealed class LyricCandidateViewModel
{
    /// <summary>Creates a row from a matcher candidate.</summary>
    /// <param name="candidate">Candidate to show.</param>
    /// <param name="select">Action invoked when the row is chosen.</param>
    public LyricCandidateViewModel(LyricCandidate candidate, Action<LyricCandidateViewModel> select)
    {
        Candidate = candidate;
        SelectCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => select(this));
    }

    /// <summary>The underlying candidate.</summary>
    public LyricCandidate Candidate { get; }

    /// <summary>Song name of the record.</summary>
    public string Title => Candidate.TrackName;

    /// <summary>Artist of the record, or a neutral label.</summary>
    public string Artist => Candidate.ArtistText;

    /// <summary>Album of the record, or an em dash.</summary>
    public string Album => Candidate.AlbumText;

    /// <summary>Length of the record, or an em dash.</summary>
    public string Duration => Candidate.DurationText;

    /// <summary>Whether the record is synced, plain or instrumental.</summary>
    public string TypeLabel => Candidate.TypeLabel;

    /// <summary>
    /// Album, length and type on one line, ready for a single <c>TextBlock</c>.
    /// </summary>
    /// <remarks>
    /// It is one property instead of three <c>Run</c> bindings on purpose. <c>Run.Text</c> binds
    /// two-way by default, so binding it to a read-only property throws
    /// <c>XamlParseException</c> while the item template is being realised — which means an
    /// unhandled exception and a terminated process the first time a candidate row is drawn, not a
    /// silent binding warning. Keeping the whole line in one view-model string removes the trap
    /// instead of remembering to annotate every binding.
    /// </remarks>
    public string Detail => $"{Album} · {Duration} · {TypeLabel}";

    /// <summary>Match score, shown so the ordering is explainable.</summary>
    public string ScoreText => $"{Candidate.Score:0}";

    /// <summary>Invoked when this row is picked.</summary>
    public CommunityToolkit.Mvvm.Input.IRelayCommand SelectCommand { get; }
}
