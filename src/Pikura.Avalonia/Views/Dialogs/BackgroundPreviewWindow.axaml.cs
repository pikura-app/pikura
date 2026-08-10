using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>
/// Live preview window for adjusting a background overlay image before applying.
/// Supports dragging to reposition, and sliders for opacity, brightness, and darkness.
/// </summary>
public partial class BackgroundPreviewWindow : Window
{
    private Bitmap? _bitmap;
    private bool _isDragging;
    private Point _dragStart;
    private double _panX;
    private double _panY;
    private double _zoom = 1.0;
    private double _baseScale = 1.0;
    private PixelSize _naturalSize;
    private readonly TranslateTransform _translateTransform = new();
    private readonly ScaleTransform _scaleTransform = new(1.0, 1.0);
    private readonly TransformGroup _transformGroup;

    /// <summary>The resulting per-image entry after the user clicks Apply. Null if cancelled.</summary>
    public OverlayImageEntry? Result { get; private set; }

    /// <summary>The image path/URL this preview is for.</summary>
    public string ImagePath { get; }

    public BackgroundPreviewWindow() : this("", null, null) { }

    public BackgroundPreviewWindow(string imagePath, byte[]? imageBytes, OverlayImageEntry? existingEntry)
    {
        ImagePath = imagePath;
        InitializeComponent();

        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_translateTransform);
        OpacityPanel.RenderTransform = _transformGroup;
        OpacityPanel.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        // Wire slider changes to live preview
        OpacitySlider.PropertyChanged += OnSliderChanged;
        BrightnessSlider.PropertyChanged += OnSliderChanged;
        DarknessSlider.PropertyChanged += OnSliderChanged;

        // Wire drag and zoom events on the preview panel
        PreviewPanel.PointerPressed += OnImagePointerPressed;
        PreviewPanel.PointerMoved += OnImagePointerMoved;
        PreviewPanel.PointerReleased += OnImagePointerReleased;
        PreviewPanel.PointerWheelChanged += OnPreviewWheelChanged;
        OpacityPanel.LayoutUpdated += OnPreviewLayoutUpdated;

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

        // Load image
        if (imageBytes != null && imageBytes.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(imageBytes);
                _bitmap = new Bitmap(ms);
                _naturalSize = _bitmap.PixelSize;
                PreviewImage.Source = _bitmap;
            }
            catch { /* non-fatal */ }
        }

        UpdateBaseScale();
        UpdateTransform();
        UpdateLabels();
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Slider.Value)) return;
        UpdatePreview();
        UpdateLabels();
    }

    private void UpdatePreview()
    {
        OpacityPanel.Opacity = OpacitySlider.Value;
        DarknessRect.Opacity = DarknessSlider.Value;
        BrightnessRect.Opacity = BrightnessSlider.Value;
    }

    private void UpdateLabels()
    {
        OpacityLabel.Text = $"{OpacitySlider.Value:F2}";
        BrightnessLabel.Text = $"{BrightnessSlider.Value:F2}";
        DarknessLabel.Text = $"{DarknessSlider.Value:F2}";
        UpdatePreview();
    }

    private void UpdateBaseScale()
    {
        var bounds = OpacityPanel.Bounds;
        if (_naturalSize.Width <= 0 || _naturalSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        _baseScale = Math.Min(bounds.Width / _naturalSize.Width, bounds.Height / _naturalSize.Height);
    }

    private void UpdateTransform()
    {
        _scaleTransform.ScaleX = _zoom;
        _scaleTransform.ScaleY = _zoom;
        _translateTransform.X = _panX * _naturalSize.Width * _baseScale * _zoom;
        _translateTransform.Y = _panY * _naturalSize.Height * _baseScale * _zoom;
    }

    private void OnPreviewLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateBaseScale();
        UpdateTransform();
    }

    private void OnPreviewWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        _zoom = Math.Clamp(_zoom * factor, 0.1, 5.0);
        UpdateTransform();
        e.Handled = true;
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewPanel).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        _dragStart = e.GetPosition(PreviewPanel);
        e.Pointer.Capture(PreviewPanel);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(PreviewPanel);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        var scaledWidth = _naturalSize.Width * _baseScale * _zoom;
        var scaledHeight = _naturalSize.Height * _baseScale * _zoom;
        if (scaledWidth > 0) _panX += dx / scaledWidth;
        if (scaledHeight > 0) _panY += dy / scaledHeight;

        UpdateTransform();
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
        UpdateTransform();
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
        base.OnClosed(e);
    }
}
