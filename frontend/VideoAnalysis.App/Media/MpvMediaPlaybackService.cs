using Avalonia.Threading;
using HanumanInstitute.LibMpv;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.App.Media;

public sealed class MpvMediaPlaybackService : IMediaPlaybackService, IDisposable
{
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MetadataPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MetadataPollTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RendererReadyTimeout = TimeSpan.FromSeconds(3);
    private const double LiveFallbackFramesPerSecond = 30d;

    private readonly MpvCommandOptions _commandOptions = new()
    {
        WaitForResponse = true,
        ThrowOnError = true,
        NoOsd = true
    };
    private readonly MpvCommandOptions _noWaitCommandOptions = new()
    {
        WaitForResponse = false,
        ThrowOnError = false,
        NoOsd = true
    };
    private readonly MpvAsyncOptions _syncOptions = new()
    {
        WaitForResponse = true,
        ThrowOnError = true
    };
    private readonly MpvAsyncOptions _noWaitOptions = new()
    {
        WaitForResponse = false,
        ThrowOnError = false
    };
    private readonly DispatcherTimer _positionTimer;
    private TaskCompletionSource<MpvContext> _contextReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MpvContext? _mpvContext;
    private string? _currentFilePath;
    private bool _hasLoadedCurrentFile;
    private bool _isLiveSource;
    private double _requestedPlaybackRate = 1d;
    private double _requestedVideoZoom = 1d;
    private double _requestedVideoZoomCenterX = 0.5d;
    private double _requestedVideoZoomCenterY = 0.5d;
    private int _volume = 100;
    private bool _isMuted;
    private bool _isPlaying;
    private bool _disposed;

    public MpvMediaPlaybackService()
    {
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PositionPollInterval
        };
        _positionTimer.Tick += (_, _) => RefreshPosition();
        _positionTimer.Start();
    }

    public event EventHandler? PlaybackStateChanged;
    public event EventHandler<long>? FrameChanged;

    public bool IsPlaying => _isPlaying;
    public bool IsMuted => _isMuted;
    public long CurrentFrame { get; private set; }
    public long DurationFrames { get; private set; } = 1;
    public double FramesPerSecond { get; private set; } = 30d;
    public long VideoWidth { get; private set; }
    public long VideoHeight { get; private set; }
    public int Volume => _volume;
    public double PlaybackRate => _requestedPlaybackRate;

    public void AttachMpvContext(MpvContext mpvContext)
    {
        if (_disposed || ReferenceEquals(_mpvContext, mpvContext))
        {
            return;
        }

        var resumeFrame = CurrentFrame;
        var wasPlaying = _isPlaying;

        DetachContextEvents();
        _mpvContext = mpvContext;
        _hasLoadedCurrentFile = false;
        AttachContextEvents(mpvContext);

        ConfigureMpv(mpvContext);
        SetOption(() => mpvContext.Pause.Set(true, _syncOptions));
        SetOption(() => mpvContext.Volume.Set(_volume, _syncOptions));
        SetOption(() => mpvContext.Mute.Set(_isMuted, _syncOptions));
        SetOption(() => mpvContext.Speed.Set(_requestedPlaybackRate, _syncOptions));
        _contextReadySource.TrySetResult(mpvContext);

        if (CanLoadCurrentSource())
        {
            var loaded = TryLoadCurrentFile(mpvContext, pause: !wasPlaying, out _);
            if (loaded)
            {
                SeekAttachedContextToFrame(mpvContext, resumeFrame);
                ApplyVideoZoom(mpvContext, _requestedVideoZoom, _requestedVideoZoomCenterX, _requestedVideoZoomCenterY);
            }

            if (loaded && wasPlaying)
            {
                ResumePlayback(mpvContext);
                _isPlaying = true;
            }
        }
    }

    public async Task<MediaMetadata> OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Video file not found.", filePath);
        }

        return await OpenSourceAsync(filePath, filePath, isLiveSource: false, startPlaying: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaMetadata> OpenLiveCameraAsync(string? deviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cameraName = string.IsNullOrWhiteSpace(deviceName)
            ? WindowsCameraDeviceEnumerator.GetVideoCaptureDevices().FirstOrDefault()?.Name
            : deviceName.Trim();
        if (string.IsNullOrWhiteSpace(cameraName))
        {
            throw new InvalidOperationException("Камера не найдена.");
        }

        var source = BuildDirectShowCameraSource(cameraName);
        var metadataPath = $"camera://{cameraName}";
        return await OpenSourceAsync(source, metadataPath, isLiveSource: true, startPlaying: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaMetadata> OpenLiveStreamAsync(
        string source,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Live stream source is required.", nameof(source));
        }

        return await OpenSourceAsync(source, metadataPath, isLiveSource: true, startPlaying: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MediaMetadata> OpenSourceAsync(
        string source,
        string metadataPath,
        bool isLiveSource,
        bool startPlaying,
        CancellationToken cancellationToken)
    {
        var mpvContext = await WaitForReadyContextAsync(cancellationToken).ConfigureAwait(false);
        _currentFilePath = source;
        _isLiveSource = isLiveSource;
        _hasLoadedCurrentFile = false;
        _isPlaying = false;
        CurrentFrame = 0;
        FramesPerSecond = isLiveSource ? LiveFallbackFramesPerSecond : FramesPerSecond;
        DurationFrames = isLiveSource ? (long)Math.Round(FramesPerSecond * 10d) : 1;
        VideoWidth = 0;
        VideoHeight = 0;

        if (!TryLoadCurrentFile(mpvContext, pause: !startPlaying, out var loadError))
        {
            throw new InvalidOperationException("mpv could not load the video source.", loadError);
        }

        SetOption(() => mpvContext.Pause.Set(!startPlaying, _syncOptions));
        SetOption(() => mpvContext.Volume.Set(_volume, _syncOptions));
        SetOption(() => mpvContext.Mute.Set(_isMuted, _syncOptions));
        SetOption(() => mpvContext.Speed.Set(isLiveSource ? 1d : _requestedPlaybackRate, _syncOptions));
        _isPlaying = startPlaying;
        await PollMetadataAsync(cancellationToken).ConfigureAwait(false);

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, CurrentFrame);

        return new MediaMetadata(metadataPath, FramesPerSecond, DurationFrames, VideoWidth, VideoHeight);
    }

    public void Play()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_currentFilePath) || _mpvContext is not { } mpvContext)
        {
            return;
        }

        if (!_hasLoadedCurrentFile)
        {
            if (!TryLoadCurrentFile(mpvContext, pause: false, out _))
            {
                _isPlaying = false;
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        ResumePlayback(mpvContext);
        _isPlaying = true;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_disposed || _mpvContext is not { } mpvContext)
        {
            return;
        }

        SetOption(() => mpvContext.Pause.Set(true, _syncOptions));
        _isPlaying = false;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool DropLiveBuffers()
    {
        if (_disposed
            || !_isLiveSource
            || !_hasLoadedCurrentFile
            || _mpvContext is not { } mpvContext)
        {
            return false;
        }

        try
        {
            mpvContext.RunCommand(_noWaitCommandOptions, ["drop-buffers"]);
            SetOption(() => mpvContext.Speed.Set(1d, _noWaitOptions));
            SetOption(() => mpvContext.Pause.Set(false, _noWaitOptions));
            _isPlaying = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        if (_mpvContext is { } mpvContext)
        {
            TryInvokeCommand(() => mpvContext.Stop().Invoke(_commandOptions));
        }

        _currentFilePath = null;
        _hasLoadedCurrentFile = false;
        _isLiveSource = false;
        _isPlaying = false;
        CurrentFrame = 0;
        DurationFrames = 1;
        VideoWidth = 0;
        VideoHeight = 0;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, CurrentFrame);
    }

    public void SeekToFrame(long frame)
    {
        if (_disposed || _isLiveSource || string.IsNullOrWhiteSpace(_currentFilePath) || _mpvContext is not { } mpvContext)
        {
            return;
        }

        var wasPlaying = _isPlaying;
        var safeFrame = Math.Clamp(frame, 0, Math.Max(0, DurationFrames));
        var seconds = safeFrame / Math.Max(1d, FramesPerSecond);

        TryInvokeCommand(() => mpvContext.Seek(seconds, SeekOption.Absolute | SeekOption.Exact).Invoke(_commandOptions));
        CurrentFrame = safeFrame;
        FrameChanged?.Invoke(this, CurrentFrame);

        SetOption(() => mpvContext.Speed.Set(_requestedPlaybackRate, _noWaitOptions));
        if (wasPlaying)
        {
            SetOption(() => mpvContext.Pause.Set(false, _noWaitOptions));
            _isPlaying = true;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StepFrameForward() => SeekToFrame(CurrentFrame + 1);

    public void StepFrameBackward() => SeekToFrame(CurrentFrame - 1);

    public void SetVolume(int volume)
    {
        if (_disposed)
        {
            return;
        }

        _volume = Math.Clamp(volume, 0, 100);
        if (_mpvContext is { } mpvContext)
        {
            SetOption(() => mpvContext.Volume.Set(_volume, _noWaitOptions));
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlaybackRate(double playbackRate)
    {
        if (_disposed)
        {
            return;
        }

        _requestedPlaybackRate = Math.Clamp(playbackRate, 0.25d, 2d);
        if (_isLiveSource)
        {
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_mpvContext is { } mpvContext)
        {
            SetOption(() => mpvContext.Speed.Set(_requestedPlaybackRate, _syncOptions));
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVideoZoom(double zoom, double centerX, double centerY, double viewportWidth, double viewportHeight)
    {
        _requestedVideoZoom = Math.Clamp(zoom, 1d, 4d);
        _requestedVideoZoomCenterX = Math.Clamp(centerX, 0d, 1d);
        _requestedVideoZoomCenterY = Math.Clamp(centerY, 0d, 1d);

        if (_disposed || _mpvContext is not { } mpvContext)
        {
            return;
        }

        ApplyVideoZoom(mpvContext, _requestedVideoZoom, _requestedVideoZoomCenterX, _requestedVideoZoomCenterY);
    }

    public void ToggleMute()
    {
        if (_disposed)
        {
            return;
        }

        _isMuted = !_isMuted;
        if (_mpvContext is { } mpvContext)
        {
            SetOption(() => mpvContext.Mute.Set(_isMuted, _noWaitOptions));
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _positionTimer.Stop();
        if (_mpvContext is { } mpvContext)
        {
            TryInvokeCommand(() => mpvContext.Stop().Invoke(_noWaitCommandOptions));
            DetachContextEvents();
        }

        _contextReadySource = new TaskCompletionSource<MpvContext>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<MpvContext> WaitForReadyContextAsync(CancellationToken cancellationToken)
    {
        if (_mpvContext is { } mpvContext)
        {
            return mpvContext;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RendererReadyTimeout);

        try
        {
            return await _contextReadySource.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("mpv renderer is not ready.");
        }
    }

    private void AttachContextEvents(MpvContext mpvContext)
    {
        mpvContext.FileLoaded += OnMpvFileLoaded;
        mpvContext.VideoReconfig += OnMpvVideoReconfig;
        mpvContext.EndFile += OnMpvEndFile;
    }

    private void DetachContextEvents()
    {
        if (_mpvContext is not { } mpvContext)
        {
            return;
        }

        mpvContext.FileLoaded -= OnMpvFileLoaded;
        mpvContext.VideoReconfig -= OnMpvVideoReconfig;
        mpvContext.EndFile -= OnMpvEndFile;
    }

    private void OnMpvFileLoaded(object? sender, EventArgs e)
    {
        _hasLoadedCurrentFile = true;
        RefreshMetadataFromPlayer();
        if (_mpvContext is { } mpvContext)
        {
            ApplyVideoZoom(mpvContext, _requestedVideoZoom, _requestedVideoZoomCenterX, _requestedVideoZoomCenterY);
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryLoadCurrentFile(MpvContext mpvContext, bool pause, out Exception? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return false;
        }

        try
        {
            TryInvokeCommand(() => mpvContext.Stop().Invoke(_commandOptions));
            ConfigureInputLatency(mpvContext);
            mpvContext.LoadFile(
                _currentFilePath,
                append: false,
                appendPlay: false,
                new Dictionary<string, object>()).Invoke(_commandOptions);
            _hasLoadedCurrentFile = true;
            SetOption(() => mpvContext.Pause.Set(pause, _syncOptions));
            return true;
        }
        catch (Exception ex)
        {
            _hasLoadedCurrentFile = false;
            error = ex;
            return false;
        }
    }

    private void ResumePlayback(MpvContext mpvContext)
    {
        SetOption(() => mpvContext.Speed.Set(_isLiveSource ? 1d : _requestedPlaybackRate, _syncOptions));
        SetOption(() => mpvContext.Pause.Set(false, _syncOptions));
    }

    private void SeekAttachedContextToFrame(MpvContext mpvContext, long frame)
    {
        var safeFrame = Math.Clamp(frame, 0, Math.Max(0, DurationFrames));
        if (safeFrame <= 0)
        {
            CurrentFrame = 0;
            FrameChanged?.Invoke(this, CurrentFrame);
            return;
        }

        var seconds = safeFrame / Math.Max(1d, FramesPerSecond);
        TryInvokeCommand(() => mpvContext.Seek(seconds, SeekOption.Absolute | SeekOption.Exact).Invoke(_commandOptions));
        CurrentFrame = safeFrame;
        FrameChanged?.Invoke(this, CurrentFrame);
    }

    private void OnMpvVideoReconfig(object? sender, EventArgs e)
    {
        RefreshMetadataFromPlayer();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMpvEndFile(object? sender, EventArgs e)
    {
        _isPlaying = false;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ConfigureMpv(MpvContext mpvContext)
    {
        SetOptionString(mpvContext, "input-default-bindings", "no");
        SetOptionString(mpvContext, "input-vo-keyboard", "no");
        SetOptionString(mpvContext, "force-window", "no");
        SetOptionString(mpvContext, "osc", "no");
        SetOptionString(mpvContext, "keep-open", "yes");
        SetOptionString(mpvContext, "hwdec", "auto-safe");
        SetOptionString(mpvContext, "vd-lavc-dr", "yes");
        SetOptionString(mpvContext, "framedrop", "vo");
        SetOptionString(mpvContext, "demuxer-seekable-cache", "yes");
        SetOptionString(mpvContext, "cache-pause", "no");
        SetOptionString(mpvContext, "osd-level", "0");
        SetOptionString(mpvContext, "video-sync", "display-vdrop");
    }

    private void ConfigureInputLatency(MpvContext mpvContext)
    {
        if (_isLiveSource)
        {
            SetOptionString(mpvContext, "profile", "low-latency");
            SetOptionString(mpvContext, "cache", "no");
            SetOptionString(mpvContext, "cache-pause", "no");
            SetOptionString(mpvContext, "cache-secs", "0");
            SetOptionString(mpvContext, "demuxer-cache-wait", "no");
            SetOptionString(mpvContext, "demuxer-readahead-secs", "0");
            SetOptionString(mpvContext, "demuxer-max-bytes", "65536");
            SetOptionString(mpvContext, "demuxer-max-back-bytes", "0");
            SetOptionString(mpvContext, "demuxer-lavf-o", "fflags=+nobuffer+discardcorrupt,probesize=32,analyzeduration=0");
            SetOptionString(mpvContext, "vd-lavc-threads", "1");
            SetOptionString(mpvContext, "untimed", "no");
            SetOptionString(mpvContext, "framedrop", "decoder+vo");
            SetOptionString(mpvContext, "video-sync", "display-vdrop");
            return;
        }

        SetOptionString(mpvContext, "profile", "default");
        SetOptionString(mpvContext, "untimed", "no");
        SetOptionString(mpvContext, "cache", "auto");
        SetOptionString(mpvContext, "demuxer-readahead-secs", "1");
        SetOptionString(mpvContext, "demuxer-max-bytes", "150M");
        SetOptionString(mpvContext, "vd-lavc-threads", "0");
        SetOptionString(mpvContext, "framedrop", "vo");
        SetOptionString(mpvContext, "video-sync", "display-vdrop");
    }

    private bool CanLoadCurrentSource()
    {
        return !string.IsNullOrWhiteSpace(_currentFilePath)
            && (_isLiveSource || File.Exists(_currentFilePath));
    }

    private static string BuildDirectShowCameraSource(string cameraName)
    {
        return $"av://dshow:video={cameraName}";
    }

    private static void SetOptionString(MpvContext mpvContext, string name, string value)
    {
        try
        {
            mpvContext.SetOptionString(name, value);
        }
        catch
        {
            // Some libmpv builds may not expose every option; unsupported tuning is safe to ignore.
        }
    }

    private async Task PollMetadataAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MetadataPollTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMetadataFromPlayer();
            if ((_isLiveSource || DurationFrames > 1) && VideoWidth > 0 && VideoHeight > 0)
            {
                return;
            }

            await Task.Delay(MetadataPollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        RefreshMetadataFromPlayer();
    }

    private void RefreshMetadataFromPlayer()
    {
        if (_mpvContext is not { } mpvContext)
        {
            return;
        }

        var fps = ReadSingle(() => mpvContext.ContainerFps.Get())
            ?? ReadSingle(() => mpvContext.EstimatedVideoFilterFps.Get())
            ?? FramesPerSecond;
        if (fps > 0.01d)
        {
            FramesPerSecond = fps;
        }

        var duration = ReadDouble(() => mpvContext.Duration.Get());
        if (duration is > 0)
        {
            DurationFrames = Math.Max(1, (long)Math.Round(duration.Value * FramesPerSecond));
        }

        var width = ReadInt(() => mpvContext.DisplayWidth.Get()) ?? ReadInt(() => mpvContext.Width.Get());
        var height = ReadInt(() => mpvContext.DisplayHeight.Get()) ?? ReadInt(() => mpvContext.Height.Get());
        if (width is > 0 && height is > 0)
        {
            VideoWidth = width.Value;
            VideoHeight = height.Value;
        }
    }

    private void RefreshPosition()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_currentFilePath) || _mpvContext is not { } mpvContext)
        {
            return;
        }

        var time = ReadDouble(() => mpvContext.TimePos.Get());
        if (time is >= 0)
        {
            var rawFrame = (long)Math.Round(time.Value * FramesPerSecond);
            if (_isLiveSource)
            {
                DurationFrames = Math.Max(
                    DurationFrames,
                    rawFrame + Math.Max(1, (long)Math.Round(FramesPerSecond * 10d)));
            }

            var frame = _isLiveSource
                ? Math.Max(0, rawFrame)
                : Math.Clamp(rawFrame, 0, Math.Max(0, DurationFrames));
            if (frame != CurrentFrame)
            {
                CurrentFrame = frame;
                FrameChanged?.Invoke(this, CurrentFrame);
            }
        }

        var pause = ReadBool(() => mpvContext.Pause.Get());
        if (pause.HasValue)
        {
            var nextIsPlaying = !pause.Value;
            if (nextIsPlaying != _isPlaying)
            {
                _isPlaying = nextIsPlaying;
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ApplyVideoZoom(MpvContext mpvContext, double zoom, double centerX, double centerY)
    {
        var normalizedZoom = Math.Clamp(zoom, 1d, 4d);
        var panX = Math.Clamp((0.5d - Math.Clamp(centerX, 0d, 1d)) * 2d, -1d, 1d);
        var panY = Math.Clamp((0.5d - Math.Clamp(centerY, 0d, 1d)) * 2d, -1d, 1d);
        var mpvZoom = normalizedZoom <= 1.001d ? 0d : Math.Log(normalizedZoom, 2d);
        var panScan = normalizedZoom <= 1.001d ? 0d : 1d;

        SetOption(() => mpvContext.PanScan.Set(panScan, _noWaitOptions));
        SetOption(() => mpvContext.VideoZoom.Set(mpvZoom, _noWaitOptions));
        SetOption(() => mpvContext.VideoPanX.Set(panX, _noWaitOptions));
        SetOption(() => mpvContext.VideoPanY.Set(panY, _noWaitOptions));
    }

    private void TryInvokeCommand(Action command)
    {
        try
        {
            command();
        }
        catch
        {
        }
    }

    private void SetOption(Action setter)
    {
        try
        {
            setter();
        }
        catch
        {
        }
    }

    private static double? ReadDouble(Func<double?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadSingle(Func<float?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBool(Func<bool?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}
