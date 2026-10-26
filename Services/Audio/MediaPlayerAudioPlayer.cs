using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using SongPlayer.Models;

namespace SongPlayer.Services.Audio;

/// <summary>
/// Playback engine built on the WPF <see cref="MediaPlayer"/>, which ships with the framework
/// and decodes MP3, WAV, WMA and AAC/M4A without an extra package.
/// </summary>
/// <remarks>
/// <para>
/// This type must be created and used on the UI thread: <see cref="MediaPlayer"/> has thread
/// affinity, so every member is called from the dispatcher that created it. The composition root
/// satisfies that by building it in <c>App.OnStartup</c>.
/// </para>
/// <para>
/// <see cref="MediaPlayer"/> is a push-target — it renders audio but never tells anyone where the
/// playhead is, which is exactly the value the shell needs. A <see cref="DispatcherTimer"/>
/// therefore samples <see cref="MediaPlayer.Position"/> four times a second and raises
/// <see cref="PositionChanged"/>; the player bar mirrors that value instead of inventing one.
/// </para>
/// <para>
/// Opening a file is asynchronous: <see cref="MediaPlayer.Open"/> returns before the media is
/// ready, and a <see cref="MediaPlayer.Position"/> assignment issued too early is silently
/// discarded. Resume positions are therefore parked in <see cref="_pendingPosition"/> and applied
/// from <see cref="OnMediaOpened"/>.
/// </para>
/// <para>
/// Seeks are coalesced to ten per second. A slider drag requests a seek for every pixel of travel,
/// and each one makes <see cref="MediaPlayer"/> flush its buffer; without coalescing the audio
/// stutters instead of following the thumb.
/// </para>
/// </remarks>
public sealed class MediaPlayerAudioPlayer : IAudioPlayer, IDisposable
{
    /// <summary>
    /// How often the playhead is sampled. Four updates a second is smooth enough for a lyric
    /// highlight and cheap enough to leave running for hours.
    /// </summary>
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Shortest interval between two seeks actually handed to the media pipeline.</summary>
    private static readonly TimeSpan SeekCoalesceInterval = TimeSpan.FromMilliseconds(100);

    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _seekTimer;

    /// <summary>Path of the media currently open, or <c>null</c> when nothing is loaded.</summary>
    private string? _openPath;

    /// <summary>Position to apply as soon as the pending media finishes opening.</summary>
    private TimeSpan _pendingPosition;

    /// <summary>True when the pending open should start playing once the media is ready.</summary>
    private bool _playWhenOpened;

    /// <summary>True between <see cref="MediaPlayer.MediaOpened"/> and the next close.</summary>
    private bool _isMediaOpen;

    /// <summary>True when the last playback ran off the end of the track.</summary>
    private bool _hasEnded;

    /// <summary>True when <see cref="_pendingPosition"/> still has to be pushed to the engine.</summary>
    private bool _hasPendingSeek;

    private double _volume = 70;

    /// <summary>
    /// Creates the engine and hooks the media events.
    /// </summary>
    public MediaPlayerAudioPlayer()
    {
        _mediaPlayer.MediaOpened += OnMediaOpened;
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.MediaFailed += OnMediaFailed;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = PositionPollInterval,
        };

        _positionTimer.Tick += OnPositionTimerTick;

        _seekTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = SeekCoalesceInterval,
        };

        _seekTimer.Tick += OnSeekTimerTick;

        _mediaPlayer.Volume = ToEngineVolume(_volume);
    }

    /// <inheritdoc />
    public bool IsPlaying { get; private set; }

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            _mediaPlayer.Volume = ToEngineVolume(_volume);
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

        if (string.IsNullOrWhiteSpace(track.FilePath))
        {
            Fail($"“{track.Title}” has no audio file, so it cannot be played.");
            return;
        }

        string path = Path.GetFullPath(track.FilePath);

        if (!File.Exists(path))
        {
            Fail($"“{track.Title}” is no longer on disk.");
            return;
        }

        bool sameMedia = string.Equals(path, _openPath, StringComparison.OrdinalIgnoreCase) && _isMediaOpen;

        if (!sameMedia)
        {
            OpenMedia(path, position);
            return;
        }

        if (_hasEnded)
        {
            // Playing again after the track ended has to rewind, otherwise the engine sits at the
            // end and produces nothing.
            _hasEnded = false;
            ApplySeek(TimeSpan.Zero);
        }
        else
        {
            ApplySeek(position);
        }

        _isMediaOpen = true;
        _mediaPlayer.Play();
        IsPlaying = true;
        _positionTimer.Start();
    }

    /// <inheritdoc />
    public void Pause()
    {
        IsPlaying = false;
        _playWhenOpened = false;
        _positionTimer.Stop();

        if (_isMediaOpen)
        {
            _mediaPlayer.Pause();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        IsPlaying = false;
        _hasEnded = false;
        _playWhenOpened = false;
        _positionTimer.Stop();
        _seekTimer.Stop();
        _hasPendingSeek = false;

        // Deliberately not MediaPlayer.Stop(): that closes the media, and the next play would have
        // to reopen the file and lose the decoded stream. Rewinding keeps the source warm.
        if (_isMediaOpen)
        {
            _mediaPlayer.Pause();
            ApplySeek(TimeSpan.Zero);
        }

        _pendingPosition = TimeSpan.Zero;
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        _pendingPosition = position;

        if (!_isMediaOpen)
        {
            return;
        }

        // A seek past the end of an ended track must clear the ended state before playing again.
        _hasEnded = false;
        _hasPendingSeek = true;

        if (!_seekTimer.IsEnabled)
        {
            _seekTimer.Start();
        }
    }

    /// <summary>
    /// Closes the media and releases the audio device.
    /// </summary>
    public void Dispose()
    {
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;

        _seekTimer.Stop();
        _seekTimer.Tick -= OnSeekTimerTick;

        _mediaPlayer.MediaOpened -= OnMediaOpened;
        _mediaPlayer.MediaEnded -= OnMediaEnded;
        _mediaPlayer.MediaFailed -= OnMediaFailed;
        _mediaPlayer.Close();

        _isMediaOpen = false;
        _openPath = null;
        IsPlaying = false;
    }

    /// <summary>
    /// Starts opening <paramref name="path"/> and remembers what to do once it is ready.
    /// </summary>
    private void OpenMedia(string path, TimeSpan position)
    {
        _isMediaOpen = false;
        _hasEnded = false;
        _hasPendingSeek = false;
        _openPath = path;
        _pendingPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _playWhenOpened = true;

        // Close first: Open on a live MediaPlayer keeps buffering the previous file.
        _mediaPlayer.Close();
        _mediaPlayer.Open(new Uri(path, UriKind.Absolute));
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        _isMediaOpen = true;

        if (_pendingPosition > TimeSpan.Zero)
        {
            // Apply immediately: a resume point has to be in place before the first audio frame,
            // and the coalescing timer would be too late.
            ApplySeek(_pendingPosition);
        }

        if (_playWhenOpened)
        {
            _playWhenOpened = false;
            _mediaPlayer.Play();
            IsPlaying = true;
            _positionTimer.Start();
        }
        else
        {
            IsPlaying = false;
            _positionTimer.Stop();
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _hasEnded = true;
        IsPlaying = false;
        _positionTimer.Stop();

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        string reason = e.ErrorException?.Message ?? "the format is not supported";

        _isMediaOpen = false;
        _openPath = null;
        IsPlaying = false;
        _playWhenOpened = false;
        _positionTimer.Stop();
        _seekTimer.Stop();
        _hasPendingSeek = false;

        Fail($"Could not play this file — {reason}");
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (!_isMediaOpen)
        {
            return;
        }

        TimeSpan position = _mediaPlayer.Position;

        // MediaPlayer reports a zero position for a moment while a seek is in flight; publishing
        // it would make the lyric highlight jump to the start on every seek.
        if (position <= TimeSpan.Zero && (_hasPendingSeek || _pendingPosition > TimeSpan.FromMilliseconds(500)))
        {
            return;
        }

        PositionChanged?.Invoke(this, position);
    }

    private void OnSeekTimerTick(object? sender, EventArgs e)
    {
        _seekTimer.Stop();

        if (!_isMediaOpen || !_hasPendingSeek)
        {
            return;
        }

        _hasPendingSeek = false;
        ApplySeek(_pendingPosition);
    }

    /// <summary>
    /// Pushes <paramref name="position"/> into the media pipeline right now.
    /// </summary>
    private void ApplySeek(TimeSpan position)
    {
        try
        {
            _mediaPlayer.Position = position;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // MediaPlayer throws when the media is torn down mid-flight; the next Play reopens it.
            Fail($"Could not seek in the loaded track: {exception.Message}");
        }
    }

    private void Fail(string message) => PlaybackFailed?.Invoke(this, message);

    /// <summary>
    /// Converts a 0-100 volume to the 0-1 range of <see cref="MediaPlayer.Volume"/>.
    /// </summary>
    private static double ToEngineVolume(double volume) => Math.Clamp(volume / 100d, 0, 1);
}
