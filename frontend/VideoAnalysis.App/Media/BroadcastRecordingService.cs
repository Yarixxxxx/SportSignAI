using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VideoAnalysis.App.Media;

public sealed class BroadcastRecordingService : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private Task<string>? _stderrTask;
    private Task<string>? _stdoutTask;
    private string? _outputPath;
    private bool _disposed;

    public BroadcastRecordingService(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
    }

    public bool IsRecording => _process is { HasExited: false };

    public async Task<BroadcastRecordingSession> StartAsync(
        string outputFolderPath,
        string? cameraName,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BroadcastRecordingService));
        }

        if (IsRecording)
        {
            throw new InvalidOperationException("Broadcast recording is already running.");
        }

        var resolvedCameraName = ResolveCameraName(cameraName);
        Directory.CreateDirectory(outputFolderPath);
        var previewPort = FindAvailableUdpPort();
        var previewSource = BuildPreviewSource(previewPort);

        _outputPath = Path.Combine(
            outputFolderPath,
            $"broadcast-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.mp4");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            Arguments = BuildFfmpegArguments(resolvedCameraName, _outputPath, previewPort),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start ffmpeg broadcast recorder.");
        _stderrTask = _process.StandardError.ReadToEndAsync(cancellationToken);
        _stdoutTask = _process.StandardOutput.ReadToEndAsync(cancellationToken);

        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        if (_process.HasExited)
        {
            var details = await ReadProcessOutputAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"ffmpeg recorder exited immediately. {details}");
        }

        return new BroadcastRecordingSession(_outputPath, previewSource);
    }

    public async Task<string?> StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var outputPath = _outputPath;
        _process = null;
        _outputPath = null;

        if (process is null)
        {
            return outputPath;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var details = await ReadProcessOutputAsync().ConfigureAwait(false);
            if (process.ExitCode != 0 && (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath)))
            {
                throw new InvalidOperationException($"ffmpeg recorder exited with code {process.ExitCode}. {details}");
            }

            return outputPath;
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        _process?.Dispose();
        _process = null;
    }

    private static string ResolveCameraName(string? cameraName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new InvalidOperationException("Broadcast recording from camera is currently available on Windows only.");
        }

        var resolvedCameraName = string.IsNullOrWhiteSpace(cameraName)
            ? WindowsCameraDeviceEnumerator.GetVideoCaptureDevices().FirstOrDefault()?.Name
            : cameraName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedCameraName))
        {
            throw new InvalidOperationException("Camera was not found.");
        }

        return resolvedCameraName;
    }

    private string ResolveFfmpegPath()
    {
        if (Path.IsPathRooted(_ffmpegPath))
        {
            if (File.Exists(_ffmpegPath))
            {
                return _ffmpegPath;
            }

            throw new InvalidOperationException($"FFmpeg was not found at '{_ffmpegPath}'.");
        }

        foreach (var candidate in EnumerateFfmpegCandidates(_ffmpegPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return _ffmpegPath;
    }

    private static IEnumerable<string> EnumerateFfmpegCandidates(string configuredPath)
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.IsPathRooted(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory());
            yield return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
        }

        foreach (var candidateName in new[]
        {
            executableName,
            Path.Combine("tools", executableName),
            Path.Combine("tools", "ffmpeg", executableName),
            Path.Combine("tools", "ffmpeg", "bin", executableName)
        })
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidateName);
            yield return Path.Combine(Directory.GetCurrentDirectory(), candidateName);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            yield break;
        }

        foreach (var pathEntry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(pathEntry, executableName);
        }
    }

    private static string BuildFfmpegArguments(string cameraName, string outputPath, int previewPort)
    {
        var previewTarget = $"udp://127.0.0.1:{previewPort}?pkt_size=1316";

        return string.Join(' ',
            "-y",
            "-f dshow",
            "-rtbufsize 512M",
            $"-i {Quote($"video={cameraName}")}",
            "-map 0:v:0",
            "-an",
            "-c:v libx264",
            "-preset veryfast",
            "-tune zerolatency",
            "-pix_fmt yuv420p",
            "-movflags +faststart",
            Quote(outputPath),
            "-map 0:v:0",
            "-an",
            "-c:v libx264",
            "-preset ultrafast",
            "-tune zerolatency",
            "-pix_fmt yuv420p",
            "-muxdelay 0",
            "-muxpreload 0",
            "-f mpegts",
            Quote(previewTarget));
    }

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string BuildPreviewSource(int previewPort)
    {
        return $"udp://127.0.0.1:{previewPort}?fifo_size=50000000&overrun_nonfatal=1";
    }

    private async Task<string> ReadProcessOutputAsync()
    {
        var stderr = _stderrTask is null ? string.Empty : await _stderrTask.ConfigureAwait(false);
        var stdout = _stdoutTask is null ? string.Empty : await _stdoutTask.ConfigureAwait(false);
        return $"{stderr}{stdout}".Trim();
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

public sealed record BroadcastRecordingSession(string OutputPath, string PreviewSource);
