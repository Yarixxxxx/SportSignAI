using LibVLCSharp.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Infrastructure.Media;

public sealed class LibVlcMediaPlaybackService : IMediaPlaybackService, IDisposable
{
    private static readonly string[] LowLatencyLibVlcOptions =
    [
        "--no-video-title-show",
        "--file-caching=60",
        "--network-caching=60",
        "--live-caching=60",
        "--disc-caching=60",
        "--avcodec-hw=any"
    ];

    private static readonly string[] LowLatencyMediaOptions =
    [
        ":file-caching=60",
        ":network-caching=60",
        ":live-caching=60",
        ":disc-caching=60"
    ];

    private static readonly TimeSpan SeekWakePollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SeekWakeMaxDuration = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SeekWakeMinimumAdvance = TimeSpan.FromMilliseconds(24);
    private static readonly TimeSpan SlowRateSeekBoostMinDuration = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan SlowRateSeekBoostMaxDuration = TimeSpan.FromMilliseconds(220);
    private const double SlowRateSeekBoostThreshold = 1.0d;

    private static readonly TimeSpan[] PlaybackResumeRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600)
    ];

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private LibVLCSharp.Shared.Media? _currentMedia;
    private IntPtr _preferredVideoHandle;
    private CancellationTokenSource? _resumePlaybackCancellation;
    private CancellationTokenSource? _seekWakeCancellation;
    private double _requestedPlaybackRate = 1.0d;
    private double _requestedVideoZoom = 1.0d;
    private double _requestedVideoZoomCenterX = 0.5d;
    private double _requestedVideoZoomCenterY = 0.5d;
    private double _requestedVideoViewportWidth;
    private double _requestedVideoViewportHeight;
    private long _rawVideoWidth;
    private long _rawVideoHeight;
    private double _sampleAspectRatio = 1d;
    private long _sourceVideoWidth;
    private long _sourceVideoHeight;
    private long _timeChangedVersion;
    private long _lastMediaTimeMilliseconds;
    private int _volume = 100;
    private bool _isMuted;
    private bool _disposed;

    public LibVlcMediaPlaybackService()
    {
    }

    public event EventHandler? PlaybackStateChanged;
    public event EventHandler<long>? FrameChanged;

    public bool IsPlaying => _mediaPlayer?.IsPlaying == true;
    public bool IsMuted => _mediaPlayer?.Mute ?? _isMuted;
    public long CurrentFrame { get; private set; }
    public long DurationFrames { get; private set; }
    public double FramesPerSecond { get; private set; } = 30d;
    public long VideoWidth { get; private set; }
    public long VideoHeight { get; private set; }
    public int Volume => _mediaPlayer?.Volume ?? _volume;
    public double PlaybackRate => _requestedPlaybackRate;
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    private MediaPlayer EnsureMediaPlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_mediaPlayer is not null)
        {
            return _mediaPlayer;
        }

        var libVlcDirectory = ResolveLibVlcDirectory();
        if (string.IsNullOrWhiteSpace(libVlcDirectory))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new InvalidOperationException(BuildMacLibVlcMissingMessage());
            }

            LibVLCSharp.Shared.Core.Initialize();
        }
        else
        {
            Trace.TraceInformation($"LibVLC runtime directory: {libVlcDirectory}");
            LibVLCSharp.Shared.Core.Initialize(libVlcDirectory);
        }

        _libVlc = new LibVLC(BuildLibVlcOptions(libVlcDirectory));
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = _volume,
            Mute = _isMuted
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _preferredVideoHandle != IntPtr.Zero)
        {
            _mediaPlayer.Hwnd = _preferredVideoHandle;
        }

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += OnPausedOrStopped;
        _mediaPlayer.Stopped += OnPausedOrStopped;

        return _mediaPlayer;
    }

    private static string[] BuildLibVlcOptions(string? libVlcDirectory)
    {
        if (string.IsNullOrWhiteSpace(libVlcDirectory))
        {
            return LowLatencyLibVlcOptions;
        }

        var pluginsDirectory = EnumerateLibVlcPluginCandidateDirectories(libVlcDirectory)
            .FirstOrDefault(Directory.Exists);
        if (string.IsNullOrWhiteSpace(pluginsDirectory))
        {
            return LowLatencyLibVlcOptions;
        }

        return [.. LowLatencyLibVlcOptions, $"--plugin-path={pluginsDirectory}"];
    }

    private static string? ResolveLibVlcDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architectureRuntime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64";

        var candidates = EnumerateLibVlcCandidateDirectories(baseDirectory, architectureRuntime).ToArray();
        foreach (var directory in candidates)
        {
            if (ContainsLibVlc(directory))
            {
                return directory;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Trace.TraceWarning($"LibVLC runtime was not found. Checked: {string.Join("; ", candidates)}");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLibVlcCandidateDirectories(string baseDirectory, string architectureRuntime)
    {
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "lib");
        yield return Path.Combine(baseDirectory, "libvlc", architectureRuntime, "lib");
        yield return Path.Combine(baseDirectory, "libvlc");
        yield return Path.Combine(baseDirectory, "libvlc", architectureRuntime);
        yield return Path.Combine(baseDirectory, "runtimes", architectureRuntime, "native");
        yield return Path.Combine(baseDirectory, "runtimes", architectureRuntime);
        yield return Path.Combine(baseDirectory, "libvlc", "macos");
        yield return Path.Combine(baseDirectory, "libvlc", "macos-arm64");
        yield return Path.Combine(baseDirectory, "libvlc", "macos-x64");
    }

    private static IEnumerable<string> EnumerateLibVlcPluginCandidateDirectories(string libVlcDirectory)
    {
        yield return Path.Combine(libVlcDirectory, "plugins");
        yield return Path.Combine(libVlcDirectory, "lib", "plugins");
        yield return Path.Combine(Directory.GetParent(libVlcDirectory)?.FullName ?? libVlcDirectory, "plugins");
    }

    private static bool ContainsLibVlc(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists(Path.Combine(directory, "libvlc.dll"));
        }

        return File.Exists(Path.Combine(directory, "libvlc.dylib"))
            && (File.Exists(Path.Combine(directory, "libvlccore.dylib"))
                || File.Exists(Path.Combine(directory, "lib", "libvlccore.dylib")));
    }

    private static string BuildMacLibVlcMissingMessage()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architectureRuntime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64";
        var candidates = EnumerateLibVlcCandidateDirectories(baseDirectory, architectureRuntime);

        return "LibVLC runtime is missing from the macOS app bundle. " +
            "Rebuild the app with scripts/macos/package-app.sh and make sure libvlc.dylib and libvlccore.dylib are copied. " +
            $"Checked directories: {string.Join("; ", candidates)}";
    }

    public Task<MediaMetadata> OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Video file not found.", filePath);
        }

        var mediaPlayer = EnsureMediaPlayer();
        var libVlc = _libVlc ?? throw new InvalidOperationException("LibVLC is not initialized.");
        _currentMedia?.Dispose();
        _rawVideoWidth = 0;
        _rawVideoHeight = 0;
        _sampleAspectRatio = 1d;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        VideoWidth = 0;
        VideoHeight = 0;
        _requestedVideoZoom = 1.0d;
        _requestedVideoZoomCenterX = 0.5d;
        _requestedVideoZoomCenterY = 0.5d;
        _requestedVideoViewportWidth = 0d;
        _requestedVideoViewportHeight = 0d;
        mediaPlayer.CropGeometry = string.Empty;
        mediaPlayer.AspectRatio = null;
        mediaPlayer.Scale = 0;
        _currentMedia = new LibVLCSharp.Shared.Media(libVlc, new Uri(filePath));
        var media = _currentMedia;
        foreach (var option in LowLatencyMediaOptions)
        {
            media.AddOption(option);
        }

        media.Parse(MediaParseOptions.ParseLocal);

        if (media.Tracks is { Length: > 0 } tracks)
        {
            foreach (var track in tracks)
            {
                if (track.TrackType != TrackType.Video)
                {
                    continue;
                }

                if (track.Data.Video.FrameRateDen > 0 && track.Data.Video.FrameRateNum > 0)
                {
                    FramesPerSecond = (double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen;
                }

                _rawVideoWidth = Math.Max(0L, (long)track.Data.Video.Width);
                _rawVideoHeight = Math.Max(0L, (long)track.Data.Video.Height);
                _sampleAspectRatio = GetSampleAspectRatio(track.Data.Video);
                (_sourceVideoWidth, _sourceVideoHeight) = GetDisplayVideoSize(_rawVideoWidth, _rawVideoHeight, _sampleAspectRatio);
                VideoWidth = _sourceVideoWidth;
                VideoHeight = _sourceVideoHeight;
                break;
            }
        }

        mediaPlayer.Media = media;
        UpdateDuration(media.Duration);
        CurrentFrame = 0;

        return Task.FromResult(new MediaMetadata(filePath, FramesPerSecond, DurationFrames, VideoWidth, VideoHeight));
    }

    public Task<MediaMetadata> OpenLiveCameraAsync(string? deviceName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Live camera capture is supported by the mpv playback service.");
    }

    public Task<MediaMetadata> OpenLiveStreamAsync(string source, string metadataPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Live stream source is required.", nameof(source));
        }

        var mediaPlayer = EnsureMediaPlayer();
        var libVlc = _libVlc ?? throw new InvalidOperationException("LibVLC is not initialized.");
        _currentMedia?.Dispose();
        _rawVideoWidth = 0;
        _rawVideoHeight = 0;
        _sampleAspectRatio = 1d;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        VideoWidth = 0;
        VideoHeight = 0;
        _requestedVideoZoom = 1.0d;
        _requestedVideoZoomCenterX = 0.5d;
        _requestedVideoZoomCenterY = 0.5d;
        _requestedVideoViewportWidth = 0d;
        _requestedVideoViewportHeight = 0d;
        mediaPlayer.CropGeometry = string.Empty;
        mediaPlayer.AspectRatio = null;
        mediaPlayer.Scale = 0;

        var uri = Uri.TryCreate(source, UriKind.Absolute, out var parsedUri)
            ? parsedUri
            : throw new InvalidOperationException($"Live stream source is invalid: {source}");

        _currentMedia = new LibVLCSharp.Shared.Media(libVlc, uri);
        var media = _currentMedia;
        foreach (var option in LowLatencyMediaOptions)
        {
            media.AddOption(option);
        }

        media.Parse(MediaParseOptions.ParseNetwork);
        mediaPlayer.Media = media;
        CurrentFrame = 0;
        DurationFrames = 1;
        TryRefreshVideoSize();
        ApplyVideoZoom();

        return Task.FromResult(new MediaMetadata(metadataPath, FramesPerSecond, DurationFrames, VideoWidth, VideoHeight));
    }

    public void Play()
    {
        if (_currentMedia is null && _mediaPlayer is null)
        {
            return;
        }

        var mediaPlayer = EnsureMediaPlayer();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            mediaPlayer.Hwnd == IntPtr.Zero &&
            _preferredVideoHandle != IntPtr.Zero)
        {
            mediaPlayer.Hwnd = _preferredVideoHandle;
        }

        mediaPlayer.SetPause(false);
        if (!mediaPlayer.IsPlaying)
        {
            mediaPlayer.Play();
        }

        TryRefreshVideoSize();
        ApplyVideoZoom();
    }
    public void Pause()
    {
        CancelPendingPlaybackResume();
        _mediaPlayer?.SetPause(true);
    }

    public void SeekToFrame(long frame)
    {
        var mediaPlayer = _mediaPlayer;
        if (mediaPlayer is null)
        {
            return;
        }

        var wasPlaying = mediaPlayer.IsPlaying;
        var timeChangedVersionBeforeSeek = Interlocked.Read(ref _timeChangedVersion);
        var safeFrame = Math.Max(0, Math.Min(frame, DurationFrames));
        var milliseconds = (long)Math.Round((safeFrame / FramesPerSecond) * 1000d);
        var shouldWakeSeek = wasPlaying;
        var shouldBrieflyBoostSeek = wasPlaying && _requestedPlaybackRate < SlowRateSeekBoostThreshold;
        if (shouldWakeSeek)
        {
            CancelPendingSeekWake();
            KeepPlaybackAwake(shouldBrieflyBoostSeek ? 1.0d : _requestedPlaybackRate);
        }

        mediaPlayer.Time = milliseconds;

        ResumePlaybackAfterOperation(
            wasPlaying,
            forceImmediatePlay: shouldWakeSeek,
            forceRetryPlay: shouldWakeSeek);

        if (shouldWakeSeek)
        {
            KeepPlaybackAwakeAfterSeek(milliseconds, timeChangedVersionBeforeSeek, shouldBrieflyBoostSeek);
        }

        CurrentFrame = safeFrame;
        FrameChanged?.Invoke(this, CurrentFrame);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StepFrameForward() => SeekToFrame(CurrentFrame + 1);
    public void StepFrameBackward() => SeekToFrame(CurrentFrame - 1);
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Volume = _volume;
        }
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingPlaybackResume();
        CancelPendingSeekWake();
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Media = null;
        }

        _currentMedia?.Dispose();
        _currentMedia = null;
        _rawVideoWidth = 0;
        _rawVideoHeight = 0;
        _sampleAspectRatio = 1d;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        _requestedPlaybackRate = 1.0d;
        _requestedVideoZoom = 1.0d;
        _requestedVideoZoomCenterX = 0.5d;
        _requestedVideoZoomCenterY = 0.5d;
        _requestedVideoViewportWidth = 0d;
        _requestedVideoViewportHeight = 0d;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.CropGeometry = string.Empty;
            _mediaPlayer.AspectRatio = null;
            _mediaPlayer.Scale = 0;
        }

        CurrentFrame = 0;
        DurationFrames = 1;
        FramesPerSecond = 30d;
        VideoWidth = 0;
        VideoHeight = 0;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, CurrentFrame);
    }

    public void SetPlaybackRate(double playbackRate)
    {
        var normalizedRate = Math.Clamp(playbackRate, 0.25d, 2.0d);
        var mediaPlayer = _mediaPlayer;
        var wasPlaying = mediaPlayer?.IsPlaying == true;
        CancelPendingSeekWake();
        _requestedPlaybackRate = normalizedRate;
        mediaPlayer?.SetRate((float)normalizedRate);

        ResumePlaybackAfterOperation(wasPlaying, forceImmediatePlay: false, forceRetryPlay: false);

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVideoZoom(double zoom, double centerX, double centerY, double viewportWidth, double viewportHeight)
    {
        _requestedVideoZoom = Math.Clamp(zoom, 1d, 4d);
        _requestedVideoZoomCenterX = Math.Clamp(centerX, 0d, 1d);
        _requestedVideoZoomCenterY = Math.Clamp(centerY, 0d, 1d);
        if (viewportWidth > 0 && viewportHeight > 0
            && !double.IsNaN(viewportWidth) && !double.IsInfinity(viewportWidth)
            && !double.IsNaN(viewportHeight) && !double.IsInfinity(viewportHeight))
        {
            _requestedVideoViewportWidth = viewportWidth;
            _requestedVideoViewportHeight = viewportHeight;
        }

        TryRefreshVideoSize();
        ApplyVideoZoom();
    }

    private void ApplyVideoZoom()
    {
        var sourceWidth = _sourceVideoWidth > 0 ? _sourceVideoWidth : VideoWidth;
        var sourceHeight = _sourceVideoHeight > 0 ? _sourceVideoHeight : VideoHeight;

        VideoWidth = sourceWidth;
        VideoHeight = sourceHeight;
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.CropGeometry = string.Empty;
        _mediaPlayer.AspectRatio = null;
        _mediaPlayer.Scale = 0;
    }

    private static string FormatAspectRatio(long width, long height)
    {
        var divisor = GreatestCommonDivisor(Math.Max(1L, width), Math.Max(1L, height));
        return $"{width / divisor}:{height / divisor}";
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Max(1L, Math.Abs(left));
    }

    private static double GetSampleAspectRatio(VideoTrack videoTrack)
    {
        if (videoTrack.SarNum == 0 || videoTrack.SarDen == 0)
        {
            return 1d;
        }

        return Math.Max(0.0001d, videoTrack.SarNum / (double)videoTrack.SarDen);
    }

    private static (long Width, long Height) GetDisplayVideoSize(long rawWidth, long rawHeight, double sampleAspectRatio)
    {
        if (rawWidth <= 0 || rawHeight <= 0)
        {
            return (rawWidth, rawHeight);
        }

        return ((long)Math.Round(rawWidth * sampleAspectRatio), rawHeight);
    }

    private bool TryRefreshVideoSize()
    {
        if (_disposed || _mediaPlayer is null)
        {
            return false;
        }

        uint width = 0;
        uint height = 0;
        if (!_mediaPlayer.Size(0, ref width, ref height) || width == 0 || height == 0)
        {
            return false;
        }

        var measuredWidth = (long)width;
        var measuredHeight = (long)height;
        if (_sourceVideoWidth <= 0
            || _sourceVideoHeight <= 0)
        {
            _rawVideoWidth = measuredWidth;
            _rawVideoHeight = measuredHeight;
            _sampleAspectRatio = 1d;
            _sourceVideoWidth = measuredWidth;
            _sourceVideoHeight = measuredHeight;
        }

        VideoWidth = _sourceVideoWidth;
        VideoHeight = _sourceVideoHeight;
        return true;
    }

    public void ToggleMute()
    {
        _isMuted = !IsMuted;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Mute = _isMuted;
        }
    }

    public void SetVideoOutputHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        _preferredVideoHandle = handle;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Hwnd = handle;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingPlaybackResume();
        CancelPendingSeekWake();
        _currentMedia?.Dispose();
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.LengthChanged -= OnLengthChanged;
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.Paused -= OnPausedOrStopped;
            _mediaPlayer.Stopped -= OnPausedOrStopped;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _libVlc?.Dispose();
        _libVlc = null;
    }

    private void ResumePlaybackAfterOperation(bool shouldResume, bool forceImmediatePlay, bool forceRetryPlay)
    {
        if (!shouldResume || _disposed || _mediaPlayer is null)
        {
            return;
        }

        CancelPendingPlaybackResume();
        if (forceImmediatePlay || !_mediaPlayer.IsPlaying)
        {
            Play();
        }

        var cancellation = new CancellationTokenSource();
        _resumePlaybackCancellation = cancellation;
        _ = RetryResumePlaybackAsync(forceRetryPlay, cancellation.Token);
    }

    private void KeepPlaybackAwakeAfterSeek(long seekMilliseconds, long timeChangedVersionBeforeSeek, bool brieflyBoostSeek)
    {
        var cancellation = new CancellationTokenSource();
        _seekWakeCancellation = cancellation;
        _ = KeepPlaybackAwakeAfterSeekAsync(seekMilliseconds, timeChangedVersionBeforeSeek, brieflyBoostSeek, cancellation.Token);
    }

    private async Task KeepPlaybackAwakeAfterSeekAsync(
        long seekMilliseconds,
        long timeChangedVersionBeforeSeek,
        bool brieflyBoostSeek,
        CancellationToken cancellationToken)
    {
        var wakeStartedAt = Environment.TickCount64;
        var seekAdvanceTarget = seekMilliseconds + (long)SeekWakeMinimumAdvance.TotalMilliseconds;

        while (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            var elapsedMilliseconds = Environment.TickCount64 - wakeStartedAt;
            var hasMediaClockAdvanced =
                Interlocked.Read(ref _timeChangedVersion) > timeChangedVersionBeforeSeek &&
                Interlocked.Read(ref _lastMediaTimeMilliseconds) >= seekAdvanceTarget;
            var shouldContinueBoosting =
                brieflyBoostSeek &&
                elapsedMilliseconds < SlowRateSeekBoostMaxDuration.TotalMilliseconds &&
                (!hasMediaClockAdvanced || elapsedMilliseconds < SlowRateSeekBoostMinDuration.TotalMilliseconds);

            if (hasMediaClockAdvanced && !shouldContinueBoosting ||
                elapsedMilliseconds >= SeekWakeMaxDuration.TotalMilliseconds)
            {
                break;
            }

            KeepPlaybackAwake(shouldContinueBoosting ? 1.0d : _requestedPlaybackRate);

            try
            {
                await Task.Delay(SeekWakePollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (brieflyBoostSeek && !_disposed && !cancellationToken.IsCancellationRequested)
        {
            _mediaPlayer?.SetRate((float)_requestedPlaybackRate);
        }
    }

    private void KeepPlaybackAwake(double playbackRate)
    {
        if (_disposed || _mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.SetRate((float)playbackRate);
        _mediaPlayer.SetPause(false);
        _mediaPlayer.Play();
    }

    private async Task RetryResumePlaybackAsync(bool forceRetryPlay, CancellationToken cancellationToken)
    {
        foreach (var delay in PlaybackResumeRetryDelays)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_mediaPlayer is null || !forceRetryPlay && _mediaPlayer.IsPlaying)
            {
                return;
            }

            Play();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelPendingPlaybackResume()
    {
        if (_resumePlaybackCancellation is null)
        {
            return;
        }

        _resumePlaybackCancellation.Cancel();
        _resumePlaybackCancellation.Dispose();
        _resumePlaybackCancellation = null;
    }

    private void CancelPendingSeekWake()
    {
        if (_seekWakeCancellation is null)
        {
            return;
        }

        _seekWakeCancellation.Cancel();
        _seekWakeCancellation.Dispose();
        _seekWakeCancellation = null;
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
    {
        Interlocked.Exchange(ref _lastMediaTimeMilliseconds, args.Time);
        Interlocked.Increment(ref _timeChangedVersion);
        var frame = (long)Math.Round((args.Time / 1000d) * FramesPerSecond);
        if (frame == CurrentFrame)
        {
            return;
        }

        CurrentFrame = frame;
        FrameChanged?.Invoke(this, CurrentFrame);
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs args)
    {
        TryRefreshVideoSize();
        ApplyVideoZoom();
        UpdateDuration(args.Length);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaying(object? sender, EventArgs args)
    {
        TryRefreshVideoSize();
        ApplyVideoZoom();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPausedOrStopped(object? sender, EventArgs args)
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDuration(long durationMilliseconds)
    {
        DurationFrames = Math.Max(1, (long)Math.Round((Math.Max(0, durationMilliseconds) / 1000d) * FramesPerSecond));
    }
}
