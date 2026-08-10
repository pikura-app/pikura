using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using SkiaSharp;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class ImageEditorWindow : Window
{
    private readonly ImageResizeService? _imageResizeService;
    private SKBitmap _originalBitmap;
    private ImageEditPreset _currentPreset;
    private CancellationTokenSource? _previewDebounceCts;
    private bool _isUpdating = false;
    private bool _maintainAspect = true;
    private float _originalAspectRatio;

    // Multi-artwork support: list of artworks, each with multiple pages
    private System.Collections.Generic.List<EditableArtwork> _artworks = new();
    private int _currentArtworkIndex = 0;

    // Multi-page support within current artwork
    private System.Collections.Generic.List<SKBitmap> _pages =>
        _currentArtworkIndex < _artworks.Count ? _artworks[_currentArtworkIndex].Pages : new();
    private int _currentPageIndex = 0;

    // Ugoira (animated) support
    private bool _isUgoira; // Set based on artwork type, can be cleared for edit modes
    private bool _forceShowUgoiraDialog; // When true, always show mode selection dialog
    private bool _ugoiraDialogShowing; // Guard to prevent double-showing dialog

    // Default and "active drag" cursors for the editor preview. Updated each
    // time the active preset changes so the user gets a hint when crop pan is
    // available.
    private static readonly global::Avalonia.Input.Cursor _moveCursor =
        new(global::Avalonia.Input.StandardCursorType.SizeAll);
    private static readonly global::Avalonia.Input.Cursor _grabbingCursor =
        new(global::Avalonia.Input.StandardCursorType.Hand);

    public ImageEditorWindow()
    {
        InitializeComponent();
        _currentPreset = new ImageEditPreset { DevicePreset = DevicePreset.Custom };
        _originalBitmap = new SKBitmap(1, 1);

        // Keyboard navigation: ←/→ or PageUp/PageDown to flip between pages.
        KeyDown += OnEditorKeyDown;

        // Hover hint: show a 4-way move cursor whenever the user is over the
        // image preview and crop-pan is available for the active preset.
        EditorImage.PointerEntered += (_, _) => UpdatePreviewCursor();
        EditorImage.PointerExited += (_, _) => EditorImage.Cursor = global::Avalonia.Input.Cursor.Default;
    }

    private void UpdatePreviewCursor()
    {
        EditorImage.Cursor = CanPanCrop
            ? (_isPanningCrop ? _grabbingCursor : _moveCursor)
            : global::Avalonia.Input.Cursor.Default;
    }

    /// <summary>
    /// Single artwork constructor (legacy compatibility).
    /// </summary>
    public ImageEditorWindow(
        ImageResizeService imageResizeService,
        SKBitmap originalBitmap,
        ImageEditPreset? initialPreset = null) : this()
    {
        _imageResizeService = imageResizeService;
        _originalBitmap = originalBitmap;

        // Wrap in EditableArtwork structure
        _artworks = new System.Collections.Generic.List<EditableArtwork>
        {
            new EditableArtwork { Pages = new System.Collections.Generic.List<SKBitmap> { originalBitmap } }
        };
        _currentArtworkIndex = 0;
        _currentPageIndex = 0;
        _isUgoira = false;

        _currentPreset = initialPreset?.Clone() ?? new ImageEditPreset { DevicePreset = DevicePreset.Custom };

        // Calculate aspect ratio
        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;

        // Initialize resize dimensions
        _currentPreset.ResizeSettings.TargetWidth = _originalBitmap.Width;
        _currentPreset.ResizeSettings.TargetHeight = _originalBitmap.Height;

        // Load initial UI values
        LoadPresetIntoUI();
        UpdateNavigationUI();

        // Generate initial preview
        _ = UpdatePreviewAsync();
    }

    /// <summary>
    /// Multi-page constructor (single artwork with multiple pages).
    /// </summary>
    public ImageEditorWindow(
        ImageResizeService imageResizeService,
        System.Collections.Generic.IList<SKBitmap> pages,
        int initialPageIndex = 0,
        ImageEditPreset? initialPreset = null) : this()
    {
        if (pages == null || pages.Count == 0)
            throw new System.ArgumentException("At least one page bitmap is required", nameof(pages));

        _imageResizeService = imageResizeService;

        // Wrap pages in EditableArtwork structure
        _artworks = new System.Collections.Generic.List<EditableArtwork>
        {
            new EditableArtwork { Pages = new System.Collections.Generic.List<SKBitmap>(pages) }
        };
        _currentArtworkIndex = 0;
        _currentPageIndex = System.Math.Clamp(initialPageIndex, 0, pages.Count - 1);
        _isUgoira = false;
        _originalBitmap = pages[_currentPageIndex];

        _currentPreset = initialPreset?.Clone() ?? new ImageEditPreset { DevicePreset = DevicePreset.Custom };

        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;
        _currentPreset.ResizeSettings.TargetWidth = _originalBitmap.Width;
        _currentPreset.ResizeSettings.TargetHeight = _originalBitmap.Height;

        LoadPresetIntoUI();
        UpdateNavigationUI();

        _ = UpdatePreviewAsync();
    }

    /// <summary>
    /// Multi-artwork constructor. Each artwork can have multiple pages.
    /// The same preset is shared across all artworks/pages.
    /// </summary>
    public ImageEditorWindow(
        ImageResizeService imageResizeService,
        System.Collections.Generic.IList<EditableArtwork> artworks,
        int initialArtworkIndex = 0,
        int initialPageIndex = 0,
        ImageEditPreset? initialPreset = null,
        bool forceShowUgoiraDialog = false) : this()
    {
        if (artworks == null || artworks.Count == 0)
            throw new System.ArgumentException("At least one artwork is required", nameof(artworks));

        _imageResizeService = imageResizeService;
        _artworks = new System.Collections.Generic.List<EditableArtwork>(artworks);
        _currentArtworkIndex = System.Math.Clamp(initialArtworkIndex, 0, _artworks.Count - 1);
        _isUgoira = _artworks[_currentArtworkIndex].IllustType == 2;
        _forceShowUgoiraDialog = forceShowUgoiraDialog;

        var currentArtwork = _artworks[_currentArtworkIndex];
        _currentPageIndex = System.Math.Clamp(initialPageIndex, 0, System.Math.Max(0, currentArtwork.PageCount - 1));
        _originalBitmap = currentArtwork.Pages.Count > 0 ? currentArtwork.Pages[_currentPageIndex] : new SKBitmap(1, 1);

        _currentPreset = initialPreset?.Clone() ?? new ImageEditPreset { DevicePreset = DevicePreset.Custom };

        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;
        _currentPreset.ResizeSettings.TargetWidth = _originalBitmap.Width;
        _currentPreset.ResizeSettings.TargetHeight = _originalBitmap.Height;

        LoadPresetIntoUI();
        UpdateNavigationUI();

        _ = UpdatePreviewAsync();
    }

    /// <summary>
    /// Updates both artwork and page navigation UI elements.
    /// Shows separate submission indicator (for multi-artwork) and page indicator (for multi-page).
    /// </summary>
    private void UpdateNavigationUI()
    {
        var hasMultipleArtworks = _artworks.Count > 1;
        var currentArtwork = _currentArtworkIndex < _artworks.Count ? _artworks[_currentArtworkIndex] : null;
        var hasMultiplePages = (currentArtwork?.PageCount ?? 1) > 1;

        // Navigation panel visible if multiple artworks or multiple pages
        if (PageNavPanel != null)
            PageNavPanel.IsVisible = hasMultipleArtworks || hasMultiplePages;

        // Submission indicator (blue badge) - only when multiple artworks
        if (SubmissionIndicatorBorder != null && SubmissionIndicator != null)
        {
            if (hasMultipleArtworks)
            {
                SubmissionIndicatorBorder.IsVisible = true;
                SubmissionIndicator.Text = $"{_currentArtworkIndex + 1}/{_artworks.Count}";
            }
            else
            {
                SubmissionIndicatorBorder.IsVisible = false;
            }
        }

        // Page indicator - shows page count within current submission
        if (currentArtwork is { } art && PageIndicator is { } pageIndicator)
        {
            if (hasMultiplePages)
                pageIndicator.Text = $"Pg {_currentPageIndex + 1}/{art.PageCount}";
            else
                pageIndicator.Text = ""; // Hide page text if single page
        }

        // Enable/disable nav buttons
        if (PrevPageButton != null)
            PrevPageButton.IsEnabled = CanGoToPrevious();
        if (NextPageButton != null)
            NextPageButton.IsEnabled = CanGoToNext();
    }

    private bool CanGoToPrevious() => _currentPageIndex > 0 || _currentArtworkIndex > 0;
    private bool CanGoToNext()
    {
        var currentArtwork = _currentArtworkIndex < _artworks.Count ? _artworks[_currentArtworkIndex] : null;
        return _currentPageIndex < (currentArtwork?.PageCount ?? 1) - 1 || _currentArtworkIndex < _artworks.Count - 1;
    }

    private void UpdatePageNav() => UpdateNavigationUI(); // Legacy alias

    private void OnPrevPageClick(object? sender, RoutedEventArgs e) => GoToPrevious();
    private void OnNextPageClick(object? sender, RoutedEventArgs e) => GoToNext();

    private void GoToPrevious()
    {
        // First try to go to previous page within current artwork
        if (_currentPageIndex > 0)
        {
            GoToPage(_currentPageIndex - 1);
        }
        // If at first page, go to previous artwork (last page)
        else if (_currentArtworkIndex > 0)
        {
            _currentArtworkIndex--;
            var prevArtwork = _artworks[_currentArtworkIndex];
            _currentPageIndex = System.Math.Max(0, prevArtwork.PageCount - 1);
            
            // Check if this is an unprocessed ugoira
            if (prevArtwork.IllustType == 2 && prevArtwork.Pages.Count == 0)
            {
                _ = LoadUgoiraForArtworkAsync(prevArtwork);
            }
            else
            {
                GoToPage(_currentPageIndex);
            }
        }
    }

    private void GoToNext()
    {
        if (_currentArtworkIndex >= _artworks.Count) return;
        var currentArtwork = _artworks[_currentArtworkIndex];

        // First try to go to next page within current artwork
        if (_currentPageIndex < currentArtwork.PageCount - 1)
        {
            GoToPage(_currentPageIndex + 1);
        }
        // If at last page, go to next artwork (first page)
        else if (_currentArtworkIndex < _artworks.Count - 1)
        {
            _currentArtworkIndex++;
            var nextArtwork = _artworks[_currentArtworkIndex];
            _currentPageIndex = 0;
            
            // Check if this is an unprocessed ugoira
            if (nextArtwork.IllustType == 2 && nextArtwork.Pages.Count == 0)
            {
                _ = LoadUgoiraForArtworkAsync(nextArtwork);
            }
            else
            {
                GoToPage(_currentPageIndex);
            }
        }
    }

    private void GoToPage(int newIndex)
    {
        if (_currentArtworkIndex >= _artworks.Count) return;
        var currentArtwork = _artworks[_currentArtworkIndex];
        
        // Validate index bounds
        if (newIndex < 0 || newIndex >= currentArtwork.PageCount) return;
        
        // Validate that we have pages loaded
        if (currentArtwork.Pages == null || currentArtwork.Pages.Count == 0) return;
        if (newIndex >= currentArtwork.Pages.Count) return;

        _currentPageIndex = newIndex;
        
        // Switch the active source bitmap. The preset stays the same so adjustments
        // are previewed identically across pages.
        _originalBitmap = currentArtwork.Pages[_currentPageIndex];
        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;
        
        // Invalidate the cached downscaled bitmap so the new page gets resized.
        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
        _cachedPreviewSourceWidth = -1;
        
        UpdateNavigationUI();
        _ = UpdatePreviewAsync(immediate: true);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Skip when typing in input controls
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or Slider)
            return;

        // Check if navigation is available (multiple artworks OR multiple pages in current)
        var currentArtwork = _currentArtworkIndex < _artworks.Count ? _artworks[_currentArtworkIndex] : null;
        var hasNav = _artworks.Count > 1 || (currentArtwork?.PageCount ?? 1) > 1;
        if (!hasNav) return;

        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                e.Handled = true;
                GoToPrevious();
                break;
            case Key.Right:
            case Key.PageDown:
                e.Handled = true;
                GoToNext();
                break;
        }
    }

    private global::Avalonia.Media.SolidColorBrush? OverlaySwatchBrush
        => OverlayColorButton?.Background as global::Avalonia.Media.SolidColorBrush;

    private void OnOverlayColorPickerChanged(object? sender, global::Avalonia.Controls.ColorChangedEventArgs e)
    {
        var c = e.NewColor;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // Always reflect the chosen colour in the swatch + label so the UI
        // stays in sync even when this fires during LoadPresetIntoUI().
        if (OverlaySwatchBrush is { } brush) brush.Color = c;
        if (OverlayColorHexLabel != null) OverlayColorHexLabel.Text = hex;

        if (_isUpdating) return;
        _currentPreset.Adjustments.ColorOverlayHex = hex;
        _ = UpdatePreviewAsync();
    }

    private void LoadPresetIntoUI()
    {
        _isUpdating = true;

        var adj = _currentPreset.Adjustments;
        var resize = _currentPreset.ResizeSettings;

        // Resize
        WidthNumeric.Value = resize.TargetWidth;
        HeightNumeric.Value = resize.TargetHeight;
        MaintainAspectCheck.IsChecked = resize.LockAspectRatio;

        // Basic adjustments
        BrightnessSlider.Value = adj.Brightness;
        BrightnessValue.Text = adj.Brightness.ToString();

        ContrastSlider.Value = adj.Contrast;
        ContrastValue.Text = adj.Contrast.ToString();

        SaturationSlider.Value = adj.Saturation;
        SaturationValue.Text = adj.Saturation.ToString();

        HueSlider.Value = adj.HueRotation;
        HueValue.Text = $"{adj.HueRotation}°";

        OpacitySlider.Value = adj.Opacity;
        OpacityValue.Text = $"{adj.Opacity}%";

        // Color adjustments
        TemperatureSlider.Value = adj.Temperature;
        TintSlider.Value = adj.Tint;
        HighlightsSlider.Value = adj.Highlights;
        HighlightsValue.Text = adj.Highlights.ToString();
        ShadowsSlider.Value = adj.Shadows;
        ShadowsValue.Text = adj.Shadows.ToString();

        // Effects
        SharpnessSlider.Value = adj.Sharpness;
        SharpnessValue.Text = adj.Sharpness.ToString();

        BlurSlider.Value = adj.BlurRadius;
        BlurValue.Text = adj.BlurRadius.ToString();

        VignetteIntensitySlider.Value = adj.VignetteIntensity;
        VignetteIntensityValue.Text = adj.VignetteIntensity.ToString();

        VignetteRadiusSlider.Value = adj.VignetteRadius;
        VignetteRadiusValue.Text = adj.VignetteRadius.ToString();

        OverlayOpacitySlider.Value = adj.ColorOverlayOpacity;
        OverlayOpacityValue.Text = adj.ColorOverlayOpacity.ToString();

        var overlayHex = string.IsNullOrEmpty(adj.ColorOverlayHex) ? "#000000" : adj.ColorOverlayHex;
        try
        {
            var parsed = global::Avalonia.Media.Color.Parse(overlayHex);
            if (OverlayColorView != null) OverlayColorView.Color = parsed;
            if (OverlaySwatchBrush is { } brush) brush.Color = parsed;
            if (OverlayColorHexLabel != null) OverlayColorHexLabel.Text = overlayHex.ToUpperInvariant();
        }
        catch { /* invalid hex - leave default */ }

        // Image info
        var fileSize = EstimateFileSize(_originalBitmap);
        ImageInfoText.Text = $"{_originalBitmap.Width} x {_originalBitmap.Height} • {fileSize}";

        _isUpdating = false;
    }

    private string EstimateFileSize(SKBitmap bitmap)
    {
        // Rough estimate: width * height * 4 bytes per pixel / 1024 / 1024
        var bytes = bitmap.Width * bitmap.Height * 4;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private bool _isFirstPreview = true;
    // Cached downscaled bitmap so we don't re-resize the original on every slider tick.
    // Created lazily in UpdatePreviewAsync, disposed when window closes.
    private SKBitmap? _cachedPreviewSource;
    private int _cachedPreviewSourceWidth = -1;

    /// <summary>
    /// Convert SKBitmap to Avalonia Bitmap via direct pixel copy (faster than encode/decode).
    /// </summary>
    public static Bitmap SkiaToAvalonia(SKBitmap skBitmap)
        => SkiaToWriteable(skBitmap, null);

    /// <summary>
    /// Convert an SKBitmap into an Avalonia WriteableBitmap, reusing the supplied
    /// existing instance when its pixel size matches. Reusing avoids a ~6 MB
    /// allocation+free per frame during slider drags.
    /// </summary>
    private static global::Avalonia.Media.Imaging.WriteableBitmap SkiaToWriteable(
        SKBitmap skBitmap,
        global::Avalonia.Media.Imaging.WriteableBitmap? reuse)
    {
        // Ensure pixels are in BGRA8888 format that Avalonia uses
        SKBitmap source = skBitmap;
        bool ownsSource = false;
        if (source.ColorType != SKColorType.Bgra8888 || source.AlphaType != SKAlphaType.Premul)
        {
            var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var converted = new SKBitmap(info);
            using (var canvas = new SKCanvas(converted))
            {
                canvas.DrawBitmap(source, 0, 0);
            }
            source = converted;
            ownsSource = true;
        }

        var size = new global::Avalonia.PixelSize(source.Width, source.Height);
        var dpi = new global::Avalonia.Vector(96, 96);
        global::Avalonia.Media.Imaging.WriteableBitmap writeable;
        if (reuse != null && reuse.PixelSize == size)
        {
            writeable = reuse;
        }
        else
        {
            writeable = new global::Avalonia.Media.Imaging.WriteableBitmap(
                size, dpi,
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                global::Avalonia.Platform.AlphaFormat.Premul);
        }

        using (var fb = writeable.Lock())
        {
            int rowBytes = source.Width * 4;
            unsafe
            {
                byte* src = (byte*)source.GetPixels();
                byte* dst = (byte*)fb.Address;
                for (int y = 0; y < source.Height; y++)
                {
                    System.Buffer.MemoryCopy(src + y * source.RowBytes, dst + y * fb.RowBytes, rowBytes, rowBytes);
                }
            }
        }

        if (ownsSource) source.Dispose();
        return writeable;
    }

    // Reused WriteableBitmap for live preview to avoid per-frame allocation.
    private global::Avalonia.Media.Imaging.WriteableBitmap? _reusableWriteable;

    private async Task UpdatePreviewAsync(bool immediate = false)
    {
        if (_imageResizeService == null || _isUpdating) return;

        // Handle ugoira (animated) - load animated preview instead of processing
        if (_isUgoira)
        {
            await LoadUgoiraPreviewAsync();
            return;
        }

        // Not ugoira - ensure static image is visible
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
        });

        // Cancel previous preview generation
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var ct = _previewDebounceCts.Token;

        // Only show spinner on first preview load (not for live adjustments)
        var showSpinner = _isFirstPreview;

        _previewRenderInFlight = true;
        try
        {
            if (showSpinner)
            {
                await Dispatcher.UIThread.InvokeAsync(() => LoadingSpinner.IsVisible = true);
            }

            // Coalesce rapid slider ticks into ~60 fps (16 ms) so we don't fire a
            // full render for every micro-step of the slider thumb. Skip when
            // immediate is requested (e.g. on page change / drag end).
            if (!immediate)
            {
                await Task.Delay(16, ct);
                if (ct.IsCancellationRequested) return;
            }

            // Cache the downscaled source ONCE at display resolution. Resizing a 4K image
            // is expensive (50-100ms in Skia); we don't want to redo it just because the
            // user started panning. The cache is invalidated only when the page/artwork
            // changes (see _cachedPreviewSource handling on navigation).
            const int previewWidth = 600;
            if (_cachedPreviewSource == null || _cachedPreviewSourceWidth != previewWidth)
            {
                _cachedPreviewSource?.Dispose();
                _cachedPreviewSource = null;
                var srcBmp = _originalBitmap;
                if (srcBmp.Width <= 0 || srcBmp.Height <= 0) return;
                _cachedPreviewSource = await Task.Run(() =>
                {
                    try
                    {
                        if (srcBmp.Width <= previewWidth) return srcBmp.Copy();
                        var scale = (float)previewWidth / srcBmp.Width;
                        var newHeight = (int)(srcBmp.Height * scale);
                        return srcBmp.Resize(new SKImageInfo(previewWidth, newHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    }
                    catch
                    {
                        return null;
                    }
                }, ct);
                _cachedPreviewSourceWidth = previewWidth;
                if (ct.IsCancellationRequested || _cachedPreviewSource == null) return;
            }

            // Generate preview using cached downscaled bitmap (adjustments + crop only)
            var previewBitmap = await _imageResizeService.ProcessForPreviewAsync(_cachedPreviewSource!, _currentPreset, ct, previewWidth);
            if (previewBitmap == null || ct.IsCancellationRequested) return;

            // Direct pixel copy into a reused WriteableBitmap (no JPEG/PNG
            // encoding overhead and no per-frame allocation when size matches).
            _reusableWriteable = SkiaToWriteable(previewBitmap, _reusableWriteable);
            var avaloniaBitmap = _reusableWriteable;
            previewBitmap.Dispose();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                EditorImage.Source = avaloniaBitmap;
                EditorImage.InvalidateVisual();
                _isFirstPreview = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Expected, ignore
        }
        catch (Exception ex)
        {
            // Log error but don't crash
            Console.WriteLine($"Preview error: {ex.Message}");
        }
        finally
        {
            _previewRenderInFlight = false;
            if (showSpinner)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LoadingSpinner.IsVisible = false;
                });
            }

            // If the user is still actively panning, queue one catch-up render
            // so the latest offset reaches the screen.
            if (_isPanningCrop)
            {
                _ = UpdatePreviewAsync();
            }
        }
    }

    private void SyncPresetFromUI()
    {
        var adj = _currentPreset.Adjustments;
        var resize = _currentPreset.ResizeSettings;

        // Resize
        resize.TargetWidth = (int)(WidthNumeric.Value ?? 1920);
        resize.TargetHeight = (int)(HeightNumeric.Value ?? 1080);
        resize.LockAspectRatio = MaintainAspectCheck.IsChecked == true;

        // Basic adjustments
        adj.Brightness = (int)BrightnessSlider.Value;
        adj.Contrast = (int)ContrastSlider.Value;
        adj.Saturation = (int)SaturationSlider.Value;
        adj.HueRotation = (int)HueSlider.Value;
        adj.Opacity = (int)OpacitySlider.Value;

        // Color adjustments
        adj.Temperature = (int)TemperatureSlider.Value;
        adj.Tint = (int)TintSlider.Value;
        adj.Highlights = (int)HighlightsSlider.Value;
        adj.Shadows = (int)ShadowsSlider.Value;

        // Effects
        adj.Sharpness = (int)SharpnessSlider.Value;
        adj.BlurRadius = (int)BlurSlider.Value;
        adj.VignetteIntensity = (int)VignetteIntensitySlider.Value;
        adj.VignetteRadius = (int)VignetteRadiusSlider.Value;
        adj.ColorOverlayOpacity = (int)OverlayOpacitySlider.Value;
        // ColorPicker handles its own value via OnOverlayColorPickerChanged;
        // here we just preserve whatever has already been set on the preset.
    }

    private static bool IsValidHexColor(string color)
    {
        if (string.IsNullOrEmpty(color)) return false;
        if (color.Length != 7 && color.Length != 4) return false;
        if (!color.StartsWith('#')) return false;
        // Check remaining characters are valid hex
        for (int i = 1; i < color.Length; i++)
        {
            if (!char.IsAsciiHexDigit(color[i])) return false;
        }
        return true;
    }

    // Event Handlers

    private void OnAdjustmentChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;

        // Update value displays
        BrightnessValue.Text = ((int)BrightnessSlider.Value).ToString();
        ContrastValue.Text = ((int)ContrastSlider.Value).ToString();
        SaturationValue.Text = ((int)SaturationSlider.Value).ToString();
        HueValue.Text = $"{(int)HueSlider.Value}°";
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        HighlightsValue.Text = ((int)HighlightsSlider.Value).ToString();
        ShadowsValue.Text = ((int)ShadowsSlider.Value).ToString();
        SharpnessValue.Text = ((int)SharpnessSlider.Value).ToString();
        BlurValue.Text = ((int)BlurSlider.Value).ToString();
        VignetteIntensityValue.Text = ((int)VignetteIntensitySlider.Value).ToString();
        VignetteRadiusValue.Text = ((int)VignetteRadiusSlider.Value).ToString();
        OverlayOpacityValue.Text = ((int)OverlayOpacitySlider.Value).ToString();

        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    private void OnResizeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdating || !_maintainAspect) return;

        var width = (int)(WidthNumeric.Value ?? 1920);
        var height = (int)(HeightNumeric.Value ?? 1080);

        // If maintaining aspect ratio, update the other dimension
        if (sender == WidthNumeric)
        {
            HeightNumeric.Value = (int)(width / _originalAspectRatio);
        }
        else if (sender == HeightNumeric)
        {
            WidthNumeric.Value = (int)(height * _originalAspectRatio);
        }

        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    private void OnMaintainAspectChanged(object? sender, RoutedEventArgs e)
    {
        _maintainAspect = MaintainAspectCheck.IsChecked == true;
    }

    private void OnCropRatioChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Skip if not initialized yet
        if (_originalBitmap == null) return;

        var index = CropRatioCombo.SelectedIndex;
        var aspect = index switch
        {
            0 => (float?)null, // Free
            1 => _originalAspectRatio, // Original
            2 => 1.0f, // 1:1
            3 => 16.0f / 9.0f, // 16:9
            4 => 9.0f / 16.0f, // 9:16
            5 => 4.0f / 3.0f, // 4:3
            6 => 3.0f / 4.0f, // 3:4
            7 => 21.0f / 9.0f, // 21:9
            _ => (float?)null
        };

        if (aspect.HasValue)
        {
            var currentWidth = (int)(WidthNumeric.Value ?? _originalBitmap.Width);
            var newHeight = (int)(currentWidth / aspect.Value);
            HeightNumeric.Value = newHeight;

            // Update crop frame size and show overlay
            CropFrame.Width = 300;
            CropFrame.Height = 300 / aspect.Value;
            CropFrame.Margin = new global::Avalonia.Thickness(0);
            CropOverlay.IsVisible = true;
        }
        else
        {
            CropOverlay.IsVisible = false;
        }

        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    private void OnResetCropClick(object? sender, RoutedEventArgs e)
    {
        WidthNumeric.Value = _originalBitmap.Width;
        HeightNumeric.Value = _originalBitmap.Height;
        CropRatioCombo.SelectedIndex = 1; // Original
        CropOverlay.IsVisible = false;

        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    private void OnResetAllClick(object? sender, RoutedEventArgs e)
    {
        _currentPreset.Adjustments.Reset();
        _currentPreset.ResizeSettings.TargetWidth = _originalBitmap.Width;
        _currentPreset.ResizeSettings.TargetHeight = _originalBitmap.Height;

        LoadPresetIntoUI();
        _ = UpdatePreviewAsync();
    }

    /// <summary>
    /// Click handler for the Ugoira Mode button - allows switching modes without closing editor.
    /// </summary>
    private async void OnUgoiraModeClick(object? sender, RoutedEventArgs e)
    {
        // Allow switching even if _isUgoira is false (we might be in frame-edit mode)
        // Just check if we have an artwork with valid ID
        var artwork = _artworks[_currentArtworkIndex];
        if (string.IsNullOrEmpty(artwork.ArtworkId)) return;

        // Show loading while preparing dialog
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (LoadingSpinner != null) LoadingSpinner.IsVisible = true;
            if (UgoiraModeButton != null) UgoiraModeButton.IsEnabled = false;
        });

        try
        {
            // Prevent double-showing dialog
            if (_ugoiraDialogShowing) return;
            _ugoiraDialogShowing = true;

            // Show the mode selection dialog again
            var dialogMode = await ShowUgoiraModeDialogAsync();
            _ugoiraDialogShowing = false;
            _forceShowUgoiraDialog = false; // Reset so we don't show again
            if (dialogMode == null) return; // User cancelled

            var newMode = dialogMode.Value;
            if (_currentPreset == null || newMode == _currentPreset.UgoiraMode) return; // No change

            // Save the new mode
            _currentPreset.UgoiraMode = newMode;

            // Show loading for extraction
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (LoadingSpinner != null) LoadingSpinner.IsVisible = true;
                if (ImageInfoText != null) ImageInfoText.Text = "Loading ugoira...";
            });

            // Reload with the new mode
            var ugoiraService = AppServices.Get<UgoiraService>();
            
            // Reset visibility flags before loading
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EditorImage != null) EditorImage.IsVisible = false;
                if (UgoiraImage != null) UgoiraImage.IsVisible = false;
            });

            // Reset _isUgoira flag so we can reload fresh
            _isUgoira = true;

            switch (newMode)
            {
                case UgoiraPresetMode.WatchAnimation:
                    await LoadUgoiraAnimationAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditSingleFrame:
                    await LoadUgoiraSingleFrameAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditAllFrames:
                    await LoadUgoiraAllFramesAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditCover:
                    await LoadUgoiraCoverAsync(ugoiraService, artwork.ArtworkId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _ugoiraDialogShowing = false; // Reset flag on error
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ImageInfoText != null) ImageInfoText.Text = $"Error: {ex.Message}";
            });
        }
        finally
        {
            _ugoiraDialogShowing = false; // Always reset flag
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (LoadingSpinner != null) LoadingSpinner.IsVisible = false;
                if (UgoiraModeButton != null) UgoiraModeButton.IsEnabled = true;
            });
        }
    }

    private void OnSaveAsPresetClick(object? sender, RoutedEventArgs e)
    {
        var name = PresetNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            // Simple validation - preset name is required
            // Could show a dialog but for now just return
            return;
        }

        _currentPreset.Name = name;
        _currentPreset.Id = Guid.NewGuid();
        _currentPreset.IsBuiltIn = false;

        Close(_currentPreset);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // Sync final preset values and close with the preset
        SyncPresetFromUI();
        Close(_currentPreset);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Close without returning a preset (cancel)
        Close(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _previewDebounceCts?.Cancel();
        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
    }

    // Drag state for crop overlay
    private global::Avalonia.Point _dragStart;

    private void OnScrollViewerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Zoom with mouse wheel (Ctrl+Wheel) or pan (Wheel alone scrolls via ScrollViewer)
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            var delta = e.Delta.Y > 0 ? 0.1 : -0.1;
            var newScale = Math.Clamp(ZoomSlider.Value + delta, 0.1, 5.0);
            ZoomSlider.Value = newScale;
            ZoomValue.Text = $"{newScale:P0}";
            ApplyImageZoom(newScale);
            SyncPresetFromUI();
            _ = UpdatePreviewAsync();
        }
    }

    private void OnZoomChanged(object? sender, RoutedEventArgs e)
    {
        var scale = ZoomSlider.Value;
        ZoomValue.Text = $"{scale:P0}";
        // Apply zoom via render transform
        ApplyImageZoom(scale);
        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    private void ApplyImageZoom(double scale)
    {
        // Apply scale transform on the container Grid (zooms image + crop overlay together)
        if (ImageCanvas == null) return;
        var scaleTransform = new ScaleTransform(scale, scale);
        ImageCanvas.RenderTransform = scaleTransform;
        ImageCanvas.RenderTransformOrigin = new global::Avalonia.RelativePoint(
            new global::Avalonia.Point(0.5, 0.5), global::Avalonia.RelativeUnit.Relative);
    }

    private void OnResetPositionClick(object? sender, RoutedEventArgs e)
    {
        // Reset zoom and scroll position
        ZoomSlider.Value = 1.0;
        ZoomValue.Text = "100%";
        if (ImageCanvas != null)
        {
            ImageCanvas.RenderTransform = null;
        }
        if (ImageScrollViewer != null)
        {
            ImageScrollViewer.Offset = new global::Avalonia.Vector(0, 0);
        }
        // Reset crop frame position
        CropFrame.Margin = new global::Avalonia.Thickness(0);
        SyncPresetFromUI();
        _ = UpdatePreviewAsync();
    }

    // Drag state for image panning
    private bool _isPanning = false;
    private global::Avalonia.Vector _panStartOffset;
    private global::Avalonia.Point _panStartPoint;

    // Drag state for Fill-mode crop offset pan (drag the source image inside
    // the fixed crop window, in the spirit of a phone-wallpaper picker).
    private bool _isPanningCrop = false;
    private double _cropPanStartOffsetX;
    private double _cropPanStartOffsetY;
    private global::Avalonia.Point _cropPanStartPoint;
    // True while a preview tick is rendering — used to drop intermediate pan
    // updates so we never queue more work than the renderer can handle.
    private bool _previewRenderInFlight;

    /// <summary>
    /// True when the active preset has a fixed crop window the user can
    /// reposition by dragging (non-Original device preset in Fill mode).
    /// </summary>
    private bool CanPanCrop =>
        _currentPreset.DevicePreset != DevicePreset.Original
        && _currentPreset.DevicePreset != DevicePreset.None
        && _currentPreset.ResizeSettings.Mode == ResizeMode.Fill;

    // Drag state for crop frame
    private bool _isDraggingCropFrame = false;
    private global::Avalonia.Thickness _cropDragStartMargin;

    // Drag state for crop frame resize
    private string? _resizingHandle = null;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private global::Avalonia.Thickness _resizeStartMargin;

    private const double MinCropSize = 50;

    // Pointer Event Handlers for crop drag, resize, and image pan
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(ImageCanvas).Properties;
        if (!properties.IsLeftButtonPressed) return;

        // 1. Check if click is on a resize handle (Border with Tag) inside CropFrame
        if (CropOverlay.IsVisible && e.Source is Border tagged && tagged.Tag is string tag)
        {
            _resizingHandle = tag;
            _dragStart = e.GetPosition(ImageCanvas);
            _resizeStartWidth = double.IsNaN(CropFrame.Width) ? CropFrame.Bounds.Width : CropFrame.Width;
            _resizeStartHeight = double.IsNaN(CropFrame.Height) ? CropFrame.Bounds.Height : CropFrame.Height;
            _resizeStartMargin = CropFrame.Margin;
            e.Pointer.Capture(ImageCanvas);
            e.Handled = true;
            return;
        }

        // 2. Check if click is on crop frame body - start crop move
        // (skip if Lock is checked - that locks both move and resize-aspect)
        if (CropOverlay.IsVisible && e.Source is Control source &&
            (source == CropFrame || IsChildOf(source, CropFrame)))
        {
            if (LockAspectCheck.IsChecked == true)
            {
                // Lock active: do not start move drag; consume event to prevent pan
                e.Handled = true;
                return;
            }
            _isDraggingCropFrame = true;
            _dragStart = e.GetPosition(ImageCanvas);
            _cropDragStartMargin = CropFrame.Margin;
            e.Pointer.Capture(ImageCanvas);
            e.Handled = true;
            return;
        }

        // 3. If the active preset has a fixed crop window (Fill mode against a
        //    device preset), drag pans the source image *inside* that crop —
        //    matching the wallpaper-picker UX in the Download Preset dialog.
        if (CanPanCrop)
        {
            _isPanningCrop = true;
            _cropPanStartPoint = e.GetPosition(EditorImage);
            _cropPanStartOffsetX = _currentPreset.ResizeSettings.CropOffsetX;
            _cropPanStartOffsetY = _currentPreset.ResizeSettings.CropOffsetY;
            UpdatePreviewCursor();
            e.Pointer.Capture(ImageCanvas);
            e.Handled = true;
            return;
        }

        // 4. Otherwise, start image panning via ScrollViewer offset
        _isPanning = true;
        _panStartPoint = e.GetPosition(ImageScrollViewer);
        _panStartOffset = ImageScrollViewer.Offset;
        e.Pointer.Capture(ImageCanvas);
        e.Handled = true;
    }

    private static bool IsChildOf(Control? child, Control parent)
    {
        var current = child?.Parent as Control;
        while (current != null)
        {
            if (current == parent) return true;
            current = current.Parent as Control;
        }
        return false;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        // Resize handle drag
        if (_resizingHandle != null)
        {
            var point = e.GetPosition(ImageCanvas);
            var dx = point.X - _dragStart.X;
            var dy = point.Y - _dragStart.Y;

            double newW = _resizeStartWidth;
            double newH = _resizeStartHeight;
            double offsetX = (_resizeStartMargin.Left - _resizeStartMargin.Right) / 2.0;
            double offsetY = (_resizeStartMargin.Top - _resizeStartMargin.Bottom) / 2.0;

            // Lock aspect ratio when LockAspectCheck is checked AND we're on a corner handle
            bool lockAspect = LockAspectCheck.IsChecked == true && _resizingHandle.Length == 2;
            double aspect = _resizeStartHeight > 0 ? _resizeStartWidth / _resizeStartHeight : 1.0;

            switch (_resizingHandle)
            {
                case "TL":
                    newW = Math.Max(MinCropSize, _resizeStartWidth - dx);
                    newH = Math.Max(MinCropSize, _resizeStartHeight - dy);
                    if (lockAspect) { if (Math.Abs(dx) > Math.Abs(dy)) newH = newW / aspect; else newW = newH * aspect; }
                    offsetX += (_resizeStartWidth - newW) / 2.0;
                    offsetY += (_resizeStartHeight - newH) / 2.0;
                    break;
                case "TR":
                    newW = Math.Max(MinCropSize, _resizeStartWidth + dx);
                    newH = Math.Max(MinCropSize, _resizeStartHeight - dy);
                    if (lockAspect) { if (Math.Abs(dx) > Math.Abs(dy)) newH = newW / aspect; else newW = newH * aspect; }
                    offsetX += (newW - _resizeStartWidth) / 2.0;
                    offsetY += (_resizeStartHeight - newH) / 2.0;
                    break;
                case "BL":
                    newW = Math.Max(MinCropSize, _resizeStartWidth - dx);
                    newH = Math.Max(MinCropSize, _resizeStartHeight + dy);
                    if (lockAspect) { if (Math.Abs(dx) > Math.Abs(dy)) newH = newW / aspect; else newW = newH * aspect; }
                    offsetX += (_resizeStartWidth - newW) / 2.0;
                    offsetY += (newH - _resizeStartHeight) / 2.0;
                    break;
                case "BR":
                    newW = Math.Max(MinCropSize, _resizeStartWidth + dx);
                    newH = Math.Max(MinCropSize, _resizeStartHeight + dy);
                    if (lockAspect) { if (Math.Abs(dx) > Math.Abs(dy)) newH = newW / aspect; else newW = newH * aspect; }
                    offsetX += (newW - _resizeStartWidth) / 2.0;
                    offsetY += (newH - _resizeStartHeight) / 2.0;
                    break;
                case "T":
                    newH = Math.Max(MinCropSize, _resizeStartHeight - dy);
                    offsetY += (_resizeStartHeight - newH) / 2.0;
                    break;
                case "B":
                    newH = Math.Max(MinCropSize, _resizeStartHeight + dy);
                    offsetY += (newH - _resizeStartHeight) / 2.0;
                    break;
                case "L":
                    newW = Math.Max(MinCropSize, _resizeStartWidth - dx);
                    offsetX += (_resizeStartWidth - newW) / 2.0;
                    break;
                case "R":
                    newW = Math.Max(MinCropSize, _resizeStartWidth + dx);
                    offsetX += (newW - _resizeStartWidth) / 2.0;
                    break;
            }

            CropFrame.Width = newW;
            CropFrame.Height = newH;
            CropFrame.Margin = new global::Avalonia.Thickness(offsetX, offsetY, -offsetX, -offsetY);
            return;
        }

        // Crop frame move
        if (_isDraggingCropFrame)
        {
            var point = e.GetPosition(ImageCanvas);
            var deltaX = point.X - _dragStart.X;
            var deltaY = point.Y - _dragStart.Y;

            CropFrame.Margin = new global::Avalonia.Thickness(
                _cropDragStartMargin.Left + deltaX,
                _cropDragStartMargin.Top + deltaY,
                _cropDragStartMargin.Right - deltaX,
                _cropDragStartMargin.Bottom - deltaY);
            return;
        }

        // Crop offset pan (Fill-mode wallpaper-style drag)
        if (_isPanningCrop)
        {
            var point = e.GetPosition(EditorImage);
            var bounds = EditorImage.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var dx = point.X - _cropPanStartPoint.X;
            var dy = point.Y - _cropPanStartPoint.Y;
            // Drag right => reveal more of left edge => decrease offset
            var newOffsetX = Math.Clamp(_cropPanStartOffsetX - 2.0 * dx / bounds.Width, -1.0, 1.0);
            var newOffsetY = Math.Clamp(_cropPanStartOffsetY - 2.0 * dy / bounds.Height, -1.0, 1.0);

            // Always store the latest offset so the next render uses it…
            if (Math.Abs(newOffsetX - _currentPreset.ResizeSettings.CropOffsetX) > 0.001
                || Math.Abs(newOffsetY - _currentPreset.ResizeSettings.CropOffsetY) > 0.001)
            {
                _currentPreset.ResizeSettings.CropOffsetX = newOffsetX;
                _currentPreset.ResizeSettings.CropOffsetY = newOffsetY;
                // …but only kick a new render if one isn't already running.
                // A trailing release-time UpdatePreviewAsync guarantees the
                // final position is rendered.
                if (!_previewRenderInFlight)
                {
                    _ = UpdatePreviewAsync();
                }
            }
            return;
        }

        // Image pan
        if (_isPanning)
        {
            var point = e.GetPosition(ImageScrollViewer);
            var deltaX = _panStartPoint.X - point.X;
            var deltaY = _panStartPoint.Y - point.Y;

            var newX = Math.Clamp(_panStartOffset.X + deltaX, 0, Math.Max(0, ImageScrollViewer.Extent.Width - ImageScrollViewer.Viewport.Width));
            var newY = Math.Clamp(_panStartOffset.Y + deltaY, 0, Math.Max(0, ImageScrollViewer.Extent.Height - ImageScrollViewer.Viewport.Height));
            ImageScrollViewer.Offset = new global::Avalonia.Vector(newX, newY);
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingHandle != null)
        {
            _resizingHandle = null;
            e.Pointer.Capture(null);
            SyncPresetFromUI();
            _ = UpdatePreviewAsync();
            return;
        }

        if (_isDraggingCropFrame)
        {
            _isDraggingCropFrame = false;
            e.Pointer.Capture(null);
            SyncPresetFromUI();
            _ = UpdatePreviewAsync();
            return;
        }

        if (_isPanningCrop)
        {
            _isPanningCrop = false;
            e.Pointer.Capture(null);
            UpdatePreviewCursor();
            // Render the final high-quality preview now that the drag is over.
            _ = UpdatePreviewAsync();
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
        }
    }

    /// <summary>
    /// Loads ugoira with mode selection dialog offering 4 options:
    /// 1. Watch Animation - plays the animated ugoira
    /// 2. Edit Single Frame - extracts one frame for editing
    /// 3. Edit All Frames - extracts all frames for batch processing
    /// 4. Edit Cover - extracts first frame only
    /// </summary>
    private async Task LoadUgoiraPreviewAsync()
    {
        try
        {
            var artwork = _artworks[_currentArtworkIndex];
            if (string.IsNullOrEmpty(artwork.ArtworkId)) return;

            // Check if we should show the ugoira mode dialog
            UgoiraPresetMode mode = _currentPreset?.UgoiraMode ?? default;
            var showDialog = true;

            // If explicitly requested (Ugoira Options button), always show dialog
            if (_forceShowUgoiraDialog)
            {
                showDialog = true;
            }
            // Otherwise, if preset has a saved mode, use it (skip dialog)
            else if (_currentPreset?.UgoiraMode != null &&
                     _currentPreset.UgoiraMode != default)
            {
                mode = _currentPreset.UgoiraMode;
                showDialog = false;
            }

            if (showDialog)
            {
                // Prevent double-showing dialog
                if (_ugoiraDialogShowing) return;
                _ugoiraDialogShowing = true;

                // Show mode selection dialog
                var dialogMode = await ShowUgoiraModeDialogAsync();
                _ugoiraDialogShowing = false;
                _forceShowUgoiraDialog = false; // Reset so we don't show again
                if (dialogMode == null) return; // User cancelled
                if (_currentPreset == null) return;
                mode = dialogMode.Value;

            // Save the selected mode to the preset
            _currentPreset.UgoiraMode = mode;

            // Show the mode switcher button in toolbar
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true;
            });

            var ugoiraService = AppServices.Get<UgoiraService>();

            switch (mode)
            {
                case UgoiraPresetMode.WatchAnimation:
                    await LoadUgoiraAnimationAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditSingleFrame:
                    await LoadUgoiraSingleFrameAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditAllFrames:
                    await LoadUgoiraAllFramesAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditCover:
                    await LoadUgoiraCoverAsync(ugoiraService, artwork.ArtworkId);
                    break;
            }
            }
        }
        catch (Exception ex)
        {
            _ugoiraDialogShowing = false; // Reset flag on error
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ImageInfoText.Text = $"Ugoira load failed: {ex.Message}";
            });
        }
    }

    // UgoiraPresetMode is defined in Pikura.Core.Models (UgoiraPresetMode)

    private async Task<UgoiraPresetMode?> ShowUgoiraModeDialogAsync()
    {
        var tcs = new TaskCompletionSource<UgoiraPresetMode?>();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new Window
            {
                Title = "Ugoira Options",
                Width = 480,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = this.Background
            };

            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock
            {
                Text = "This is an animated ugoira. Choose how to open it:",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            void AddOption(string title, string desc, UgoiraPresetMode mode)
            {
                var btn = new Button
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13 },
                            new TextBlock { Text = desc, FontSize = 11, Opacity = 0.7 }
                        }
                    },
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(6)
                };
                btn.Click += (_, _) =>
                {
                    tcs.TrySetResult(mode);
                    dialog.Close();
                };
                panel.Children.Add(btn);
            }

            // Pre-select the current preset mode if any
            var currentMode = _currentPreset?.UgoiraMode ?? UgoiraPresetMode.EditSingleFrame;

            AddOption("▶ Watch Animation", "Play the animated ugoira (no editing)", UgoiraPresetMode.WatchAnimation);
            AddOption("🖼 Edit Single Frame", "Extract one frame to edit as static image", UgoiraPresetMode.EditSingleFrame);
            AddOption("📑 Edit All Frames", "Extract all frames for batch processing", UgoiraPresetMode.EditAllFrames);
            AddOption("🎴 Edit Cover", "Extract the first frame (cover image)", UgoiraPresetMode.EditCover);

            dialog.Content = panel;
            dialog.Closed += (_, _) => tcs.TrySetResult(null);
            dialog.ShowDialog(this);
        });

        return await tcs.Task;
    }

    private async Task LoadUgoiraAnimationAsync(UgoiraService service, string artworkId)
    {
        var previewPath = await service.GetOrCreatePreviewAsync(artworkId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = false;
            UgoiraImage.IsVisible = true;
            UgoiraImage.SourcePath = previewPath;
            UgoiraImage.IsPlaying = true;
            LoadingSpinner.IsVisible = false;
            CropOverlay.IsVisible = false;
            ImageInfoText.Text = "Ugoira (animated) - watching only";
        });
    }

    private async Task LoadUgoiraSingleFrameAsync(UgoiraService service, string artworkId)
    {
        // Get metadata to know frame count
        var pixivClient = AppServices.Get<Pikura.Core.Services.PixivClient>();
        var meta = await pixivClient.GetUgoiraMetaAsync(artworkId);
        var frameCount = meta?.Frames?.Count ?? 0;
        if (frameCount == 0) throw new Exception("Could not determine frame count");

        // Show frame picker dialog with preview
        var selectedFrame = await ShowFramePickerDialogAsync(artworkId, frameCount);
        _forceShowUgoiraDialog = false; // Reset after showing picker
        if (selectedFrame == null) throw new Exception("No frame selected");

        var frameIndex = selectedFrame.Value;

        // Extract selected frame as PNG
        var framePath = await service.ExtractSingleFrameAsync(artworkId, frameIndex: frameIndex);
        if (framePath == null) throw new Exception("Failed to extract frame");

        // Load as bitmap and treat as regular static image
        var bitmap = SKBitmap.Decode(framePath);
        if (bitmap == null) throw new Exception("Failed to decode frame");

        // Replace the current artwork's pages with this single frame
        var artwork = _artworks[_currentArtworkIndex];
        artwork.Pages.Clear();
        artwork.Pages.Add(bitmap);
        artwork.PageCount = 1; // Set PageCount to 1 for single frame
        _currentPageIndex = 0;
        _originalBitmap = bitmap;
        _originalAspectRatio = (float)bitmap.Width / bitmap.Height;

        // Mark as non-ugoira so it renders as static image
        _isUgoira = false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = $"Ugoira - Editing frame {frameIndex + 1} of {frameCount} (static)";
            UpdateNavigationUI(); // Update nav to show single page
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true; // Keep mode button visible
        });

        await UpdatePreviewAsync();
    }

    /// <summary>
    /// Shows a dialog to pick which frame to extract from the ugoira with live preview.
    /// </summary>
    private async Task<int?> ShowFramePickerDialogAsync(string artworkId, int frameCount)
    {
        var tcs = new TaskCompletionSource<int?>();
        var ugoiraService = AppServices.Get<UgoiraService>();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new Window
            {
                Title = "Select Frame",
                Width = 500,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = this.Background
            };

            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock
            {
                Text = $"This ugoira has {frameCount} frames. Drag slider to preview:",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            // Preview image
            var previewImage = new Image
            {
                Width = 320,
                Height = 180,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 8)
            };
            panel.Children.Add(previewImage);

            var frameText = new TextBlock
            {
                Text = "Frame 1",
                FontSize = 13,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
            };

            var slider = new Slider
            {
                Minimum = 1,
                Maximum = frameCount,
                Value = 1,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Width = 400
            };

            // Debounce timer for preview loading
            System.Threading.Timer? previewTimer = null;

            slider.ValueChanged += async (_, e) =>
            {
                var frameNum = (int)e.NewValue;
                frameText.Text = $"Frame {frameNum}";

                // Debounce - wait 100ms before loading preview
                previewTimer?.Dispose();
                previewTimer = new System.Threading.Timer(async _ =>
                {
                    try
                    {
                        var framePath = await ugoiraService.ExtractSingleFrameAsync(artworkId, frameNum - 1);
                        if (framePath != null)
                        {
                            var bitmap = SKBitmap.Decode(framePath);
                            if (bitmap != null)
                            {
                                var avaloniaBitmap = SkiaToAvalonia(bitmap);
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    previewImage.Source = avaloniaBitmap;
                                });
                            }
                        }
                    }
                    catch { /* ignore preview errors */ }
                }, null, 100, System.Threading.Timeout.Infinite);
            };

            panel.Children.Add(slider);
            panel.Children.Add(frameText);

            var buttonPanel = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 8) };
            cancelBtn.Click += (_, _) =>
            {
                previewTimer?.Dispose();
                tcs.TrySetResult(null);
                dialog.Close();
            };

            var okBtn = new Button { Content = "OK", Classes = { "accent" }, Padding = new Thickness(16, 8) };
            okBtn.Click += (_, _) =>
            {
                previewTimer?.Dispose();
                tcs.TrySetResult((int)slider.Value - 1); // Convert to 0-based index
                dialog.Close();
            };

            buttonPanel.Children.Add(cancelBtn);
            buttonPanel.Children.Add(okBtn);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            dialog.Closed += (_, _) =>
            {
                previewTimer?.Dispose();
                tcs.TrySetResult(null);
            };

            dialog.ShowDialog(this);

            // Load initial preview
            _ = Task.Run(async () =>
            {
                try
                {
                    var framePath = await ugoiraService.ExtractSingleFrameAsync(artworkId, 0);
                    if (framePath != null)
                    {
                        var bitmap = SKBitmap.Decode(framePath);
                        if (bitmap != null)
                        {
                            var avaloniaBitmap = SkiaToAvalonia(bitmap);
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                previewImage.Source = avaloniaBitmap;
                            });
                        }
                    }
                }
                catch { /* ignore */ }
            });
        });

        return await tcs.Task;
    }

    private async Task LoadUgoiraAllFramesAsync(UgoiraService service, string artworkId)
    {
        var framePaths = await service.ExtractAllFramesAsync(artworkId);
        if (framePaths.Count == 0) throw new Exception("Failed to extract frames");

        // Load all frames as bitmaps
        var bitmaps = new System.Collections.Generic.List<SKBitmap>();
        foreach (var path in framePaths)
        {
            var bmp = SKBitmap.Decode(path);
            if (bmp != null) bitmaps.Add(bmp);
        }

        if (bitmaps.Count == 0) throw new Exception("Failed to decode any frames");

        // Replace artwork pages with all frames
        var artwork = _artworks[_currentArtworkIndex];
        artwork.Pages.Clear();
        foreach (var bmp in bitmaps)
            artwork.Pages.Add(bmp);
        
        // IMPORTANT: Update PageCount to match the number of frames loaded
        artwork.PageCount = bitmaps.Count;

        // Reset to first frame
        _currentPageIndex = 0;
        _originalBitmap = bitmaps[0];
        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;

        // Invalidate cached preview
        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
        _cachedPreviewSourceWidth = -1;

        // CRITICAL: Mark as non-ugoira so UpdatePreviewAsync treats frames as regular images
        // This prevents it from calling LoadUgoiraPreviewAsync() again
        _isUgoira = false;

        // Update UI on main thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = $"Ugoira - Batch editing {bitmaps.Count} frames";
            
            // Force navigation UI update
            UpdateNavigationUI();
            
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true; // Keep mode button visible
        });

        // Generate and display preview for first frame
        await UpdatePreviewAsync(immediate: true);
    }

    private async Task LoadUgoiraCoverAsync(UgoiraService service, string artworkId)
    {
        // Same as single frame but explicitly for cover
        var coverPath = await service.GetCoverImageAsync(artworkId);
        if (coverPath == null) throw new Exception("Failed to extract cover");

        var bitmap = SKBitmap.Decode(coverPath);
        if (bitmap == null) throw new Exception("Failed to decode cover");

        var artwork = _artworks[_currentArtworkIndex];
        artwork.Pages.Clear();
        artwork.Pages.Add(bitmap);
        artwork.PageCount = 1; // Cover is single image
        _currentPageIndex = 0;
        _originalBitmap = bitmap;
        _originalAspectRatio = (float)bitmap.Width / bitmap.Height;

        // Mark as non-ugoira so it renders as static image
        _isUgoira = false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = "Ugoira - Editing cover image (static)";
            UpdateNavigationUI();
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true; // Keep mode button visible
        });

        await UpdatePreviewAsync();
    }

    /// <summary>
    /// Loads ugoira content for a specific artwork when navigating between multiple artworks.
    /// Shows dialog if needed, applies preset mode if available.
    /// </summary>
    private async Task LoadUgoiraForArtworkAsync(EditableArtwork artwork)
    {
        if (string.IsNullOrEmpty(artwork.ArtworkId)) return;
        if (artwork.Pages.Count > 0) return; // Already loaded

        _isUgoira = true;
        
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoadingSpinner.IsVisible = true;
            ImageInfoText.Text = "Loading ugoira...";
        });

        try
        {
            var ugoiraService = AppServices.Get<UgoiraService>();
            
            // Determine which mode to use
            UgoiraPresetMode mode;
            var showDialog = _currentPreset?.UgoiraMode == null || _currentPreset.UgoiraMode == default;
            
            if (showDialog && !_ugoiraDialogShowing)
            {
                _ugoiraDialogShowing = true;
                var dialogMode = await ShowUgoiraModeDialogAsync();
                _ugoiraDialogShowing = false;
                _forceShowUgoiraDialog = false;
                
                if (dialogMode == null) return; // User cancelled
                if (_currentPreset == null) return;
                mode = dialogMode.Value;
                _currentPreset.UgoiraMode = mode; // Save for remaining artworks
            }
            else
            {
                mode = _currentPreset?.UgoiraMode ?? UgoiraPresetMode.EditSingleFrame;
            }

            // Process based on selected mode
            switch (mode)
            {
                case UgoiraPresetMode.WatchAnimation:
                    await LoadUgoiraAnimationAsync(ugoiraService, artwork.ArtworkId);
                    break;
                case UgoiraPresetMode.EditSingleFrame:
                    await LoadUgoiraSingleFrameForArtworkAsync(ugoiraService, artwork);
                    break;
                case UgoiraPresetMode.EditAllFrames:
                    await LoadUgoiraAllFramesForArtworkAsync(ugoiraService, artwork);
                    break;
                case UgoiraPresetMode.EditCover:
                    await LoadUgoiraCoverForArtworkAsync(ugoiraService, artwork);
                    break;
            }

            // Update UI after loading
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateNavigationUI();
            });
        }
        catch (Exception ex)
        {
            _ugoiraDialogShowing = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ImageInfoText.Text = $"Error loading ugoira: {ex.Message}";
                LoadingSpinner.IsVisible = false;
            });
        }
    }

    /// <summary>
    /// Loads single frame for a specific artwork (used in multi-artwork navigation).
    /// </summary>
    private async Task LoadUgoiraSingleFrameForArtworkAsync(UgoiraService service, EditableArtwork artwork)
    {
        var pixivClient = AppServices.Get<Pikura.Core.Services.PixivClient>();
        var meta = await pixivClient.GetUgoiraMetaAsync(artwork.ArtworkId);
        var frameCount = meta?.Frames?.Count ?? 0;
        
        // For simplicity in batch mode, always use frame 1
        var frameIndex = 0;
        
        var framePath = await service.ExtractSingleFrameAsync(artwork.ArtworkId, frameIndex);
        if (framePath == null) throw new Exception("Failed to extract frame");

        var bitmap = SKBitmap.Decode(framePath);
        if (bitmap == null) throw new Exception("Failed to decode frame");

        artwork.Pages.Clear();
        artwork.Pages.Add(bitmap);
        artwork.PageCount = 1;
        
        _originalBitmap = bitmap;
        _originalAspectRatio = (float)bitmap.Width / bitmap.Height;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = $"Ugoira - Frame 1 of {frameCount} (static)";
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true;
        });

        await UpdatePreviewAsync(immediate: true);
    }

    /// <summary>
    /// Loads all frames for a specific artwork (used in multi-artwork navigation).
    /// </summary>
    private async Task LoadUgoiraAllFramesForArtworkAsync(UgoiraService service, EditableArtwork artwork)
    {
        var framePaths = await service.ExtractAllFramesAsync(artwork.ArtworkId);
        if (framePaths.Count == 0) throw new Exception("Failed to extract frames");

        var bitmaps = new System.Collections.Generic.List<SKBitmap>();
        foreach (var path in framePaths)
        {
            var bmp = SKBitmap.Decode(path);
            if (bmp != null) bitmaps.Add(bmp);
        }

        if (bitmaps.Count == 0) throw new Exception("Failed to decode any frames");

        artwork.Pages.Clear();
        foreach (var bmp in bitmaps)
            artwork.Pages.Add(bmp);
        
        artwork.PageCount = bitmaps.Count;
        _currentPageIndex = 0;
        _originalBitmap = bitmaps[0];
        _originalAspectRatio = (float)_originalBitmap.Width / _originalBitmap.Height;

        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
        _cachedPreviewSourceWidth = -1;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = $"Ugoira - Batch editing {bitmaps.Count} frames";
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true;
        });

        await UpdatePreviewAsync(immediate: true);
    }

    /// <summary>
    /// Loads cover for a specific artwork (used in multi-artwork navigation).
    /// </summary>
    private async Task LoadUgoiraCoverForArtworkAsync(UgoiraService service, EditableArtwork artwork)
    {
        var coverPath = await service.GetCoverImageAsync(artwork.ArtworkId);
        if (coverPath == null) throw new Exception("Failed to extract cover");

        var bitmap = SKBitmap.Decode(coverPath);
        if (bitmap == null) throw new Exception("Failed to decode cover");

        artwork.Pages.Clear();
        artwork.Pages.Add(bitmap);
        artwork.PageCount = 1;
        
        _originalBitmap = bitmap;
        _originalAspectRatio = (float)bitmap.Width / bitmap.Height;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EditorImage.IsVisible = true;
            UgoiraImage.IsVisible = false;
            UgoiraImage.SourcePath = null;
            LoadingSpinner.IsVisible = false;
            ImageInfoText.Text = "Ugoira - Cover image (static)";
            if (UgoiraModePanel != null) UgoiraModePanel.IsVisible = true;
        });

        await UpdatePreviewAsync(immediate: true);
    }
}

/// <summary>
/// Extension methods for ImageEditPreset
/// </summary>
public static class ImageEditPresetExtensions
{
    public static ImageEditPreset Clone(this ImageEditPreset original)
    {
        return new ImageEditPreset
        {
            Id = original.Id,
            Name = original.Name,
            IsBuiltIn = original.IsBuiltIn,
            CreatedAt = original.CreatedAt,
            LastUsedAt = original.LastUsedAt,
            DevicePreset = original.DevicePreset,
            ResizeSettings = new ResizeSettings
            {
                TargetWidth = original.ResizeSettings.TargetWidth,
                TargetHeight = original.ResizeSettings.TargetHeight,
                Mode = original.ResizeSettings.Mode,
                OutputFormat = original.ResizeSettings.OutputFormat,
                JpegQuality = original.ResizeSettings.JpegQuality,
                LockAspectRatio = original.ResizeSettings.LockAspectRatio,
                AspectRatio = original.ResizeSettings.AspectRatio,
                IsPortrait = original.ResizeSettings.IsPortrait,
                CropOffsetX = original.ResizeSettings.CropOffsetX,
                CropOffsetY = original.ResizeSettings.CropOffsetY
            },
            Adjustments = new ImageAdjustments
            {
                Brightness = original.Adjustments.Brightness,
                Contrast = original.Adjustments.Contrast,
                Saturation = original.Adjustments.Saturation,
                HueRotation = original.Adjustments.HueRotation,
                Temperature = original.Adjustments.Temperature,
                Tint = original.Adjustments.Tint,
                Highlights = original.Adjustments.Highlights,
                Shadows = original.Adjustments.Shadows,
                Sharpness = original.Adjustments.Sharpness,
                BlurRadius = original.Adjustments.BlurRadius,
                VignetteIntensity = original.Adjustments.VignetteIntensity,
                VignetteRadius = original.Adjustments.VignetteRadius,
                ColorOverlayHex = original.Adjustments.ColorOverlayHex,
                ColorOverlayOpacity = original.Adjustments.ColorOverlayOpacity
            },
            CropRegion = original.CropRegion == null ? null : new CropRegion
            {
                X = original.CropRegion.X,
                Y = original.CropRegion.Y,
                Width = original.CropRegion.Width,
                Height = original.CropRegion.Height,
                IsRelative = original.CropRegion.IsRelative
            },
            SaveAsNew = original.SaveAsNew,
            ApplyToAllPages = original.ApplyToAllPages,
            SelectedPageIndices = original.SelectedPageIndices?.ToList(), // Clone the list
            CustomOutputFolder = original.CustomOutputFolder,
            AlsoDownloadUnprocessed = original.AlsoDownloadUnprocessed
        };
    }
}
