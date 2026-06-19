using System.Runtime.InteropServices;

namespace VideoAnalysis.App.Services;

internal static class MpvRuntimeProbe
{
    public static bool IsAvailable(out string? errorMessage)
    {
        if (OperatingSystem.IsWindows())
        {
            errorMessage = null;
            return true;
        }

        var candidates = OperatingSystem.IsMacOS()
            ? GetMacOsCandidates()
            : GetUnixCandidates();

        foreach (var candidate in candidates)
        {
            if (TryLoadCandidate(candidate))
            {
                AppLogService.Info($"libmpv runtime probe succeeded: {candidate}", "Startup");
                errorMessage = null;
                return true;
            }
        }

        errorMessage = OperatingSystem.IsMacOS()
            ? "Не найден runtime libmpv для macOS. Установите libmpv/mpv на систему или добавьте libmpv.dylib в пакет приложения."
            : "Не найден runtime libmpv для текущей системы.";
        return false;
    }

    private static IEnumerable<string> GetMacOsCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;

        return
        [
            Path.Combine(baseDirectory, "libmpv.2.dylib"),
            Path.Combine(baseDirectory, "libmpv.dylib"),
            Path.Combine(baseDirectory, "Frameworks", "libmpv.2.dylib"),
            Path.Combine(baseDirectory, "Frameworks", "libmpv.dylib"),
            "libmpv.2.dylib",
            "libmpv.dylib"
        ];
    }

    private static IEnumerable<string> GetUnixCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;

        return
        [
            Path.Combine(baseDirectory, "libmpv.so.2"),
            Path.Combine(baseDirectory, "libmpv.so"),
            "libmpv.so.2",
            "libmpv.so"
        ];
    }

    private static bool TryLoadCandidate(string candidate)
    {
        try
        {
            if (!NativeLibrary.TryLoad(candidate, out var handle) || handle == IntPtr.Zero)
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Warning(ex, $"libmpv runtime probe failed for '{candidate}'");
            return false;
        }
    }
}
