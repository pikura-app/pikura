using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>
/// Live preview window for adjusting a background overlay image before applying.
/// Supports dragging to reposition, and sliders for opacity, brightness, and darkness.
/// </summary>
public partial class BackgroundPreviewWindow : Window
{
    private Bitmap? _bitmap;
    private PixelSize _naturalSize;
    private bool _isDragging;
    private Point _dragStart;
    private double _panX;
    private double _panY;
    private double _zoom = 1.0;
    private CancellationTokenSource? _loadCts;

    /// <summary>The resulting per-image entry after the user clicks Apply. Null if cancelled.</summary>
    public OverlayImageEntry? Result { get; private set; }

    /// <summary>The image path/URL this preview is for.</summary>
    public string ImagePath { get; }

    public BackgroundPreviewWindow() : this("", null, null) { }

    public BackgroundPreviewWindow(string imagePath, byte[]? imageBytes, OverlayImageEntry? existingEntry)
    {
        ImagePath = imagePath;
        InitializeComponent();

        // Wire slider changes to live preview
        OpacitySlider.PropertyChanged += OnSliderChanged;
        BrightnessSlider.PropertyChanged += OnSliderChanged;
        DarknessSlider.PropertyChanged += OnSliderChanged;

        // Wire drag and zoom events on the preview area
        PreviewCanvas.PointerPressed += OnImagePointerPressed;
        PreviewCanvas.PointerMoved += OnImagePointerMoved;
        PreviewCanvas.PointerReleased += OnImagePointerReleased;
        PreviewCanvas.PointerWheelChanged += OnPreviewWheelChanged;

        // Update the image host size/position whenever the viewport or zoom/pan changes
        PreviewCanvas.LayoutUpdated += OnPreviewCanvasLayoutUpdated;

        // Load existing settings if editing
        if (existingEntry != null)
        {
            OpacitySlider.Value = existingEntry.Opacity;
            BrightnessSlider.Value = existingEntry.Brightness;
            DarknessSlider.Value = existingEntry.Darkness;
            _panX = existingEntry.PanX;
            _panY = existingEntry.PanY;
            _zoom = Math.Max(0.1, existingEntry.Zoom);
        }

        // Load image immediately if bytes were supplied; otherwise start an async load
        // so the preview window opens right away while the full-resolution image downloads.
        if (imageBytes != null && imageBytes.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(imageBytes);
                _bitmap = Bitmap.DecodeToWidth(ms, 1200);
                _naturalSize = _bitmap.PixelSize;
                PreviewImage.Source = _bitmap;
            }
            catch { /* non-fatal */ }
        }
        else if (!string.IsNullOrWhiteSpace(imagePath))
        {
            _loadCts = new CancellationTokenSource();
            LoadingOverlay.IsVisible = true;
            _ = LoadImageAsync(imagePath, existingEntry?.IllustId, _loadCts.Token);
        }

        UpdateImageHost();
        UpdatePreview();
        UpdateLabels();
    }

    private async Task LoadImageAsync(string imagePath, string? illustId, CancellationToken ct)
    {
        try
        {
            var overlay = AppServices.Get<BackgroundOverlayService>();
            var bytes = await overlay.FetchImageBytesAsync(imagePath, illustId, ct).ConfigureAwait(false);

            if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested)
                return;

            var bmp = await Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(ms, 1200);
                }
                catch { return null; }
            }, ct).ConfigureAwait(false);

            if (bmp is null || ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _bitmap = bmp;
                _naturalSize = bmp.PixelSize;
                PreviewImage.Source = _bitmap;
                LoadingOverlay.IsVisible = false;
                UpdateImageHost();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadingOverlay.IsVisible = false;
                HintText.Text = Pikura.Avalonia.Localization.LocalizationService.Instance["Pikura_CouldNotLoadImagePreview"];
                HintText.Opacity = 1;
            }, DispatcherPriority.Background);
        }
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Slider.Value)) return;
        UpdatePreview();
        UpdateLabels();
    }

    private void UpdatePreview()
    {
        PreviewImageHost.Opacity = OpacitySlider.Value;
        DarknessRect.Opacity = DarknessSlider.Value;
        BrightnessRect.Opacity = BrightnessSlider.Value;
    }

    private void UpdateLabels()
    {
        OpacityLabel.Text = $"{OpacitySlider.Value:F2}";
        BrightnessLabel.Text = $"{BrightnessSlider.Value:F2}";
        DarknessLabel.Text = $"{DarknessSlider.Value:F2}";
    }

    private void OnPreviewCanvasLayoutUpdated(object? sender, EventArgs e)
        => UpdateImageHost();

    /// <summary>
    /// Computes the image host's displayed size and position so the image is fitted
    /// inside the viewport by default, then translated/zoomed by the user's pan/zoom.
    /// </summary>
    private void UpdateImageHost()
    {
        if (PreviewImage?.Source is not Bitmap bmp) return;

        var viewport = PreviewCanvas.Bounds;
        if (_naturalSize.Width <= 0 || _naturalSize.Height <= 0 ||
            viewport.Width <= 0 || viewport.Height <= 0)
            return;

        // Fit the image inside the viewport, then apply user zoom on top.
        var baseScale = Math.Min(viewport.Width / _naturalSize.Width, viewport.Height / _naturalSize.Height);
        var effectiveScale = baseScale * _zoom;

        var displayWidth = _naturalSize.Width * effectiveScale;
        var displayHeight = _naturalSize.Height * effectiveScale;

        PreviewImageHost.Width = displayWidth;
        PreviewImageHost.Height = displayHeight;

        // Center in viewport, then apply normalized pan (-1..1 = half the displayed image).
        var x = (viewport.Width - displayWidth) / 2.0 + _panX * displayWidth;
        var y = (viewport.Height - displayHeight) / 2.0 + _panY * displayHeight;

        Canvas.SetLeft(PreviewImageHost, x);
        Canvas.SetTop(PreviewImageHost, y);

        // Keep the hint text anchored at the bottom of the viewport.
        if (HintText != null)
        {
            HintText.Width = viewport.Width;
            Canvas.SetLeft(HintText, 0);
            Canvas.SetTop(HintText, Math.Max(0, viewport.Height - 24));
        }
    }

    private void OnPreviewWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        _zoom = Math.Clamp(_zoom * factor, 0.1, 5.0);
        UpdateImageHost();
        e.Handled = true;
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        _dragStart = e.GetPosition(PreviewCanvas);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(PreviewCanvas);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;

        var viewport = PreviewCanvas.Bounds;
        var baseScale = Math.Min(viewport.Width / _naturalSize.Width, viewport.Height / _naturalSize.Height);
        var displayWidth = _naturalSize.Width * baseScale * _zoom;
        var displayHeight = _naturalSize.Height * baseScale * _zoom;

        if (displayWidth > 0) _panX += dx / displayWidth;
        if (displayHeight > 0) _panY += dy / displayHeight;

        _panX = Math.Clamp(_panX, -1.0, 1.0);
        _panY = Math.Clamp(_panY, -1.0, 1.0);

        UpdateImageHost();
        _dragStart = pos;
        e.Handled = true;
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnResetPosition(object? sender, RoutedEventArgs e)
    {
        _panX = 0;
        _panY = 0;
        _zoom = 1.0;
        UpdateImageHost();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        Result = new OverlayImageEntry
        {
            Path = ImagePath,
            Opacity = OpacitySlider.Value,
            Brightness = BrightnessSlider.Value,
            Darkness = DarknessSlider.Value,
            PanX = _panX,
            PanY = _panY,
            Zoom = _zoom,
        };
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }
        catch { /* non-fatal */ }
        base.OnClosed(e);
    }
}
