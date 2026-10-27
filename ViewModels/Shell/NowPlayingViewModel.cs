using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SongPlayer.Models;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.ViewModels.Shell;

/// <summary>
/// Full-window Now Playing overlay: artwork, transport and the lyric card.
/// </summary>
/// <remarks>
/// <para>
/// The overlay mirrors the transport state of <see cref="PlayerBarViewModel"/> instead of owning it.
/// Position is pushed in by the shell mediator, so both views always show the same moment of the track
/// and clicking a lyric line seeks the one shared position.
/// </para>
/// <para>
/// Lyrics are looked up asynchronously and every load carries a version number. A result that arrives
/// after the user has moved to another track is dropped on arrival rather than trusted, because a
/// cancellation token can only ask a request to stop — it cannot stop one that has already completed.
/// </para>
/// </remarks>
public partial class NowPlayingViewModel : ObservableObject
{
    /// <summary>Amount a single offset nudge moves the lyric timeline.</summary>
    private static readonly TimeSpan OffsetStep = TimeSpan.FromMilliseconds(500);

    /// <summary>Largest offset the nudge commands will build up, so the text cannot say "+40 min".</summary>
    private const int MaxOffsetMilliseconds = 30_000;

    /// <summary>Source of the lyrics for the loaded track.</summary>
    private readonly ILyricProvider _lyricProvider;

    /// <summary>Cancels the lookup in flight when a new track replaces it.</summary>
    private CancellationTokenSource? _loadCancellation;

    /// <summary>Increments on every load; only the newest version may publish a result.</summary>
    private int _loadVersion;

    /// <summary>Creates the overlay view model for the design-time preview.</summary>
    /// <remarks>
    /// The parameterless overload is what XAML uses for design-time and shell instantiation; the
    /// composition root passes the real provider. It deliberately never touches the network.
    /// </remarks>
    public NowPlayingViewModel()
        : this(new DesignTimeLyricProvider())
    {
    }

    /// <summary>Creates the overlay view model.</summary>
    /// <param name="lyricProvider">Source of the lyrics shown on the overlay.</param>
    public NowPlayingViewModel(ILyricProvider lyricProvider)
    {
        _lyricProvider = lyricProvider;
    }

    /// <summary>Timed lyric lines of the loaded track, projected into rows the view binds to.</summary>
    public ObservableCollection<LyricLineViewModel> Lyrics { get; } = new();

    /// <summary>Records the matcher could not choose between, for the manual picker.</summary>
    public ObservableCollection<LyricCandidateViewModel> Candidates { get; } = new();

    /// <summary>
    /// Index of the highlighted line, or -1 while the track has not reached the first line yet.
    /// The view listens to this to scroll the active line into view.
    /// </summary>
    [ObservableProperty]
    private int activeLyricIndex = -1;

    /// <summary>True while a lookup is in flight for the loaded track.</summary>
    [ObservableProperty]
    private bool isLoading;

    /// <summary>User offset in milliseconds; positive shows lyrics later. See <see cref="LyricOffset"/>.</summary>
    [ObservableProperty]
    private int lyricOffsetMilliseconds;

    /// <summary>View of the right-hand card: the visualiser by default, the lyric card on request.</summary>
    [ObservableProperty]
    private OverlayView activeView = OverlayView.Visualizer;

    /// <summary>Track whose lyrics are loaded, used to word the labels honestly.</summary>
    private Track _loadedTrack = Track.Empty;

    /// <summary>Last result applied to this view model, or null before the first answer.</summary>
    private LyricLookupResult? _result;

    /// <summary>
    /// True while the overlay may still switch itself to the lyrics card once a result arrives.
    /// </summary>
    /// <remarks>
    /// Cleared the moment the user picks the visualiser during a load. A late result must not steal a
    /// view the user asked for; the card is theirs to choose, the automatic switch is only a courtesy.
    /// </remarks>
    private bool _autoSwitchToLyrics;

    /// <summary>Current playback position, remembered so a late result can highlight the right line.</summary>
    private TimeSpan _position = TimeSpan.Zero;

    /// <summary>The lyric timeline offset applied to highlighting and seeking.</summary>
    /// <remarks>
    /// Convention: <c>lyricTime = playbackTime - userOffset</c>. A positive offset therefore shows
    /// lyrics later — a line written at 10 s is highlighted at 15 s — which is what "the lyrics are
    /// ahead of the audio, push them back" means to the person clicking the button. Clicking a line
    /// applies the same arithmetic in reverse, so the highlight and the seek always agree.
    /// </remarks>
    public TimeSpan LyricOffset => TimeSpan.FromMilliseconds(LyricOffsetMilliseconds);

    /// <summary>Readable form of the offset, for the nudge controls.</summary>
    public string LyricOffsetText =>
        (LyricOffsetMilliseconds / 1000.0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>True when the timeline has been nudged away from zero.</summary>
    public bool HasLyricOffset => LyricOffsetMilliseconds != 0;

    /// <summary>True when at least one timed line exists.</summary>
    public bool HasLyrics => Lyrics.Count > 0;

    /// <summary>True when the record has text but no timing, so it is shown as a block of prose.</summary>
    public bool HasPlainText => !HasLyrics && !string.IsNullOrWhiteSpace(_result?.PlainText);

    /// <summary>Plain lyric text of the current result, empty when there is none.</summary>
    public string PlainText => HasPlainText ? _result!.PlainText! : string.Empty;

    /// <summary>True when there is anything at all to read: timed lines, prose, or a candidate list.</summary>
    public bool HasPresentableContent => HasLyrics || HasPlainText || Candidates.Count > 0;

    /// <summary>
    /// True when a lookup has produced something the user might want to replace.
    /// </summary>
    /// <remarks>
    /// This is the correction affordance: whenever lyrics are on screen, there is a control that asks
    /// the database again and forgets any earlier manual pick. It stays out of the way while nothing
    /// has arrived, where the state panel already carries its own retry.
    /// </remarks>
    public bool CanChangeLyrics => HasPresentableContent;

    /// <summary>True when the candidate picker has rows to show.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Tooltip-friendly explanation of how the current record was chosen.</summary>
    public string MatchExplanation => _result?.Explanation ?? string.Empty;

    /// <summary>True when a match explanation is worth printing.</summary>
    public bool HasMatchExplanation => HasLyrics && MatchExplanation.Length > 0;

    /// <summary>One-line status of the lookup, shown while loading and after a failure.</summary>
    public string QueryStatus
    {
        get
        {
            if (IsLoading)
            {
                return "Looking for lyrics…";
            }

            return _result?.Message ?? string.Empty;
        }
    }

    /// <summary>True when <see cref="QueryStatus"/> has something to say.</summary>
    public bool HasQueryStatus => QueryStatus.Length > 0;

    /// <summary>True when the current state can be retried by asking again.</summary>
    public bool CanRetry => !IsLoading && _result?.CanRetry == true;

    /// <summary>Header label of the lyric card, naming the source that actually answered.</summary>
    /// <remarks>
    /// This used to be decided by whether the track was a stream, which meant a Bilibili video with
    /// real LRCLIB lyrics still claimed to be video subtitles. The label now follows the result, so it
    /// never promises a provenance the data does not have.
    /// </remarks>
    public string SectionLabel
    {
        get
        {
            if (_result is { Source: LyricSourceKind.VideoCaptions })
            {
                return "LYRICS · VIDEO CAPTIONS";
            }

            if (_result is { Source: LyricSourceKind.Lrclib })
            {
                return "LYRICS · LRCLIB";
            }

            if (_result is { Source: LyricSourceKind.Sample })
            {
                return "LYRICS · SAMPLE";
            }

            return "LYRICS";
        }
    }

    /// <summary>Provenance sentence printed under the line list.</summary>
    public string SourceNotice => _result switch
    {
        { Source: LyricSourceKind.Lrclib } => "Timed lyrics from LRCLIB, matched to this track automatically.",
        { Source: LyricSourceKind.VideoCaptions } => "Timed by the video's subtitle track, not by the song.",
        { Source: LyricSourceKind.Sample } => "Demo lyrics, shown for the built-in sample track only.",
        _ => string.Empty,
    };

    /// <summary>True when <see cref="SourceNotice"/> has something to say.</summary>
    public bool HasSourceNotice => HasPresentableContent && SourceNotice.Length > 0;

    /// <summary>
    /// Headline of the state panel.
    /// </summary>
    /// <remarks>
    /// A lookup in progress gets its own headline: the panel is the only thing on the card at that
    /// moment, and "no lyrics for this track" would be a claim made before the answer exists.
    /// </remarks>
    public string EmptyTitle => IsLoading
        ? "Looking for lyrics…"
        : _result?.Kind switch
        {
            LyricKind.Instrumental => "This track is instrumental",
            LyricKind.NotFound => "No lyrics found",
            LyricKind.NeedsSelection => "Pick the right lyrics",
            LyricKind.Failed => "Could not reach the lyric database",
            LyricKind.Plain => "Plain lyrics",
            LyricKind.Synced => "No lyrics for this track",
            _ => _loadedTrack.IsStream ? "No lyrics for this video" : "No lyrics for this track",
        };

    /// <summary>Explanation under the state panel headline.</summary>
    /// <remarks>
    /// Each wording names the real cause so the user can tell "the database has nothing" from "the
    /// network is down" from "we are not sure this is the same song". Playback is never affected by any
    /// of these states; the visualiser simply keeps running behind the card.
    /// </remarks>
    public string EmptyHint => IsLoading
        ? "Playback carries on while the request runs."
        : _result?.Kind switch
        {
            LyricKind.Instrumental =>
                "The record is marked instrumental, so there is nothing to sing along to.",
            LyricKind.NotFound =>
                "LRCLIB has no entry for this track. Searching for it under another name sometimes finds one.",
            LyricKind.NeedsSelection =>
                "Several records matched about equally well. Pick the one that belongs to this recording.",
            LyricKind.Failed =>
                "The request failed. Playback is unaffected; try again when the network is back.",
            LyricKind.Plain =>
                "This record has the words but no timing, so no line can be highlighted.",
            _ => "Try another lookup, or keep the visualiser running.",
        };

    /// <summary>True when the visualiser is the visible card.</summary>
    public bool IsVisualizerSelected => ActiveView == OverlayView.Visualizer;

    /// <summary>True when the lyric card is the visible card.</summary>
    public bool IsLyricsSelected => ActiveView == OverlayView.Lyrics;

    /// <summary>True when the timed line list should be shown.</summary>
    public bool ShowsLyricList => IsLyricsSelected && HasLyrics;

    /// <summary>True when the plain-text block should be shown instead of a list.</summary>
    public bool ShowsPlainLyrics => IsLyricsSelected && HasPlainText;

    /// <summary>True when the candidate picker should be shown.</summary>
    public bool ShowsCandidateList => IsLyricsSelected && HasCandidates;

    /// <summary>True when the state panel should be shown: the card is open and has nothing to read.</summary>
    public bool ShowsEmptyLyricState => IsLyricsSelected && !HasPresentableContent;

    /// <summary>
    /// Always true: the lyric card is reachable whether or not lyrics exist yet.
    /// </summary>
    /// <remarks>
    /// The card carries its own loading, "not found", "network failed" and "pick one" states, so
    /// disabling the entry until lyrics exist would hide the only place those states can be read.
    /// </remarks>
    public bool CanShowLyrics => true;

    /// <summary>Opens the visualiser card.</summary>
    /// <remarks>
    /// Also cancels the automatic switch, so lyrics that arrive later stay on their own card instead of
    /// pulling the view away from what the user just chose.
    /// </remarks>
    [RelayCommand]
    private void ShowVisualizer()
    {
        _autoSwitchToLyrics = false;
        ActiveView = OverlayView.Visualizer;
    }

    /// <summary>Opens the lyric card.</summary>
    [RelayCommand]
    private void ShowLyrics()
    {
        _autoSwitchToLyrics = false;
        ActiveView = OverlayView.Lyrics;
    }

    /// <summary>Asks again, ignoring the cache and forgetting a manual choice.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task Retry() => LoadAsync(_loadedTrack, new LyricLookupOptions(ForceRefresh: true, ClearManualChoice: true));

    /// <summary>Shows lyrics earlier against the audio.</summary>
    [RelayCommand]
    private Task OffsetEarlier() => NudgeOffsetAsync(-(int)OffsetStep.TotalMilliseconds);

    /// <summary>Shows lyrics later against the audio.</summary>
    [RelayCommand]
    private Task OffsetLater() => NudgeOffsetAsync((int)OffsetStep.TotalMilliseconds);

    /// <summary>Drops the offset back to zero.</summary>
    [RelayCommand]
    private Task ResetOffset() => NudgeOffsetAsync(-LyricOffsetMilliseconds);

    /// <summary>Re-announces the flags derived from the card selection.</summary>
    partial void OnActiveViewChanged(OverlayView value)
    {
        OnPropertyChanged(nameof(IsVisualizerSelected));
        OnPropertyChanged(nameof(IsLyricsSelected));
        OnPropertyChanged(nameof(ShowsLyricList));
        OnPropertyChanged(nameof(ShowsPlainLyrics));
        OnPropertyChanged(nameof(ShowsCandidateList));
        OnPropertyChanged(nameof(ShowsEmptyLyricState));
    }

    /// <summary>Re-announces the flags derived from the loading flag.</summary>
    /// <remarks>
    /// The state panel wording depends on loading too, so both groups of derived flags are refreshed.
    /// </remarks>
    partial void OnIsLoadingChanged(bool value)
    {
        NotifyLookupDerived();
        NotifyIdentityDerived();
    }

    /// <summary>Re-announces the flags derived from the offset.</summary>
    partial void OnLyricOffsetMillisecondsChanged(int value)
    {
        OnPropertyChanged(nameof(LyricOffset));
        OnPropertyChanged(nameof(LyricOffsetText));
        OnPropertyChanged(nameof(HasLyricOffset));
    }

    /// <summary>
    /// Switches the overlay to a newly loaded track and starts its lyric lookup.
    /// </summary>
    /// <param name="track">Track that is now playing.</param>
    /// <remarks>
    /// Returns immediately. Playback has already started by the time this is called and must never
    /// wait for a network round trip; the card fills in when the answer lands.
    /// </remarks>
    public void LoadFor(Track track)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        _loadVersion++;
        _ = LoadAsync(track, LyricLookupOptions.Default);
    }

    /// <summary>Cancels a lookup in flight, called when the window is closing.</summary>
    public void CancelPendingLookup()
    {
        _loadVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    /// <summary>
    /// Moves the karaoke highlight to the line matching <paramref name="position"/>.
    /// </summary>
    /// <param name="position">Current playback position, supplied by the shell mediator.</param>
    /// <remarks>
    /// Only the rows that changed are touched: with hundreds of lines and a position that ticks several
    /// times a second, notifying every row would keep the whole list dirty.
    /// </remarks>
    public void UpdateActiveLyric(TimeSpan position)
    {
        _position = position;

        if (!HasLyrics)
        {
            if (ActiveLyricIndex != -1)
            {
                ActiveLyricIndex = -1;
            }

            return;
        }

        var lyricPosition = position - LyricOffset;
        var activeIndex = -1;

        for (var i = 0; i < Lyrics.Count; i++)
        {
            if (Lyrics[i].Time > lyricPosition)
            {
                break;
            }

            activeIndex = i;
        }

        for (var i = 0; i < Lyrics.Count; i++)
        {
            Lyrics[i].IsActive = i == activeIndex;
        }

        if (ActiveLyricIndex != activeIndex)
        {
            ActiveLyricIndex = activeIndex;
        }
    }

    /// <summary>
    /// Applies a manual record choice and reloads it.
    /// </summary>
    /// <param name="candidate">Row the user picked.</param>
    private Task ApplyCandidateAsync(LyricCandidateViewModel candidate) =>
        LoadAsync(_loadedTrack, new LyricLookupOptions(RecordId: candidate.Candidate.RecordId));

    /// <summary>
    /// Runs one lookup and publishes it when it is still the newest one.
    /// </summary>
    /// <param name="track">Track being loaded.</param>
    /// <param name="options">Cache, refresh and manual-choice options for this attempt.</param>
    private async Task LoadAsync(Track track, LyricLookupOptions options)
    {
        var version = ++_loadVersion;

        // The caller is the UI thread; it is the only thread allowed to touch the observable
        // collections below. Capturing its dispatcher here makes that a property of this method
        // instead of a lucky accident of where an await happened to resume.
        Dispatcher? owner = Dispatcher.FromThread(Thread.CurrentThread);

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;

        _loadedTrack = track;
        _result = null;
        _autoSwitchToLyrics = true;
        Lyrics.Clear();
        Candidates.Clear();

        IsLoading = true;
        ActiveLyricIndex = -1;
        NotifyLookupDerived();
        NotifyIdentityDerived();

        try
        {
            int offset = await _lyricProvider.GetOffsetMillisecondsAsync(track, cancellationToken);

            await RunOnOwnerAsync(owner, () =>
            {
                if (version == _loadVersion)
                {
                    LyricOffsetMilliseconds = offset;
                }
            });

            LyricLookupResult result = await _lyricProvider.GetLyricsAsync(track, options, cancellationToken);

            await RunOnOwnerAsync(owner, () =>
            {
                if (version == _loadVersion)
                {
                    Apply(track, result);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The user moved on. Nothing to report and nothing to publish.
        }
        catch (Exception exception)
        {
            // A lookup is a convenience; it must never take playback down with it. Anything the
            // provider failed to classify is reported as an ordinary failure state.
            await RunOnOwnerAsync(owner, () =>
            {
                if (version == _loadVersion)
                {
                    Apply(track, LyricLookupResult.Failed(exception.Message));
                }
            });
        }
        finally
        {
            await RunOnOwnerAsync(owner, () =>
            {
                if (version == _loadVersion)
                {
                    IsLoading = false;
                    NotifyLookupDerived();
                }
            });
        }
    }

    /// <summary>
    /// Runs a UI update on the thread that started the load.
    /// </summary>
    /// <remarks>
    /// <see cref="ObservableCollection{T}"/> throws when it is changed from a thread other than the
    /// one that owns its collection view, and a lyric lookup is awaited across arbitrary thread-pool
    /// threads. Marshalling every publish step keeps "UI state changes on the UI thread" true by
    /// construction instead of depending on which thread the continuation happened to resume on.
    /// When there is no context at all (tests, design-time) the action simply runs inline.
    /// </remarks>
    private static Task RunOnOwnerAsync(Dispatcher? owner, Action action)
    {
        if (owner is null || owner.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        // Awaiting the dispatcher operation keeps the ordering of the publish steps, so a result
        // cannot be overwritten by an older one that resumed later.
        return owner.InvokeAsync(action).Task;
    }

    /// <summary>
    /// Publishes a lookup result to the bindable surface.
    /// </summary>
    /// <param name="track">Track the result belongs to.</param>
    /// <param name="result">Result to show.</param>
    private void Apply(Track track, LyricLookupResult result)
    {
        _loadedTrack = track;
        _result = result;

        Lyrics.Clear();
        Candidates.Clear();

        foreach (LyricLine line in result.Lines)
        {
            Lyrics.Add(new LyricLineViewModel(line));
        }

        foreach (LyricCandidate candidate in result.Candidates)
        {
            Candidates.Add(new LyricCandidateViewModel(candidate, chosen => _ = ApplyCandidateAsync(chosen)));
        }

        // A card that opened on the visualiser only switches when the answer is actually readable, and
        // only when the user has not chosen the visualiser in the meantime.
        if (_autoSwitchToLyrics && (Lyrics.Count > 0 || HasPlainText || Candidates.Count > 0))
        {
            ActiveView = OverlayView.Lyrics;
        }

        NotifyLookupDerived();
        NotifyIdentityDerived();

        // A late answer still has to land on the line that is playing right now, not on the first one.
        UpdateActiveLyric(_position);
    }

    /// <summary>Stores a nudged offset and re-highlights from the current position.</summary>
    /// <param name="deltaMilliseconds">Change to apply to the stored offset.</param>
    private async Task NudgeOffsetAsync(int deltaMilliseconds)
    {
        int next = Math.Clamp(
            LyricOffsetMilliseconds + deltaMilliseconds,
            -MaxOffsetMilliseconds,
            MaxOffsetMilliseconds);

        if (next == LyricOffsetMilliseconds)
        {
            return;
        }

        LyricOffsetMilliseconds = next;
        UpdateActiveLyric(_position);

        try
        {
            await _lyricProvider.SetOffsetMillisecondsAsync(_loadedTrack, next);
        }
        catch (Exception)
        {
            // A memory-only offset is still useful this session; failing to persist it must not
            // interrupt anything the user is doing.
        }
    }

    /// <summary>
    /// Converts a lyric line time back into the playback position that should be highlighted for it.
    /// </summary>
    /// <param name="line">Line the user clicked.</param>
    /// <returns>The playback moment the line belongs to, after the offset.</returns>
    /// <remarks>
    /// The inverse of the highlight rule (<c>lyricTime = playbackTime - offset</c>) lives here so the
    /// two directions cannot drift apart: clicking a line lands exactly where highlighting would have
    /// picked it up.
    /// </remarks>
    public TimeSpan PlaybackPositionFor(LyricLineViewModel line) => line.Time + LyricOffset;

    /// <summary>Re-announces the flags that depend on the current result.</summary>
    private void NotifyLookupDerived()
    {
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(HasPlainText));
        OnPropertyChanged(nameof(PlainText));
        OnPropertyChanged(nameof(HasPresentableContent));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(MatchExplanation));
        OnPropertyChanged(nameof(HasMatchExplanation));
        OnPropertyChanged(nameof(QueryStatus));
        OnPropertyChanged(nameof(HasQueryStatus));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanChangeLyrics));
        OnPropertyChanged(nameof(ShowsLyricList));
        OnPropertyChanged(nameof(ShowsPlainLyrics));
        OnPropertyChanged(nameof(ShowsCandidateList));
        OnPropertyChanged(nameof(ShowsEmptyLyricState));
        OnPropertyChanged(nameof(HasSourceNotice));
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-announces the flags that describe where the lyrics came from.</summary>
    private void NotifyIdentityDerived()
    {
        OnPropertyChanged(nameof(SectionLabel));
        OnPropertyChanged(nameof(SourceNotice));
        OnPropertyChanged(nameof(HasSourceNotice));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }
}
