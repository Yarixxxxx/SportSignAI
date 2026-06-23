using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Dtos;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Infrastructure.Services;

public sealed class FfmpegClipComposerService : IClipComposerService
{
    private const int ExportVideoCrf = 16;
    private const string ExportVideoPreset = "medium";
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly string _ffmpegPath;

    public FfmpegClipComposerService(string ffmpegPath)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public IReadOnlyList<ClipSegmentDto> BuildSegments(IEnumerable<TagEvent> events, ClipRecipe recipe, long maxFrame)
    {
        if (maxFrame <= 0)
        {
            return [];
        }

        var lastFrame = Math.Max(0, maxFrame - 1);
        return events
            .Where((tagEvent) =>
                !tagEvent.IsOpen &&
                (!recipe.TagPresetId.HasValue || tagEvent.TagPresetId == recipe.TagPresetId.Value) &&
                (string.IsNullOrWhiteSpace(recipe.Player) || string.Equals(tagEvent.Player, recipe.Player, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(recipe.Period) || string.Equals(tagEvent.Period, recipe.Period, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(recipe.QueryText) || (!string.IsNullOrWhiteSpace(tagEvent.Notes) && tagEvent.Notes.Contains(recipe.QueryText, StringComparison.OrdinalIgnoreCase))))
            .Select((tagEvent) =>
            {
                var requestedStart = tagEvent.StartFrame - recipe.PreRollFrames;
                var requestedEnd = tagEvent.EndFrame + recipe.PostRollFrames;
                var start = Math.Clamp(requestedStart, 0, lastFrame);
                var end = Math.Clamp(requestedEnd, start, lastFrame);
                return new ClipSegmentDto(tagEvent.Id, start, end, recipe.Name, tagEvent.Player);
            })
            .OrderBy((segment) => segment.StartFrame)
            .ToList();
    }

    public async Task<string> ComposeAsync(
        string sourceVideoPath,
        IReadOnlyList<ClipSegmentDto> segments,
        string outputPath,
        double framesPerSecond,
        string? overlayFilterPath,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("No segments were provided.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "video-analysis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var ffmpegExecutablePath = FfmpegExecutableResolver.Resolve(_ffmpegPath);

        try
        {
            var segmentFiles = new List<string>(segments.Count);
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var startSeconds = segment.StartFrame / framesPerSecond;
                var durationSeconds = Math.Max(0.02, (segment.EndFrame - segment.StartFrame + 1) / framesPerSecond);
                var partPath = Path.Combine(tempRoot, $"part_{index:D4}.mp4");
                segmentFiles.Add(partPath);

                var args = string.Join(' ',
                    "-y",
                    $"-i {Quote(sourceVideoPath)}",
                    $"-ss {ToInvariant(startSeconds)}",
                    $"-t {ToInvariant(durationSeconds)}",
                    "-map 0:v:0",
                    "-map 0:a?",
                    $"-c:v libx264 -preset {ExportVideoPreset} -crf {ExportVideoCrf}",
                    "-pix_fmt yuv420p",
                    "-c:a aac -b:a 192k",
                    "-movflags +faststart",
                    "-avoid_negative_ts make_zero",
                    Quote(partPath));

                await RunFfmpegAsync(ffmpegExecutablePath, args, cancellationToken);
            }

            var listPath = Path.Combine(tempRoot, "concat.txt");
            await File.WriteAllTextAsync(listPath, string.Join(Environment.NewLine, segmentFiles.Select((path) => $"file '{path.Replace("'", "''")}'")), cancellationToken);
            var mergedPath = string.IsNullOrWhiteSpace(overlayFilterPath) ? outputPath : Path.Combine(tempRoot, "merged.mp4");

            await RunFfmpegAsync(ffmpegExecutablePath, $"-y -f concat -safe 0 -i {Quote(listPath)} -c copy {Quote(mergedPath)}", cancellationToken);

            if (!string.IsNullOrWhiteSpace(overlayFilterPath))
            {
                await RunFfmpegAsync(
                    ffmpegExecutablePath,
                    $"-y -i {Quote(mergedPath)} -filter_script:v {Quote(overlayFilterPath!)} -c:v libx264 -preset {ExportVideoPreset} -crf {ExportVideoCrf} -pix_fmt yuv420p -c:a copy -movflags +faststart {Quote(outputPath)}",
                    cancellationToken);
            }

            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunFfmpegAsync(string ffmpegExecutablePath, string arguments, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutablePath,
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

    private static string ToInvariant(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private string ResolveFfmpegExecutablePath()
    {
        var configuredPath = _ffmpegPath.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            throw new InvalidOperationException(
                $"FFmpeg не найден по указанному пути '{configuredPath}'. Проверьте путь в settings.json (FfmpegPath) или разместите ffmpeg рядом с приложением.");
        }

        foreach (var candidate in EnumerateFfmpegCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"FFmpeg не найден. Проверьте установку {(IsWindows ? "ffmpeg.exe" : "ffmpeg")} или укажите путь в settings.json (FfmpegPath). Текущее значение: '{_ffmpegPath}'.");
    }

    private IEnumerable<string> EnumerateFfmpegCandidates()
    {
        var configuredPath = _ffmpegPath.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathRooted(configuredPath))
            {
                yield return Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory());
                yield return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
            }
        }

        var executableName = IsWindows ? "ffmpeg.exe" : "ffmpeg";
        var candidateNames = new[]
        {
            executableName,
            Path.Combine("tools", executableName),
            Path.Combine("tools", "ffmpeg", executableName),
            Path.Combine("tools", "ffmpeg", "bin", executableName)
        };

        foreach (var candidateName in candidateNames)
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidateName);
            yield return Path.Combine(Directory.GetCurrentDirectory(), candidateName);
        }

        var documentsToolsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Video Analytics",
            "Tools");
        yield return Path.Combine(documentsToolsRoot, executableName);
        yield return Path.Combine(documentsToolsRoot, "ffmpeg", executableName);
        yield return Path.Combine(documentsToolsRoot, "ffmpeg", "bin", executableName);

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
