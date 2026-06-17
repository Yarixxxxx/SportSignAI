using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VideoAnalysis.App.Configuration;
using VideoAnalysis.App.Media;
using VideoAnalysis.App.Services;
using VideoAnalysis.App.ViewModels.Shell;
using VideoAnalysis.App.Views;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Services;
using VideoAnalysis.Infrastructure.Media;
using VideoAnalysis.Infrastructure.Persistence;
using VideoAnalysis.Infrastructure.Services;

namespace VideoAnalysis.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLogService.InstallUiExceptionHandler();
        _serviceProvider = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splashWindow = new SplashWindow();
            desktop.MainWindow = splashWindow;
            splashWindow.Show();
            _ = ShowMainWindowAsync(desktop, splashWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ShowMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splashWindow)
    {
        await Task.Delay(2_200);

        if (_serviceProvider is null)
        {
            splashWindow.Close();
            return;
        }

        var mainWindow = new MainWindow
        {
            DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            WindowState = WindowState.Maximized
        };

        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        await splashWindow.CompleteAsync();
        splashWindow.Close();
    }

    private static ServiceProvider ConfigureServices()
    {
        var documentsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Video Analytics");
        var projectsRootPath = Path.Combine(documentsRoot, "Projects");
        var legacyAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoAnalysis");
        var legacyDatabasePath = Path.Combine(legacyAppDataDir, "video-analysis.db");

        Directory.CreateDirectory(documentsRoot);
        Directory.CreateDirectory(projectsRootPath);

        var settingsStore = new AppSettingsStore(Path.Combine(documentsRoot, "settings.json"));
        var settings = settingsStore.Load();

        var services = new ServiceCollection();
        services.AddSingleton(settingsStore);
        services.AddSingleton(settings);
        services.AddSingleton<IProjectRepository>(_ => new SqliteProjectRepository(projectsRootPath, legacyDatabasePath));
        services.AddSingleton<IProjectSetupService>((provider) =>
            new ProjectSetupService(
                provider.GetRequiredService<IProjectRepository>(),
                projectsRootPath));
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<IEventCaptureService, EventCaptureService>();
        services.AddSingleton<IPlaylistService, PlaylistService>();
        services.AddSingleton<IMediaPlaybackService, MpvMediaPlaybackService>();
        services.AddSingleton<IVideoProxyService>(_ => new FfmpegVideoProxyService(settings.FfmpegPath));
        services.AddSingleton<IClipComposerService>(_ => new FfmpegClipComposerService(settings.FfmpegPath));
        services.AddSingleton<IAnnotationRenderService, AnnotationRenderService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
