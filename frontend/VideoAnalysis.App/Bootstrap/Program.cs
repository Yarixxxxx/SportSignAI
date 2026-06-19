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
            AppLogService.Info("Building Avalonia app.", "Startup");
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
        AppLogService.Info("Configuring Avalonia platform.", "Startup");
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                ]
            });
        }

        AppLogService.Info("Avalonia app builder configured.", "Startup");
        return builder;
    }
}
