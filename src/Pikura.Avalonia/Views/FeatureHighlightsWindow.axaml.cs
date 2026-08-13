using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Pikura.Avalonia.Controls;
using Pikura.Avalonia.Converters;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views;

public partial class FeatureHighlightsWindow : Window
{
    private const double StaticImageSeconds = 3;
    private const double FallbackGifSeconds = 4;

    // Auto-advances a page's screenshot/GIF carousel. GIFs get a full loop to finish (their
    // real decoded duration) before advancing; static images just get a fixed dwell time.
    // Manually picking a media item pauses the carousel for that page, but it resumes on its
    // own after a minute of no further interaction rather than staying paused forever.
    private readonly DispatcherTimer _autoplayTimer = new();
    private readonly DispatcherTimer _resumeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _pausedForCurrentPage;

    public FeatureHighlightsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = TransformOperations.Parse("scale(1)");
        };
        _autoplayTimer.Tick += OnAutoplayTick;
        _resumeTimer.Tick += OnResumeTick;
    }

    public FeatureHighlightsWindow(FeatureHighlightsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += () => Close(true);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FeatureHighlightsViewModel.CurrentPageIndex))
            {
                _pausedForCurrentPage = false;
                _resumeTimer.Stop();
                RestartAutoplayForCurrentMedia();
            }
        };
        Closed += (_, _) =>
        {
            _autoplayTimer.Stop();
            _resumeTimer.Stop();
            AnimatedImage.ClearCache();
        };
        RestartAutoplayForCurrentMedia();
    }

    private void OnAutoplayTick(object? sender, EventArgs e)
    {
        if (_pausedForCurrentPage) return;
        if (DataContext is not FeatureHighlightsViewModel { CurrentPage: { HasMultipleScreenshots: true } page }) return;
        page.AdvanceScreenshot();
        RestartAutoplayForCurrentMedia();
    }

    private void OnMediaDotClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || DataContext is not FeatureHighlightsViewModel { CurrentPage: { } page }) return;
        page.SelectScreenshot(path);
        _pausedForCurrentPage = true;
        _autoplayTimer.Stop();
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }

    /// <summary>After a minute with no further interaction, resume auto-advancing instead of
    /// staying paused for the rest of the session.</summary>
    private void OnResumeTick(object? sender, EventArgs e)
    {
        _resumeTimer.Stop();
        _pausedForCurrentPage = false;
        RestartAutoplayForCurrentMedia();
    }

    /// <summary>(Re)starts the autoplay timer with an interval matching whatever media is
    /// currently selected — a GIF's own decoded loop length, or a fixed dwell time for a static
    /// image — so GIFs always finish playing before the carousel moves on.</summary>
    private void RestartAutoplayForCurrentMedia()
    {
        _autoplayTimer.Stop();
        if (DataContext is not FeatureHighlightsViewModel { CurrentPage: { HasMultipleScreenshots: true } page }) return;

        _autoplayTimer.Interval = TimeSpan.FromSeconds(GetDwellSeconds(page.SelectedScreenshot));
        _autoplayTimer.Start();
    }

    private static double GetDwellSeconds(string? avaresPath)
    {
        if (avaresPath is null || !avaresPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return StaticImageSeconds;

        if (AssetPathConverter.Instance.Convert(avaresPath, typeof(string), null, CultureInfo.InvariantCulture)
                is string localPath
            && AnimatedImage.GetTotalDurationMs(localPath) is { } durationMs)
        {
            return durationMs / 1000.0;
        }

        return FallbackGifSeconds;
    }
}
