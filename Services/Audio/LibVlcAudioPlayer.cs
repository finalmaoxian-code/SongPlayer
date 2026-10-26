using System.IO;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using SongPlayer.Models;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Playback engine built on LibVLC.
/// </summary>
/// <remarks>
/// LibVLC replaces the WPF <c>MediaPlayer</c> because the interesting sources are not plain local
/// files: a Bilibili track is a fragmented MP4 (<c>.m4s</c>) served over HTTPS by a CDN that
/// answers <c>application/octet-stream</c> and needs a referer. LibVLC demuxes that without
/// help and also decodes formats WPF cannot, so one engine covers both the library and online
/// playback.
/// <para>
/// The engine is the only writer of the playhead. It polls <see cref="MediaPlayer.Time"/> on the
/// dispatcher instead of listening to LibVLC's own <c>TimeChanged</c>/<c>PositionChanged</c>
/// events, because those are raised on native threads and would make every consumer of
/// <see cref="PositionChanged"/> worry about thread affinity. Polling also matches the previous
/// engine, so the player bar's feedback-loop guards keep working unchanged.
/// </para>
/// <para>
/// Seeks are coalesced: dragging the slider requests a seek per pixel and each one would make
/// LibVLC flush its network buffer.
/// </para>
/// </remarks>
public sealed class LibVlcAudioPlayer : IAudioPlayer, IDisposable
{
    /// <summary>How often the playhead is sampled while playing.</summary>
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a seek waits for the next one before it is actually applied.</summary>
    private static readonly TimeSpan SeekCoalesceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Referer the CDN expects. Bilibili rejects media requests that arrive without it.
    /// </summary>
    private const string StreamReferer = "https://www.bilibili.com/";

    private readonly Dispatcher _dispatcher;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _seekTimer;

    private Media? _currentMedia;
    private TimeSpan _pendingPosition;
    private bool _hasPendingSeek;
    private bool _hasEnded;
    private double _volume = 70;
    private bool _disposed;

    /// <summary>
    /// Creates the engine and initialises the native LibVLC runtime.
    /// </summary>
    /// <remarks>
    /// Must be constructed on the UI thread: the poll timer and every event it raises depend on
    /// that thread's dispatcher.
    /// </remarks>
    public LibVlcAudioPlayer()
    {
        Core.Initialize();

        _dispatcher = Dispatcher.CurrentDispatcher;

        // SongPlayer has no video surface; asking LibVLC to skip video avoids creating an output
        // window for the video track some audio streams still carry.
        _libVlc = new LibVLC("--no-video");
        _player = new MediaPlayer(_libVlc) { Volume = (int)_volume };

        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnEncounteredError;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = PositionPollInterval,
        };

        _positionTimer.Tick += OnPositionTimerTick;

        _seekTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = SeekCoalesceInterval,
        };

        _seekTimer.Tick += OnSeekTimerTick;
    }

    /// <inheritdoc />
    public bool IsPlaying => _player.IsPlaying;

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);

            if (!_disposed)
            {
                _player.Volume = (int)_volume;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler? PlaybackEnded;

    /// <inheritdoc />
    public event EventHandler<string>? PlaybackFailed;

    /// <inheritdoc />
    public void Play(Track track, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(track);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? source = ResolveSource(track, out string? failure);

        if (source is null)
        {
            Fail(failure);
            return;
        }

        bool isStream = string.IsNullOrWhiteSpace(track.FilePath);

        if (!isStream && !File.Exists(source))
        {
            Fail($"“{track.Title}” is no longer on disk.");
            return;
        }

        // Reopening an unchanged file would restart the decoder for nothing, so a resume is just
        // a seek on the media that is already open.
        if (!_hasEnded && IsSameSource(source))
        {
            ApplySeek(position);
            _player.Play();
            _positionTimer.Start();
            return;
        }

        _hasEnded = false;
        _pendingPosition = position;
        _hasPendingSeek = false;

        if (!_player.Play(OpenMedia(source, isStream)))
        {
            Fail($"Could not start playing “{track.Title}”.");
            return;
        }

        ApplySeek(position);
        _positionTimer.Start();
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_disposed || _hasEnded)
        {
            return;
        }

        _player.SetPause(true);
        _positionTimer.Stop();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented as pause plus a seek to zero rather than <see cref="MediaPlayer.Stop"/>, which
    /// closes the media and throws away the decoded stream every time the user loads a track.
    /// </remarks>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _player.SetPause(true);
        ApplySeek(TimeSpan.Zero);
        _positionTimer.Stop();
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        if (_disposed || _hasEnded)
        {
            return;
        }

        _pendingPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _hasPendingSeek = true;

        if (!_seekTimer.IsEnabled)
        {
            _seekTimer.Start();
        }
    }

    /// <summary>
    /// Releases the native runtime.
    /// </summary>
    /// <remarks>
    /// LibVLC keeps an audio output device open, which would outlive the window, so the
    /// composition root disposes the engine on exit.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _positionTimer.Stop();
        _seekTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _seekTimer.Tick -= OnSeekTimerTick;

        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnEncounteredError;

        _player.Stop();
        _player.Dispose();

        _currentMedia?.Dispose();
        _currentMedia = null;

        _libVlc.Dispose();
    }

    /// <summary>
    /// Picks the string LibVLC should open and explains why there is none.
    /// </summary>
    /// <param name="track">Track requested by the player bar.</param>
    /// <param name="failure">Message to publish when no source could be resolved.</param>
    /// <returns>Absolute file path or stream URL, or <see langword="null"/> on failure.</returns>
    private static string? ResolveSource(Track track, out string? failure)
    {
        failure = null;

        if (!string.IsNullOrWhiteSpace(track.FilePath))
        {
            return Path.GetFullPath(track.FilePath);
        }

        if (!string.IsNullOrWhiteSpace(track.StreamUri))
        {
            return track.StreamUri;
        }

        failure = $"“{track.Title}” has no audio file, so it cannot be played.";
        return null;
    }

    /// <summary>
    /// Builds the media for <paramref name="source"/> and remembers it as the open source.
    /// </summary>
    /// <param name="source">Absolute path or URL.</param>
    /// <param name="isStream">True for an online source, which needs network options.</param>
    /// <returns>Media that replaces <see cref="_currentMedia"/>.</returns>
    private Media OpenMedia(string source, bool isStream)
    {
        _currentMedia?.Dispose();

        var media = new Media(_libVlc, source, isStream ? FromType.FromLocation : FromType.FromPath);

        if (isStream)
        {
            // Temporary CDN URLs are only honoured when the request looks like it came from the
            // site, and the CDN answers octet-stream so the demuxer has to be told what it is.
            media.AddOption($":http-referrer={StreamReferer}");
            media.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            media.AddOption(":network-caching=1500");
        }

        _currentMedia = media;
        _openSource = source;

        return media;
    }

    /// <summary>
    /// Source of the media currently loaded, if any.
    /// </summary>
    private string? _openSource;

    /// <summary>
    /// True when <paramref name="source"/> is the media already open.
    /// </summary>
    /// <param name="source">Source about to be played.</param>
    private bool IsSameSource(string source) =>
        string.Equals(source, _openSource, StringComparison.OrdinalIgnoreCase)
        && _currentMedia is not null;

    /// <summary>
    /// Moves the playhead, ignoring sources LibVLC cannot seek in.
    /// </summary>
    /// <param name="position">Target position.</param>
    private void ApplySeek(TimeSpan position)
    {
        if (!_player.IsSeekable)
        {
            return;
        }

        try
        {
            _player.Time = (long)position.TotalMilliseconds;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Fail($"Could not seek in the loaded track: {exception.Message}");
        }
    }

    /// <summary>
    /// Applies the seek the user asked for most recently.
    /// </summary>
    private void OnSeekTimerTick(object? sender, EventArgs e)
    {
        _seekTimer.Stop();

        if (!_hasPendingSeek)
        {
            return;
        }

        _hasPendingSeek = false;
        ApplySeek(_pendingPosition);
    }

    /// <summary>
    /// Publishes the position the audio output has actually reached.
    /// </summary>
    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _hasEnded)
        {
            return;
        }

        TimeSpan position = Position;

        // LibVLC reports zero for a moment after a seek is requested; publishing it would snap the
        // lyric highlight back to the start of the track on every seek.
        if (position <= TimeSpan.Zero && (_hasPendingSeek || _pendingPosition > TimeSpan.Zero))
        {
            return;
        }

        PositionChanged?.Invoke(this, position);
    }

    /// <summary>
    /// Current playhead, clamped because LibVLC reports <c>-1</c> before a media has a clock.
    /// </summary>
    private TimeSpan Position
    {
        get
        {
            long milliseconds = _player.Time;

            return milliseconds <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>
    /// Reports the end of the track once and stops polling.
    /// </summary>
    /// <remarks>
    /// Raised on a native thread, so the event is marshalled; stopping the media from inside the
    /// handler deadlocks LibVLC.
    /// </remarks>
    private void OnEndReached(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _hasEnded)
            {
                return;
            }

            _hasEnded = true;
            _positionTimer.Stop();

            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Turns a LibVLC decoding failure into a message the player bar can show.
    /// </summary>
    private void OnEncounteredError(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                Fail("Could not play this source — the format is not supported or the link has expired.");
            }
        });
    }

    /// <summary>
    /// Stops polling and publishes <paramref name="message"/>.
    /// </summary>
    /// <param name="message">User-facing reason.</param>
    /// <remarks>
    /// The playhead is deliberately left where it is: snapping it to zero would look like the
    /// track restarted successfully right before the error line appeared.
    /// </remarks>
    private void Fail(string? message)
    {
        _positionTimer.Stop();

        PlaybackFailed?.Invoke(this, message ?? "Playback failed.");
    }
}
