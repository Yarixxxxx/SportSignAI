using System.Diagnostics;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Infrastructure.Services;

public sealed class FfmpegVideoProxyService : IVideoProxyService
{
    private const string ProxyProfileVersion = "v2";
    private const int MaxProxyWidth = 1920;
    private const int ProxyCrf = 20;
    private readonly string _ffmpegPath;

    public FfmpegVideoProxyService(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public async Task<VideoProxyResult> EnsureProxyAsync(
        string sourceVideoPath,
        string projectFolderPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoPath))
        {
            throw new ArgumentException("Source video path is required.", nameof(sourceVideoPath));
        }

        if (string.IsNullOrWhiteSpace(projectFolderPath))
        {
            throw new ArgumentException("Project folder path is required.", nameof(projectFolderPath));
        }

        var sourcePath = Path.GetFullPath(sourceVideoPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source video file was not found.", sourcePath);
        }

        var proxyFolderPath = Path.Combine(Path.GetFullPath(projectFolderPath), "media", "proxy");
        Directory.CreateDirectory(proxyFolderPath);

        var proxyFileName = $"{SanitizeForPath(Path.GetFileNameWithoutExtension(sourcePath))}.analysis-proxy-{ProxyProfileVersion}.mp4";
        var proxyPath = Path.Combine(proxyFolderPath, proxyFileName);
        if (IsProxyFresh(sourcePath, proxyPath))
        {
            return new VideoProxyResult(proxyPath, Created: false);
        }

        var tempProxyPath = Path.Combine(proxyFolderPath, $"{Path.GetFileNameWithoutExtension(proxyFileName)}.{Guid.NewGuid():N}.tmp.mp4");
        try
        {
            await RunFfmpegAsync(BuildProxyArguments(sourcePath, tempProxyPath), cancellationToken);

            if (File.Exists(proxyPath))
            {
                File.Delete(proxyPath);
            }

            File.Move(tempProxyPath, proxyPath);
            return new VideoProxyResult(proxyPath, Created: true);
        }
        finally
        {
            TryDeleteFile(tempProxyPath);
        }
    }

    private static bool IsProxyFresh(string sourcePath, string proxyPath)
    {
        if (!File.Exists(proxyPath))
        {
            return false;
        }

        var sourceInfo = new FileInfo(sourcePath);
        var proxyInfo = new FileInfo(proxyPath);
        return proxyInfo.Length > 0 && proxyInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc;
    }

    private static string BuildProxyArguments(string sourcePath, string outputPath)
    {
        var scaleFilter = $"scale=w='min({MaxProxyWidth},iw)':h=-2";
        return string.Join(' ',
            "-y",
            $"-i {Quote(sourcePath)}",
            "-map 0:v:0",
            "-map 0:a?",
            "-sn -dn",
            $"-vf {Quote(scaleFilter)}",
            $"-c:v libx264 -preset veryfast -tune fastdecode -crf {ProxyCrf}",
            "-g 1 -keyint_min 1 -sc_threshold 0",
            "-pix_fmt yuv420p",
            "-c:a aac -b:a 128k",
            "-movflags +faststart",
            Quote(outputPath));
    }

    private async Task RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = FfmpegExecutableResolver.Resolve(_ffmpegPath),
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg process.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var stdErr = await stdErrTask;
            var stdOut = await stdOutTask;
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. {stdErr}{stdOut}");
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string SanitizeForPath(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "video" : sanitized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
