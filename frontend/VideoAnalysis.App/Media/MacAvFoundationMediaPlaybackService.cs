using Avalonia.Threading;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.App.Media;

public sealed class MacAvFoundationMediaPlaybackService : IMediaPlaybackService, IDisposable
{
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(100);
    private const double FallbackFramesPerSecond = 30d;

    private readonly DispatcherTimer _positionTimer;
    private MacAvFoundationVideoView? _renderer;
    private string? _currentSource;
    private string? _metadataPath;
    private bool _isLiveSource;
    private bool _isPlaying;
    private bool _isMuted;
    private int _volume = 100;
    private double _playbackRate = 1d;
    private bool _disposed;

    public MacAvFoundationMediaPlaybackService()
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

    public bool IsPlaying => _renderer?.IsPlaying ?? _isPlaying;
    public bool IsMuted => _isMuted;
    public long CurrentFrame { get; private set; }
    public long DurationFrames { get; private set; } = 1;
    public double FramesPerSecond { get; private set; } = FallbackFramesPerSecond;
    public long VideoWidth { get; private set; }
    public long VideoHeight { get; private set; }
    public int Volume => _volume;
    public double PlaybackRate => _playbackRate;

    public void AttachRenderer(MacAvFoundationVideoView renderer)
    {
        if (_disposed || ReferenceEquals(_renderer, renderer))
        {
            return;
        }

        _renderer = renderer;
        renderer.SetVolume(_volume);
        renderer.SetMuted(_isMuted);
        renderer.SetPlaybackRate(_playbackRate);

        if (!string.IsNullOrWhiteSpace(_currentSource))
        {
            renderer.Open(_currentSource, _isPlaying);
        }
    }

    public Task<MediaMetadata> OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Video file not found.", filePath);
        }

        return OpenSourceAsync(filePath, filePath, isLiveSource: false, startPlaying: false, cancellationToken);
    }

    public Task<MediaMetadata> OpenLiveCameraAsync(string? deviceName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Live camera capture is routed through live-DVR on macOS.");
    }

    public Task<MediaMetadata> OpenLiveStreamAsync(string source, string metadataPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Live stream source is required.", nameof(source));
        }

        return OpenSourceAsync(source, metadataPath, isLiveSource: true, startPlaying: true, cancellationToken);
    }

    public void Close()
    {
        _currentSource = null;
        _metadataPath = null;
        _isPlaying = false;
        _renderer?.ClosePlayer();
        CurrentFrame = 0;
        DurationFrames = 1;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, CurrentFrame);
    }

    public void Play()
    {
        if (_renderer is null || string.IsNullOrWhiteSpace(_currentSource))
        {
            return;
        }

        _renderer.SetPlaybackRate(_playbackRate);
        _renderer.Play();
        _isPlaying = true;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        _renderer?.Pause();
        _isPlaying = false;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekToFrame(long frame)
    {
        CurrentFrame = Math.Max(0, frame);
        if (_renderer is not null)
        {
            _renderer.Seek(CurrentFrame / Math.Max(0.01d, FramesPerSecond));
        }

        FrameChanged?.Invoke(this, CurrentFrame);
    }

    public void StepFrameForward()
    {
        SeekToFrame(CurrentFrame + 1);
    }

    public void StepFrameBackward()
    {
        SeekToFrame(Math.Max(0, CurrentFrame - 1));
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _renderer?.SetVolume(_volume);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlaybackRate(double playbackRate)
    {
        _playbackRate = playbackRate <= 0 ? 1d : playbackRate;
        _renderer?.SetPlaybackRate(_isLiveSource ? 1d : _playbackRate);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVideoZoom(double zoom, double centerX, double centerY, double viewportWidth, double viewportHeight)
    {
        // The first macOS AVFoundation renderer keeps native aspect-fit rendering.
        // Zoom/pan can be layered on top once the native path is stable.
    }

    public void ToggleMute()
    {
        _isMuted = !_isMuted;
        _renderer?.SetMuted(_isMuted);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SyncLiveEdge()
    {
        if (!_isLiveSource || _renderer is null || string.IsNullOrWhiteSpace(_currentSource))
        {
            return;
        }

        _renderer.Open(_currentSource, startPlaying: true);
        _isPlaying = true;
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
        _renderer?.Dispose();
        _renderer = null;
    }

    private Task<MediaMetadata> OpenSourceAsync(
        string source,
        string metadataPath,
        bool isLiveSource,
        bool startPlaying,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _currentSource = source;
        _metadataPath = metadataPath;
        _isLiveSource = isLiveSource;
        _isPlaying = startPlaying;
        CurrentFrame = 0;
        FramesPerSecond = FallbackFramesPerSecond;
        DurationFrames = isLiveSource ? (long)Math.Round(FallbackFramesPerSecond * 10d) : 1;
        VideoWidth = 0;
        VideoHeight = 0;

        _renderer?.Open(source, startPlaying);
        _renderer?.SetVolume(_volume);
        _renderer?.SetMuted(_isMuted);
        _renderer?.SetPlaybackRate(isLiveSource ? 1d : _playbackRate);

        RefreshPosition();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        FrameChanged?.Invoke(this, CurrentFrame);

        return Task.FromResult(new MediaMetadata(
            _metadataPath ?? source,
            FramesPerSecond,
            DurationFrames,
            VideoWidth,
            VideoHeight));
    }

    private void RefreshPosition()
    {
        if (_disposed || _renderer is null)
        {
            return;
        }

        var durationSeconds = _renderer.DurationSeconds;
        if (!_isLiveSource && durationSeconds > 0.01d)
        {
            DurationFrames = Math.Max(1, (long)Math.Round(durationSeconds * FramesPerSecond));
        }

        var nextFrame = (long)Math.Round(_renderer.CurrentSeconds * FramesPerSecond);
        if (nextFrame == CurrentFrame)
        {
            return;
        }

        CurrentFrame = Math.Max(0, nextFrame);
        FrameChanged?.Invoke(this, CurrentFrame);
    }
}
