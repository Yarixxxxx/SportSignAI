using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VideoAnalysis.App.Views;

public partial class SplashWindow : Window
{
    private static readonly Uri[] ImageUris =
    [
        new("avares://VideoAnalysis.App/Assets/Splash/splash-hockey-1.png"),
        new("avares://VideoAnalysis.App/Assets/Splash/splash-hockey-2.png"),
        new("avares://VideoAnalysis.App/Assets/Splash/splash-hockey-3.png")
    ];

    private readonly List<Bitmap> _images = [];
    private readonly DispatcherTimer _imageTimer;
    private readonly DispatcherTimer _progressTimer;
    private int _imageIndex;
    private double _progress;

    public SplashWindow()
    {
        InitializeComponent();
        LoadImages();

        _imageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _imageTimer.Tick += (_, _) => ShowNextImage();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _progressTimer.Tick += (_, _) => AdvanceProgress();

        Opened += (_, _) =>
        {
            _imageTimer.Start();
            _progressTimer.Start();
        };

        Closed += (_, _) =>
        {
            _imageTimer.Stop();
            _progressTimer.Stop();

            foreach (var image in _images)
            {
                image.Dispose();
            }
        };
    }

    public async Task CompleteAsync()
    {
        _progress = 100;
        UpdateProgress();
        SplashStatusText.Text = "Готово";
        await Task.Delay(220);
    }

    private Image SplashHeroImage => this.FindControl<Image>(nameof(SplashHeroImage))
        ?? throw new InvalidOperationException("SplashHeroImage was not found.");

    private ProgressBar SplashProgress => this.FindControl<ProgressBar>(nameof(SplashProgress))
        ?? throw new InvalidOperationException("SplashProgress was not found.");

    private TextBlock SplashPercentText => this.FindControl<TextBlock>(nameof(SplashPercentText))
        ?? throw new InvalidOperationException("SplashPercentText was not found.");

    private TextBlock SplashStatusText => this.FindControl<TextBlock>(nameof(SplashStatusText))
        ?? throw new InvalidOperationException("SplashStatusText was not found.");

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadImages()
    {
        foreach (var uri in ImageUris)
        {
            using var stream = AssetLoader.Open(uri);
            _images.Add(new Bitmap(stream));
        }

        if (_images.Count > 0)
        {
            SplashHeroImage.Source = _images[0];
        }
    }

    private void ShowNextImage()
    {
        if (_images.Count == 0)
        {
            return;
        }

        _imageIndex = (_imageIndex + 1) % _images.Count;
        SplashHeroImage.Source = _images[_imageIndex];
    }

    private void AdvanceProgress()
    {
        if (_progress >= 94)
        {
            return;
        }

        var remaining = 96 - _progress;
        _progress += Math.Max(0.25, remaining * 0.025);
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var value = Math.Clamp(_progress, 0, 100);
        SplashProgress.Value = value;
        SplashPercentText.Text = $"{(int)Math.Round(value)}%";
    }
}
