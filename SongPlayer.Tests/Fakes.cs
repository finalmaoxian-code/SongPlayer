using SongPlayer.Models;
using SongPlayer.Services.Bilibili;
using SongPlayer.Services.Library;
using SongPlayer.Services.Lyrics;
using SongPlayer.Services.Storage;

namespace SongPlayer.Tests;

/// <summary>
/// Hand-written lyric database client used by the provider tests.
/// </summary>
/// <remarks>
/// The real client's job is HTTP plumbing, which is covered separately through an
/// <see cref="HttpMessageHandler"/>. The provider's job is ordering, caching and matching, and that is
/// what these fakes drive: records can be queued per query, failures can be raised on demand, and the
/// calls made are recorded so the tests can assert that an exact query was or was not attempted.
/// </remarks>
internal sealed class FakeLrclibClient : ILrclibClient
{
    public List<LrclibQuery> ExactQueries { get; } = [];

    public List<LrclibQuery> SearchQueries { get; } = [];

    public List<long> RequestedIds { get; } = [];

    public LrclibRecord? ExactResult { get; set; }

    public IReadOnlyList<LrclibRecord> SearchResults { get; set; } = [];

    public LrclibRecord? ByIdResult { get; set; }

    public LrclibException? Failure { get; set; }

    public Task<LrclibRecord?> GetAsync(LrclibQuery query, CancellationToken cancellationToken = default)
    {
        ExactQueries.Add(query);
        return Failure is not null ? throw Failure : Task.FromResult(ExactResult);
    }

    public Task<IReadOnlyList<LrclibRecord>> SearchAsync(LrclibQuery query, CancellationToken cancellationToken = default)
    {
        SearchQueries.Add(query);
        return Failure is not null ? throw Failure : Task.FromResult(SearchResults);
    }

    public Task<LrclibRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        RequestedIds.Add(id);
        return Failure is not null ? throw Failure : Task.FromResult(ByIdResult);
    }
}

/// <summary>Subtitle archive that answers from memory.</summary>
internal sealed class FakeCaptionArchive : ICaptionArchive
{
    public Dictionary<string, IReadOnlyList<LyricLine>> Archived { get; } = [];

    public IReadOnlyList<LyricLine> Load(string streamKey) =>
        Archived.TryGetValue(streamKey, out IReadOnlyList<LyricLine>? lines) ? lines : [];

    public Task SaveAsync(string streamKey, IReadOnlyList<BilibiliCaption> captions) => Task.CompletedTask;
}

/// <summary>Lyric provider that returns whatever the test sets, with hand-driven completion.</summary>
internal sealed class ScriptedLyricProvider : ILyricProvider
{
    private readonly Dictionary<string, TaskCompletionSource<LyricLookupResult>> _pending = [];

    public List<(Track Track, LyricLookupOptions Options)> Requests { get; } = [];

    public Dictionary<string, int> Offsets { get; } = [];

    /// <summary>Queues a lookup whose completion the test controls.</summary>
    public TaskCompletionSource<LyricLookupResult> Hold(Track track)
    {
        TaskCompletionSource<LyricLookupResult> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[Key(track)] = source;
        return source;
    }

    /// <summary>Answers immediately with the given result.</summary>
    public LyricLookupResult Immediate { get; set; } = LyricLookupResult.NotFound("Nothing queued.");

    public Task<LyricLookupResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) =>
        GetLyricsAsync(track, LyricLookupOptions.Default, cancellationToken);

    public Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken = default)
    {
        Requests.Add((track, options));

        return _pending.TryGetValue(Key(track), out TaskCompletionSource<LyricLookupResult>? held)
            ? held.Task
            : Task.FromResult(Immediate);
    }

    public Task<int> GetOffsetMillisecondsAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(Offsets.TryGetValue(Key(track), out int offset) ? offset : 0);

    public Task SetOffsetMillisecondsAsync(
        Track track,
        int milliseconds,
        CancellationToken cancellationToken = default)
    {
        Offsets[Key(track)] = milliseconds;
        return Task.CompletedTask;
    }

    private static string Key(Track track) => track.StreamKey ?? track.FilePath ?? track.Title;
}

/// <summary>Library facade that never touches disk.</summary>
internal sealed class EmptyLibrary : IMusicLibraryService
{
    public Task<IReadOnlyList<Track>> ScanAsync(string folder) => Task.FromResult<IReadOnlyList<Track>>([]);

    public Task<IReadOnlyList<Track>> LoadAsync() => Task.FromResult<IReadOnlyList<Track>>([]);

    public Task SaveAsync(IEnumerable<Track> tracks) => Task.CompletedTask;
}

/// <summary>Folder picker that always cancels.</summary>
internal sealed class NullFolderPicker : IFolderPicker
{
    public string? PickFolder(string title) => null;
}

/// <summary>Confirmation prompt that always declines.</summary>
internal sealed class NullConfirmationPrompt : IConfirmationPrompt
{
    public bool Confirm(string title, string message) => false;
}

/// <summary>Online source that refuses to answer, so no test touches the network.</summary>
internal sealed class NullBilibiliSource : IBilibiliSource
{
    public Task<ResolvedMedia> ResolveAsync(string bvid, CancellationToken cancellationToken = default) =>
        throw new BilibiliSourceException("Network access is not available in tests.");

    public Task<IReadOnlyList<BilibiliSearchResult>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BilibiliSearchResult>>([]);
}

/// <summary>
/// Lyric provider that always answers with a chooser.
/// </summary>
/// <remarks>
/// It exists so a test can realise the candidate item template. That template is where a two-way
/// default on a read-only property turns into an unhandled <c>XamlParseException</c>, and no other
/// state reaches it.
/// </remarks>
internal sealed class ChooserLyricProvider : ILyricProvider
{
    /// <summary>Rows the provider offers.</summary>
    public static IReadOnlyList<LyricCandidate> Rows { get; } =
    [
        new(1, "Creep", "Radiohead", "Pablo Honey", TimeSpan.FromSeconds(239), false, true, true, 96),
        new(2, "Creep", "Glee Cast", null, TimeSpan.FromSeconds(212), false, false, true, 74),
        new(3, "Creep (Instrumental)", null, null, null, true, false, false, 55),
    ];

    public Task<LyricLookupResult> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) =>
        GetLyricsAsync(track, LyricLookupOptions.Default, cancellationToken);

    public Task<LyricLookupResult> GetLyricsAsync(
        Track track,
        LyricLookupOptions options,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LyricLookupResult.NeedsSelection(Rows, "Pick the right lyrics.", "Not a confident match."));

    public Task<int> GetOffsetMillisecondsAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task SetOffsetMillisecondsAsync(
        Track track,
        int milliseconds,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
