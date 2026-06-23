using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoAnalysis.Infrastructure.Services;

internal static class FfmpegExecutableResolver
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string Resolve(string ffmpegPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        var probeErrors = new List<string>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Path.IsPathRooted(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException(
                    $"FFmpeg was not found at '{configuredPath}'. Check FfmpegPath in settings.json or place ffmpeg next to the application.");
            }

            if (TryUseCandidate(configuredPath, seenCandidates, probeErrors, out var configuredCandidate))
            {
                return configuredCandidate;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new InvalidOperationException(
                    $"FFmpeg at '{configuredPath}' is not usable. {string.Join(Environment.NewLine, probeErrors)}");
            }
        }

        foreach (var candidate in EnumerateCandidates(configuredPath, preferBundled: true))
        {
            if (TryUseCandidate(candidate, seenCandidates, probeErrors, out var resolvedCandidate))
            {
                return resolvedCandidate;
            }
        }

        var details = probeErrors.Count == 0
            ? $"Current value: '{configuredPath}'."
            : string.Join(Environment.NewLine, probeErrors);
        throw new InvalidOperationException(
            $"FFmpeg was not found or is not usable. Install {(IsWindows ? "ffmpeg.exe" : "ffmpeg")} or set FfmpegPath in settings.json. {details}");
    }

    private static IEnumerable<string> EnumerateCandidates(string configuredPath, bool preferBundled)
    {
        if (preferBundled)
        {
            foreach (var candidate in EnumerateBundledCandidates())
            {
                yield return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.IsPathRooted(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath, Directory.GetCurrentDirectory());
            yield return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
        }

        var executableName = IsWindows ? "ffmpeg.exe" : "ffmpeg";
        var candidateNames = new[]
        {
            executableName,
            Path.Combine("tools", executableName),
            Path.Combine("tools", "ffmpeg", executableName),
            Path.Combine("tools", "ffmpeg", "bin", executableName),
            Path.Combine("tools", "ffmpeg", "macos-arm64", "unpacked", executableName),
            Path.Combine("tools", "ffmpeg", "macos-x64", "unpacked", executableName)
        };

        foreach (var candidateName in candidateNames)
        {
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

    private static IEnumerable<string> EnumerateBundledCandidates()
    {
        var executableName = IsWindows ? "ffmpeg.exe" : "ffmpeg";
        foreach (var candidateName in new[]
                 {
                     executableName,
                     Path.Combine("tools", executableName),
                     Path.Combine("tools", "ffmpeg", executableName),
                     Path.Combine("tools", "ffmpeg", "bin", executableName),
                     Path.Combine("tools", "ffmpeg", "macos-arm64", "unpacked", executableName),
                     Path.Combine("tools", "ffmpeg", "macos-x64", "unpacked", executableName)
                 })
        {
            yield return Path.Combine(AppContext.BaseDirectory, candidateName);
        }
    }

    private static bool TryUseCandidate(
        string candidate,
        ISet<string> seenCandidates,
        ICollection<string> probeErrors,
        out string resolvedCandidate)
    {
        resolvedCandidate = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            probeErrors.Add($"FFmpeg candidate '{candidate}' is invalid: {ex.Message}");
            return false;
        }

        if (!seenCandidates.Add(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        if (IsUsableFfmpeg(fullPath, out var error))
        {
            resolvedCandidate = fullPath;
            return true;
        }

        probeErrors.Add($"FFmpeg at '{fullPath}' is not usable: {error}");
        return false;
    }

    private static bool IsUsableFfmpeg(string ffmpegPath, out string error)
    {
        error = string.Empty;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                error = "process did not start.";
                return false;
            }

            if (!process.WaitForExit(2_000))
            {
                process.Kill(entireProcessTree: true);
                error = "version probe timed out.";
                return false;
            }

            if (process.ExitCode == 0)
            {
                return true;
            }

            var output = $"{process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}".Trim();
            error = string.IsNullOrWhiteSpace(output)
                ? $"version probe exited with code {process.ExitCode}."
                : output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                  ?? $"version probe exited with code {process.ExitCode}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
