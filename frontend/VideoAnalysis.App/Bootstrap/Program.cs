using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Win32;
using System.Diagnostics;
using VideoAnalysis.App.Services;

namespace VideoAnalysis.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogService.InitializeDefault();
        Trace.Listeners.Add(new AppLogTraceListener());
        Trace.AutoFlush = true;
        AppLogService.InstallGlobalExceptionHandlers();
        AppLogService.Info("Application process starting.");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLogService.Info("Application process exited.");
        }
        catch (Exception ex)
        {
            AppLogService.Fatal(ex, "Top-level application crash");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                ]
            })
            .LogToTrace()
            .UseReactiveUI();
    }
}
