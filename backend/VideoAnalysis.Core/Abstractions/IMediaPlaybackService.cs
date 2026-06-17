using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Core.Abstractions;

public interface IMediaPlaybackService
{
    event EventHandler? PlaybackStateChanged;
    event EventHandler<long>? FrameChanged;

    bool IsPlaying { get; }
    bool IsMuted { get; }
    long CurrentFrame { get; }
    long DurationFrames { get; }
    double FramesPerSecond { get; }
    long VideoWidth { get; }
    long VideoHeight { get; }
    int Volume { get; }
    double PlaybackRate { get; }

    Task<MediaMetadata> OpenAsync(string filePath, CancellationToken cancellationToken);
    Task<MediaMetadata> OpenLiveCameraAsync(string? deviceName, CancellationToken cancellationToken);
    void Close();
    void Play();
    void Pause();
    void SeekToFrame(long frame);
    void StepFrameForward();
    void StepFrameBackward();
    void SetVolume(int volume);
    void SetPlaybackRate(double playbackRate);
    void SetVideoZoom(double zoom, double centerX, double centerY, double viewportWidth, double viewportHeight);
    void ToggleMute();
}
