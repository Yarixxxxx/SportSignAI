using System.Runtime.InteropServices;

namespace VideoAnalysis.Infrastructure.Services;

internal static class FfmpegExecutableResolver
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string Resolve(string ffmpegPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        if (Path.IsPathRooted(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            throw new InvalidOperationException(
                $"FFmpeg was not found at '{configuredPath}'. Check FfmpegPath in settings.json or place ffmpeg next to the application.");
        }

        foreach (var candidate in EnumerateCandidates(configuredPath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"FFmpeg was not found. Install {(IsWindows ? "ffmpeg.exe" : "ffmpeg")} or set FfmpegPath in settings.json. Current value: '{configuredPath}'.");
    }

    private static IEnumerable<string> EnumerateCandidates(string configuredPath)
    {
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
}
