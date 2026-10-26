using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.ViewModels.Shell;

/// <summary>
/// Full-window Now Playing overlay: artwork, transport and the karaoke lyric list.
/// </summary>
/// <remarks>
/// The overlay mirrors the transport state of <see cref="PlayerBarViewModel"/> instead of
/// owning it. Position is pushed in by the shell mediator, so both views always show the same
/// moment of the track and clicking a lyric line seeks the one shared position.
/// </remarks>
public partial class NowPlayingViewModel : ObservableObject
{
    /// <summary>
    /// Source of the timed lyrics for the loaded track.
    /// </summary>
    private readonly ILyricProvider _lyricProvider;

    /// <summary>
    /// Creates the overlay view model for the sample lyrics.
    /// </summary>
    /// <remarks>
    /// The parameterless overload is what XAML uses for design-time and shell instantiation;
    /// the composition root passes the real provider.
    /// </remarks>
    public NowPlayingViewModel()
        : this(new SampleLyricProvider())
    {
    }

    /// <summary>
    /// Creates the overlay view model.
    /// </summary>
    /// <param name="lyricProvider">Source of the timed lyrics shown on the overlay.</param>
    public NowPlayingViewModel(ILyricProvider lyricProvider)
    {
        _lyricProvider = lyricProvider;
    }

    /// <summary>
    /// Timed lyrics of the loaded track, projected into rows the view can bind to.
    /// </summary>
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = new();

    /// <summary>
    /// Index of the highlighted lyric line, or -1 while the track has not reached the first
    /// line yet. The view listens to this to scroll the active line into view.
    /// </summary>
    [ObservableProperty]
    private int activeLyricIndex = -1;

    /// <summary>
    /// Track whose lines are currently loaded, used to word the labels honestly.
    /// </summary>
    /// <remarks>
    /// Kept separately instead of reading through a shell reference: the overlay mirrors the player
    /// bar rather than observing it, and the wording only has to be rebuilt when a load happens.
    /// </remarks>
    private Track _loadedTrack = Track.Empty;

    /// <summary>
    /// True when the loaded track has at least one lyric line, which decides whether the
    /// list or the empty hint is shown.
    /// </summary>
    public bool HasLyrics => Lyrics.Count > 0;

    /// <summary>
    /// Header label for the line list: lyrics for local files, subtitles for online videos.
    /// </summary>
    /// <remarks>
    /// The distinction is not pedantry. A subtitle track is a transcript of whatever the video
    /// says — an intro, a spoken word, a machine transcript — and calling it "lyrics" would claim
    /// a correctness the source never promised.
    /// </remarks>
    public string SectionLabel => _loadedTrack.IsStream ? "SUBTITLES" : "LYRICS";

    /// <summary>
    /// One-line provenance note printed under the line list.
    /// </summary>
    /// <remarks>
    /// Empty for local tracks, whose lines (once a lyric reader exists) come from a file the user
    /// controls. For online tracks it warns that the timing and wording are the uploader's or a
    /// speech model's, not the song's.
    /// </remarks>
    public string SourceNotice =>
        _loadedTrack.IsStream ? "Timed by the video's subtitle track, not by the song." : string.Empty;

    /// <summary>
    /// True when <see cref="SourceNotice"/> has something to say.
    /// </summary>
    /// <remarks>
    /// Also requires lines to exist: the note explains where the highlighted lines came from, so
    /// printing it under an empty state would describe a transcript the user is not looking at.
    /// </remarks>
    public bool HasSourceNotice => HasLyrics && !string.IsNullOrEmpty(SourceNotice);

    /// <summary>
    /// Headline of the empty state.
    /// </summary>
    public string EmptyTitle => _loadedTrack.IsStream
        ? "No subtitles for this video"
        : "No lyrics for this track";

    /// <summary>
    /// Explanation under the empty-state headline.
    /// </summary>
    /// <remarks>
    /// The online wording describes the real cause: Bilibili only serves most subtitle tracks to a
    /// signed-in session, so "nothing here" usually means "not shared", not "this video has none".
    /// The local wording no longer promises <c>.lrc</c> support, because the reader does not exist.
    /// </remarks>
    public string EmptyHint => _loadedTrack.IsStream
        ? "Bilibili only hands out most subtitle tracks to a signed-in session, so a video may well have them without sharing them here."
        : "Reading an .lrc file next to the audio file is not implemented yet.";

    /// <summary>
    /// Rebuilds the lyric rows for a newly loaded track and resets the highlight.
    /// </summary>
    /// <param name="track">Track whose lyrics should be shown.</param>
    public void LoadFor(Track track)
    {
        Lyrics.Clear();

        _loadedTrack = track;

        foreach (var line in _lyricProvider.GetLyrics(track))
        {
            Lyrics.Add(new LyricLineViewModel(line));
        }

        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(SectionLabel));
        OnPropertyChanged(nameof(SourceNotice));
        OnPropertyChanged(nameof(HasSourceNotice));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        UpdateActiveLyric(TimeSpan.Zero);
    }

    /// <summary>
    /// Moves the karaoke highlight to the line matching <paramref name="position"/>.
    /// </summary>
    /// <param name="position">Current playback position, supplied by the shell mediator.</param>
    public void UpdateActiveLyric(TimeSpan position)
    {
        var activeIndex = -1;

        for (var i = 0; i < Lyrics.Count; i++)
        {
            if (Lyrics[i].Time > position)
            {
                break;
            }

            activeIndex = i;
        }

        // Only the two rows that actually change need a notification.
        for (var i = 0; i < Lyrics.Count; i++)
        {
            Lyrics[i].IsActive = i == activeIndex;
        }

        if (ActiveLyricIndex != activeIndex)
        {
            ActiveLyricIndex = activeIndex;
        }
    }
}
