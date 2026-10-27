namespace SongPlayer.Models;

/// <summary>
/// What a lyric lookup ended up with.
/// </summary>
/// <remarks>
/// The old provider answered "a list of lines", which cannot tell "this song has no lyrics" from
/// "somebody wrote plain lyrics with no timing" or "the database was down". Those three want
/// different words on screen and different follow-up actions, so the answer carries the kind.
/// <see cref="LyricKind.Loading"/> is deliberately absent: loading is a state of the view model,
/// not something a completed lookup can report.
/// </remarks>
public enum LyricKind
{
    /// <summary>Timed lines that can follow the playback position.</summary>
    Synced,

    /// <summary>Lyric text without timestamps, shown as a static block.</summary>
    Plain,

    /// <summary>The record exists and says the track has no vocals.</summary>
    Instrumental,

    /// <summary>Nothing usable was found anywhere.</summary>
    NotFound,

    /// <summary>Several candidates fit, so the user has to pick one.</summary>
    NeedsSelection,

    /// <summary>The lookup could not be completed (offline, throttled, broken response).</summary>
    Failed,
}

/// <summary>
/// Where the lines on screen actually came from.
/// </summary>
/// <remarks>
/// The overlay labels its card from this rather than from <see cref="Track.IsStream"/>. A Bilibili
/// track whose lyrics came from a real lyric database must not be labelled as video subtitles, and
/// a video subtitle track must not be presented as the song's own words.
/// </remarks>
public enum LyricSourceKind
{
    /// <summary>No source, used by the empty results.</summary>
    None,

    /// <summary>Synchronised lyric database (LRCLIB).</summary>
    Lrclib,

    /// <summary>Subtitle track published with the video, archived on an earlier play.</summary>
    VideoCaptions,

    /// <summary>Built-in demo lyrics, only ever for the sample track.</summary>
    Sample,
}

/// <summary>
/// One lyric record the matcher considered, reduced to what a chooser needs to show.
/// </summary>
/// <param name="RecordId">Identifier of the record in the lyric database.</param>
/// <param name="TrackName">Song name the record claims.</param>
/// <param name="ArtistName">Artist the record claims, or <see langword="null"/> when blank.</param>
/// <param name="AlbumName">Album the record claims, or <see langword="null"/> when blank.</param>
/// <param name="Duration">Length the record claims, or <see langword="null"/> when unknown.</param>
/// <param name="Instrumental">True when the record is flagged as having no vocals.</param>
/// <param name="HasSyncedLines">True when the record carries a timed lyric body.</param>
/// <param name="HasPlainLyrics">True when the record carries untimed lyric text.</param>
/// <param name="Score">Match score in the range 0–100, from <c>LyricMatcher</c>.</param>
public sealed record LyricCandidate(
    long RecordId,
    string TrackName,
    string? ArtistName,
    string? AlbumName,
    TimeSpan? Duration,
    bool Instrumental,
    bool HasSyncedLines,
    bool HasPlainLyrics,
    double Score)
{
    /// <summary>True when picking this record would put words on screen.</summary>
    public bool HasContent => HasSyncedLines || HasPlainLyrics || Instrumental;

    /// <summary>Short description of the payload, for the chooser row.</summary>
    public string TypeLabel => Instrumental
        ? "Instrumental"
        : HasSyncedLines
            ? "Synced"
            : HasPlainLyrics
                ? "Plain text"
                : "No text";

    /// <summary>Length as <c>m:ss</c>, or an em dash when the record does not say.</summary>
    public string DurationText =>
        Duration is { } length && length > TimeSpan.Zero ? TimeSpanFormatting.ToClock(length) : "—";

    /// <summary>Artist, or a neutral label when the record has none.</summary>
    public string ArtistText => string.IsNullOrWhiteSpace(ArtistName) ? "Unknown artist" : ArtistName;

    /// <summary>Album, or an em dash when the record has none.</summary>
    public string AlbumText => string.IsNullOrWhiteSpace(AlbumName) ? "—" : AlbumName;
}

/// <summary>
/// Everything the lyric lookup learned, in a shape the overlay can render without asking again.
/// </summary>
/// <param name="Kind">Which of the outcomes this is.</param>
/// <param name="Lines">Timed lines, empty unless <paramref name="Kind"/> is <see cref="LyricKind.Synced"/>.</param>
/// <param name="PlainText">Untimed lyric text, or <see langword="null"/>.</param>
/// <param name="Source">Provider the content came from.</param>
/// <param name="RecordId">Lyric database record chosen, when there is one.</param>
/// <param name="MatchedTitle">Song name of the chosen record.</param>
/// <param name="MatchedArtist">Artist of the chosen record.</param>
/// <param name="MatchedAlbum">Album of the chosen record.</param>
/// <param name="MatchedDuration">Length of the chosen record.</param>
/// <param name="MatchScore">Score that decided the automatic match, 0–100.</param>
/// <param name="Explanation">One sentence saying why this record was chosen.</param>
/// <param name="Candidates">Records to offer when the choice must be made by hand.</param>
/// <param name="Message">Presentable status or failure text, or <see langword="null"/>.</param>
public sealed record LyricLookupResult(
    LyricKind Kind,
    IReadOnlyList<LyricLine> Lines,
    string? PlainText,
    LyricSourceKind Source,
    long? RecordId,
    string? MatchedTitle,
    string? MatchedArtist,
    string? MatchedAlbum,
    TimeSpan? MatchedDuration,
    int MatchScore,
    string? Explanation,
    IReadOnlyList<LyricCandidate> Candidates,
    string? Message)
{
    /// <summary>
    /// True when there is something to read: timed lines or untimed text.
    /// </summary>
    /// <remarks>
    /// <see cref="LyricKind.Instrumental"/> is a real answer that carries no words, which is why it
    /// is not counted here: it must not pull the card away from the visualiser.
    /// </remarks>
    public bool HasContent => Lines.Count > 0 || !string.IsNullOrWhiteSpace(PlainText);

    /// <summary>
    /// True when the tracked timeline can be followed and clicked.
    /// </summary>
    public bool HasTimedLines => Lines.Count > 0;

    /// <summary>True when the user can be offered a choice of records.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>
    /// True when asking again could plausibly change the answer.
    /// </summary>
    /// <remarks>
    /// A completed, cached "no such song" is still worth a retry: the record may have been added
    /// since, and the negative cache expires. A live network failure obviously is.
    /// </remarks>
    public bool CanRetry => Kind is LyricKind.NotFound or LyricKind.Failed or LyricKind.NeedsSelection;

    /// <summary>Builds a result with no content and no source.</summary>
    private static LyricLookupResult Empty(LyricKind kind, string message) =>
        new(kind, [], null, LyricSourceKind.None, null, null, null, null, null, 0, null, [], message);

    /// <summary>Result for a matched record with timed lines.</summary>
    public static LyricLookupResult Synced(
        IReadOnlyList<LyricLine> lines,
        LyricSourceKind source,
        long? recordId = null,
        string? title = null,
        string? artist = null,
        string? album = null,
        TimeSpan? duration = null,
        int score = 0,
        string? explanation = null,
        string? message = null) =>
        new(
            LyricKind.Synced, lines, null, source, recordId, title, artist, album, duration, score,
            explanation, [], message);

    /// <summary>Result for a matched record that only carries untimed text.</summary>
    public static LyricLookupResult Plain(
        string plainText,
        LyricSourceKind source,
        long? recordId = null,
        string? title = null,
        string? artist = null,
        string? album = null,
        TimeSpan? duration = null,
        int score = 0,
        string? explanation = null,
        string? message = null) =>
        new(
            LyricKind.Plain, [], plainText, source, recordId, title, artist, album, duration, score,
            explanation, [], message);

    /// <summary>Result for a record flagged as having no vocals.</summary>
    public static LyricLookupResult Instrumental(
        LyricSourceKind source,
        long? recordId = null,
        string? title = null,
        string? artist = null,
        string? album = null,
        TimeSpan? duration = null,
        int score = 0,
        string? explanation = null,
        string? message = null) =>
        new(
            LyricKind.Instrumental, [], null, source, recordId, title, artist, album, duration, score,
            explanation, [], message);

    /// <summary>Result for "the database does not have this song".</summary>
    public static LyricLookupResult NotFound(string message) => Empty(LyricKind.NotFound, message);

    /// <summary>Result asking the user to choose between near-equal candidates.</summary>
    public static LyricLookupResult NeedsSelection(
        IReadOnlyList<LyricCandidate> candidates,
        string message,
        string? explanation = null) =>
        new(
            LyricKind.NeedsSelection, [], null, LyricSourceKind.Lrclib, null, null, null, null, null, 0,
            explanation, candidates, message);

    /// <summary>Result for a lookup that could not be completed.</summary>
    public static LyricLookupResult Failed(string message) => Empty(LyricKind.Failed, message);
}
