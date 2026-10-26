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
    /// True when the loaded track has at least one lyric line, which decides whether the
    /// list or the empty hint is shown.
    /// </summary>
    public bool HasLyrics => Lyrics.Count > 0;

    /// <summary>
    /// Rebuilds the lyric rows for a newly loaded track and resets the highlight.
    /// </summary>
    /// <param name="track">Track whose lyrics should be shown.</param>
    public void LoadFor(Track track)
    {
        Lyrics.Clear();

        foreach (var line in _lyricProvider.GetLyrics(track))
        {
            Lyrics.Add(new LyricLineViewModel(line));
        }

        OnPropertyChanged(nameof(HasLyrics));
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
