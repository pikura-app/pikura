using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using SkiaSharp;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class DownloadPresetWindow : Window
{
    private readonly ImageResizeService? _imageResizeService;
    private readonly DialogService? _dialogService;
    private readonly PixivImageLoader? _imageLoader;
    private readonly PixivClient? _pixivClient;

    private List<ArtworkPreview> _selectedArtworks = new();
    private Dictionary<int, List<int>?> _batchPageSelections = new();
    private List<PresetViewModel> _presets = new();
    private int _currentImageIndex = 0;
    private int _currentPageIndex = 0;
    private Dictionary<int, int> _rememberedPageIndex = new();
    private Dictionary<(int artworkIndex, int pageIndex), ImageEditPreset> _perPagePresets = new();
    private HashSet<int> _selectedArtworkApplyIndices = new();
    private SKBitmap? _currentOriginalBitmap;
    private ImageEditPreset _currentPreset;
    private CancellationTokenSource? _previewDebounceCts;

    // Ugoira (animated) support
    private bool _isCurrentUgoira;
    private string? _currentArtworkId;

    // Drag-to-pan state for repositioning the Fill-mode crop window. Coords
    // are in PreviewImage local pixels; the delta is converted to a -1..1
    // CropOffset before being applied to the preset.
    private bool _isDraggingPreview;
    private global::Avalonia.Point _dragStartPoint;
    private double _dragStartCropOffsetX;
    private double _dragStartCropOffsetY;
    // True while a preview render is in flight — throttles pan ticks so we
    // never queue more work than the renderer can keep up with.
    private bool _previewRenderInFlight;

    public ImageEditPreset? SelectedPreset { get; private set; }
    public bool DownloadClicked { get; private set; }
    /// <summary>
    /// Per-page preset overrides. Key is (artworkIndex, pageIndex).
    /// Pages without an entry use SelectedPreset as fallback.
    /// </summary>
    public IReadOnlyDictionary<(int artworkIndex, int pageIndex), ImageEditPreset> PerPagePresets => _perPagePresets;

    public DownloadPresetWindow()
    {
        InitializeComponent();
        _currentPreset = new ImageEditPreset { DevicePreset = DevicePreset.Original };

        // Keyboard navigation: ←/→ or PageUp/PageDown to navigate between
        // pages of the current artwork and across artworks.
        KeyDown += OnWindowKeyDown;

        // Drag-to-pan handlers for repositioning the Fill-mode crop region.
        PreviewImage.PointerPressed += OnPreviewPointerPressed;
        PreviewImage.PointerMoved += OnPreviewPointerMoved;
        PreviewImage.PointerReleased += OnPreviewPointerReleased;
        PreviewImage.PointerCaptureLost += OnPreviewPointerCaptureLost;
        PreviewImage.Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.SizeAll);
    }

    // ── Drag-to-pan ──────────────────────────────────────────────────────────

    /// <summary>
    /// True when the active preset is in <see cref="ResizeMode.Fill"/> against a
    /// non-Original device preset — i.e. when there's actually a crop window
    /// the user can pan around.
    /// </summary>
    private bool CanPanPreview =>
        _currentPreset.DevicePreset != DevicePreset.Original
        && _currentPreset.DevicePreset != DevicePreset.None
        && _currentPreset.ResizeSettings.Mode == ResizeMode.Fill;

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanPanPreview || PreviewImage.Source == null) return;
        var props = e.GetCurrentPoint(PreviewImage).Properties;
        if (!props.IsLeftButtonPressed) return;

        _isDraggingPreview = true;
        _dragStartPoint = e.GetPosition(PreviewImage);
        _dragStartCropOffsetX = _currentPreset.ResizeSettings.CropOffsetX;
        _dragStartCropOffsetY = _currentPreset.ResizeSettings.CropOffsetY;
        e.Pointer.Capture(PreviewImage);
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingPreview) return;
        var current = e.GetPosition(PreviewImage);
        var bounds = PreviewImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Map drag delta (in displayed-preview pixels) to normalized -1..1
        // CropOffset. Dragging right/down pans the visible region in the
        // same direction (reveal more of the right/bottom of the source),
        // which is the opposite sign of the cursor delta.
        var dx = current.X - _dragStartPoint.X;
        var dy = current.Y - _dragStartPoint.Y;
        var newOffsetX = Math.Clamp(_dragStartCropOffsetX - 2.0 * dx / bounds.Width, -1.0, 1.0);
        var newOffsetY = Math.Clamp(_dragStartCropOffsetY - 2.0 * dy / bounds.Height, -1.0, 1.0);

        if (Math.Abs(newOffsetX - _currentPreset.ResizeSettings.CropOffsetX) < 0.001
            && Math.Abs(newOffsetY - _currentPreset.ResizeSettings.CropOffsetY) < 0.001)
        {
            return;
        }

        // Always store the latest offset so the next render reflects it…
        _currentPreset.ResizeSettings.CropOffsetX = newOffsetX;
        _currentPreset.ResizeSettings.CropOffsetY = newOffsetY;
        // …but only kick a new render if one isn't already running. A
        // pointer-release UpdatePreviewAsync guarantees the final tick.
        if (!_previewRenderInFlight)
        {
            _ = UpdatePreviewAsync();
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingPreview) return;
        _isDraggingPreview = false;
        e.Pointer.Capture(null);
        // Force one final high-quality render now that the drag is over —
        // the in-flight previews used a coarser maxWidth.
        _ = UpdatePreviewAsync();
        e.Handled = true;
    }

    private void OnPreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isDraggingPreview)
        {
            _isDraggingPreview = false;
            _ = UpdatePreviewAsync();
        }
    }

    protected override void OnClosing(global::Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _previewDebounceCts?.Cancel();
        _cachedPreviewSource?.Dispose();
        _cachedPreviewSource = null;
        _currentOriginalBitmap?.Dispose();
    }

    private async void OnWindowKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        // Don't intercept arrows when user is typing in a text input or
        // adjusting a slider/numeric box.
        if (FocusManager?.GetFocusedElement() is global::Avalonia.Controls.TextBox or
            global::Avalonia.Controls.NumericUpDown or
            global::Avalonia.Controls.Slider)
        {
            return;
        }

        var artwork = GetCurrentArtwork();
        switch (e.Key)
        {
            case global::Avalonia.Input.Key.Left:
            case global::Avalonia.Input.Key.PageUp:
                e.Handled = true;
                // Navigate pages within the current artwork first
                if (artwork != null && artwork.PageCount > 1 && _currentPageIndex > 0)
                {
                    _currentPageIndex--;
                    StopUgoiraPlayback();
                    await LoadCurrentImageAsync();
                    UpdateSelectedCount();
                }
                else
                {
                    OnPrevImageClick(sender, new RoutedEventArgs());
                }
                break;
            case global::Avalonia.Input.Key.Right:
            case global::Avalonia.Input.Key.PageDown:
                e.Handled = true;
                // Navigate pages within the current artwork first
                if (artwork != null && artwork.PageCount > 1 && _currentPageIndex < artwork.PageCount - 1)
                {
                    _currentPageIndex++;
                    StopUgoiraPlayback();
                    await LoadCurrentImageAsync();
                    UpdateSelectedCount();
                }
                else
                {
                    OnNextImageClick(sender, new RoutedEventArgs());
                }
                break;
        }
    }

    public DownloadPresetWindow(
        ImageResizeService imageResizeService,
        DialogService dialogService,
        PixivImageLoader imageLoader,
        PixivClient pixivClient,
        List<ArtworkPreview> selectedArtworks,
        List<ImageEditPreset>? customPresets = null) : this()
    {
        _imageResizeService = imageResizeService;
        _dialogService = dialogService;
        _imageLoader = imageLoader;
        _pixivClient = pixivClient;
        _selectedArtworks = selectedArtworks;

        // Initialize presets list
        InitializePresets(customPresets);

        // Build the selected-artworks checklist for per-artwork preset application
        PopulateApplyToArtworksList();

        // Initialize the download artwork selection controls
        InitializeDownloadArtworksSelection();

        // Update UI
        UpdateSelectedCount();
        _ = LoadCurrentImageAsync();
    }

    private void InitializePresets(List<ImageEditPreset>? customPresets)
    {
        // Add built-in presets
        var builtIn = ImageEditPreset.GetBuiltInPresets();
        _presets = builtIn.Select(p => new PresetViewModel { Preset = p, IsBuiltIn = true }).ToList();

        // Add custom presets
        if (customPresets != null)
        {
            foreach (var custom in customPresets)
            {
                _presets.Add(new PresetViewModel { Preset = custom, IsBuiltIn = false });
            }
        }

        // Select first preset (Original)
        if (_presets.Count > 0)
        {
            _presets[0].IsSelected = true;
            _currentPreset = _presets[0].Preset;

            // Sync ugoira checkboxes with initial preset
            if (UgoiraSaveFramesCheck != null)
                UgoiraSaveFramesCheck.IsChecked = _currentPreset.SaveUgoiraFrames;
            if (UgoiraFramesOnlyCheck != null)
                UgoiraFramesOnlyCheck.IsChecked = _currentPreset.UgoiraFramesOnly;
        }

        PresetList.ItemsSource = _presets;
    }

    private void UpdateSelectedCount()
    {
        var count = _selectedArtworks.Count;
        var totalPages = _selectedArtworks.Sum(a => a.PageCount);
        var currentArtwork = GetCurrentArtwork();

        SelectedCountText.Text = $"{count} image{(count != 1 ? "s" : "")} selected";

        // Update submission info text (shows "Submission X of Y" for multi-selection)
        if (SubmissionInfoText != null)
        {
            if (count > 1)
            {
                SubmissionInfoText.Text = $"Submission {_currentImageIndex + 1} of {count}";
                SubmissionInfoText.IsVisible = true;
            }
            else
            {
                SubmissionInfoText.IsVisible = false;
            }
        }

        // Show multi-page info when any artwork has multiple pages
        if (totalPages > count)
        {
            if (MultiPageInfoText != null)
            {
                MultiPageInfoText.Text = $"{totalPages} total pages across {count} submissions";
                MultiPageInfoText.IsVisible = true;
            }
        }
        else
        {
            if (MultiPageInfoText != null)
                MultiPageInfoText.IsVisible = false;
        }

        // Note: Ugoira handling is now automatic in Custom Edit - no separate button needed

        // Show batch mode panel when multiple submissions selected
        if (BatchModePanel != null)
        {
            BatchModePanel.IsVisible = count > 1;
            if (count > 1 && EnableBatchModeCheck?.IsChecked == true)
            {
                PopulateBatchSubmissionsList();
            }
        }

        // Update navigation indicator and buttons
        if (currentArtwork != null)
        {
            var hasMultiplePages = currentArtwork.PageCount > 1;
            var hasMultipleArtworks = count > 1;

            // Submission indicator (shows X/Y for multi-selection)
            if (SubmissionIndicator != null)
                SubmissionIndicator.Text = $"{_currentImageIndex + 1} / {count}";

            // Show separate page indicator for multi-page artworks
            if (PageNumberBorder != null && PageNumberText != null)
            {
                if (hasMultiplePages)
                {
                    PageNumberBorder.IsVisible = true;
                    PageNumberText.Text = $"Pg {_currentPageIndex + 1}/{currentArtwork.PageCount}";
                }
                else
                {
                    PageNumberBorder.IsVisible = false;
                }
            }

            // Show/hide left/right navigation overlays based on PAGE navigation only
            // These overlays ONLY navigate pages within the current artwork
            if (LeftNavOverlay != null && RightNavOverlay != null)
            {
                // Only show overlays if current artwork has multiple pages
                if (hasMultiplePages)
                {
                    LeftNavOverlay.IsVisible = _currentPageIndex > 0;
                    RightNavOverlay.IsVisible = _currentPageIndex < currentArtwork.PageCount - 1;
                }
                else
                {
                    LeftNavOverlay.IsVisible = false;
                    RightNavOverlay.IsVisible = false;
                }
            }

            // TOP BUTTONS: Only enabled for submission navigation (not pages)
            PrevImageButton.IsEnabled = _currentImageIndex > 0;
            NextImageButton.IsEnabled = _currentImageIndex < count - 1;
        }
        else
        {
            if (SubmissionIndicator != null)
                SubmissionIndicator.Text = $"0 / {count}";
            if (PageNumberBorder != null) PageNumberBorder.IsVisible = false;
            if (LeftNavOverlay != null) LeftNavOverlay.IsVisible = false;
            if (RightNavOverlay != null) RightNavOverlay.IsVisible = false;
            PrevImageButton.IsEnabled = false;
            NextImageButton.IsEnabled = false;
        }

        UpdateAppliedStatus();
    }

    private ArtworkPreview? GetCurrentArtwork()
    {
        if (_currentImageIndex < _selectedArtworks.Count)
            return _selectedArtworks[_currentImageIndex];
        return null;
    }

    private async Task LoadCurrentImageAsync()
    {
        var artwork = GetCurrentArtwork();
        if (artwork == null)
        {
            NoPreviewText.IsVisible = true;
            PreviewImage.Source = null;
            return;
        }

        NoPreviewText.IsVisible = false;
        LoadingSpinner.IsVisible = true;

        try
        {
            // Dispose previous bitmap and invalidate the cached downscaled copy
            // so the next preview tick recomputes from the new source.
            _currentOriginalBitmap?.Dispose();
            _currentOriginalBitmap = null;
            _cachedPreviewSource?.Dispose();
            _cachedPreviewSource = null;
            _cachedPreviewSourceWidth = -1;

            // Load image from URL or file - ALWAYS try to get high-res Original first
            if (!string.IsNullOrEmpty(artwork.LocalPath) && File.Exists(artwork.LocalPath))
            {
                await using var stream = File.OpenRead(artwork.LocalPath);
                _currentOriginalBitmap = SkiaSharp.SKBitmap.Decode(stream);
            }
            else if (_imageLoader != null)
            {
                string? imageUrl = null;
                
                // ALWAYS try to fetch pages to get Original high-res URL first
                if (_pixivClient != null && !string.IsNullOrEmpty(artwork.ArtworkId))
                {
                    try
                    {
                        var pages = await _pixivClient.GetArtworkPagesAsync(artwork.ArtworkId);
                        if (pages != null && pages.Count > _currentPageIndex)
                        {
                            var urls = pages[_currentPageIndex].Urls;
                            // Prioritize Original URL for high resolution
                            imageUrl = urls.Original ?? urls.Regular ?? urls.Small;
                        }
                    }
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"Failed to fetch high-res pages: {ex.Message}");
                    }
                }

                // Only use ThumbnailUrl as absolute last resort
                if (string.IsNullOrEmpty(imageUrl))
                {
                    imageUrl = artwork.ThumbnailUrl ?? artwork.ImageUrl;
                    Debug.WriteLine($"Warning: Using thumbnail URL (low quality): {imageUrl}");
                }

                if (!string.IsNullOrEmpty(imageUrl))
                {
                    var bytes = await _imageLoader.FetchBytesAsync(imageUrl);
                    if (bytes != null)
                        _currentOriginalBitmap = SkiaSharp.SKBitmap.Decode(bytes);
                }
            }

            // Apply current preset for preview (immediate, no debounce for navigation)
            await UpdatePreviewAsync(immediate: true);

            // Update ugoira play button visibility
            _isCurrentUgoira = artwork.IllustType == 2;
            _currentArtworkId = artwork.ArtworkId;
            if (PlayAnimationButton != null)
            {
                PlayAnimationButton.IsVisible = _isCurrentUgoira && UgoiraPlayer?.IsVisible != true;
            }

            // Show/hide ugoira format selection panel
            if (UgoiraFormatPanel != null)
            {
                UgoiraFormatPanel.IsVisible = _isCurrentUgoira;
            }
        }
        catch (Exception ex)
        {
            NoPreviewText.Text = $"Failed to load image: {ex.Message}";
            NoPreviewText.IsVisible = true;
            _isCurrentUgoira = false;
            if (PlayAnimationButton != null) PlayAnimationButton.IsVisible = false;
        }
        finally
        {
            LoadingSpinner.IsVisible = false;
        }
    }

    private string GetPageUrl(ArtworkPreview artwork, int pageIndex)
    {
        // Return appropriate URL based on page index
        if (artwork.PageCount > 1 && artwork.PageUrls != null && pageIndex < artwork.PageUrls.Count)
        {
            return artwork.PageUrls[pageIndex];
        }
        return artwork.ThumbnailUrl ?? artwork.ImageUrl ?? "";
    }

    private bool _hasInitialPreview = false;

    // Cached downscaled copy of the active source bitmap so we don't re-resize
    // a 4K image on every pan/adjust tick. Recomputed when the source changes
    // or the preview-width target changes. Disposed in OnClosing.
    private SKBitmap? _cachedPreviewSource;
    private int _cachedPreviewSourceWidth = -1;

    private async Task UpdatePreviewAsync(bool immediate = false)
    {
        if (_currentOriginalBitmap == null || _imageResizeService == null)
            return;

        // Cancel previous preview generation
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var ct = _previewDebounceCts.Token;

        // Show spinner when loading new images or on first load
        var showSpinner = immediate || !_hasInitialPreview;

        _previewRenderInFlight = true;
        try
        {
            if (showSpinner)
                await Dispatcher.UIThread.InvokeAsync(() => LoadingSpinner.IsVisible = true);

            // Only debounce for live adjustments (slider changes), not for navigation
            if (!immediate)
            {
                await Task.Delay(20, ct);
                if (ct.IsCancellationRequested) return;
            }

            // Always cache the downscaled source at the user-selected preview
            // quality (default 800px). Importantly, we do NOT change the cache
            // width based on whether the user is dragging — flipping caches
            // mid-drag was forcing a 4K → 480 → 4K → 800 resize storm. The
            // pointer-move handler already throttles via _previewRenderInFlight.
            int maxWidth = 800;
            try { maxWidth = (int)PreviewQualitySlider.Value; } catch { }

            if (_cachedPreviewSource == null || _cachedPreviewSourceWidth != maxWidth)
            {
                _cachedPreviewSource?.Dispose();
                _cachedPreviewSource = null;
                var src = _currentOriginalBitmap;
                if (src == null || src.Width <= 0 || src.Height <= 0) return;
                _cachedPreviewSource = await Task.Run(() =>
                {
                    try
                    {
                        if (src.Width <= maxWidth) return src.Copy();
                        var scale = (float)maxWidth / src.Width;
                        var newH = (int)(src.Height * scale);
                        return src.Resize(new SKImageInfo(maxWidth, newH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    }
                    catch
                    {
                        return null;
                    }
                }, ct);
                _cachedPreviewSourceWidth = maxWidth;
                if (ct.IsCancellationRequested || _cachedPreviewSource == null) return;
            }

            // Generate preview from the cached downscaled source.
            var previewBitmap = await _imageResizeService.ProcessForPreviewAsync(
                _cachedPreviewSource!, _currentPreset, ct, maxWidth);
            if (previewBitmap == null || ct.IsCancellationRequested) return;

            // Direct pixel copy to Avalonia WriteableBitmap (no encoding overhead)
            var avaloniaBitmap = ImageEditorWindow.SkiaToAvalonia(previewBitmap);
            previewBitmap.Dispose();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PreviewImage.Source = avaloniaBitmap;
                _hasInitialPreview = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Expected, ignore
        }
        catch (Exception ex)
        {
            _dialogService?.ShowMessageAsync("Preview Error", $"Failed to generate preview: {ex.Message}");
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

            // If the user kept dragging while we were rendering, the latest
            // crop offset may not be reflected on screen yet. Trigger one
            // catch-up render so the preview lands on the current state.
            if (_isDraggingPreview)
            {
                _ = UpdatePreviewAsync();
            }
        }
    }

    // Event Handlers

    private void OnPresetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is PresetViewModel preset)
        {
            SelectPreset(preset);
        }
    }

    private void OnPresetRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.DataContext is PresetViewModel preset)
        {
            SelectPreset(preset);
        }
    }

    private void SelectPreset(PresetViewModel preset)
    {
        // Deselect all
        foreach (var p in _presets) p.IsSelected = false;

        // Select clicked
        preset.IsSelected = true;
        _currentPreset = preset.Preset;

        // Reset crop pan offset when switching presets so each preset starts
        // centred. Users can drag the preview to reposition again.
        _currentPreset.ResizeSettings.CropOffsetX = 0;
        _currentPreset.ResizeSettings.CropOffsetY = 0;

        // Update UI
        UpdatePresetInfo();

        // Update ugoira options from preset
        if (UgoiraSaveFramesCheck != null)
            UgoiraSaveFramesCheck.IsChecked = _currentPreset.SaveUgoiraFrames;
        if (UgoiraFramesOnlyCheck != null)
            UgoiraFramesOnlyCheck.IsChecked = _currentPreset.UgoiraFramesOnly;

        _ = LoadCurrentImageAsync();

        // Refresh list to update all radio buttons
        PresetList.ItemsSource = null;
        PresetList.ItemsSource = _presets;

        // Enable/disable delete button
        DeletePresetButton.IsEnabled = !preset.IsBuiltIn;
    }

    private void OnPresetSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var searchText = PresetSearchBox.Text?.ToLowerInvariant() ?? "";
        var filtered = _presets.Where(p =>
            p.Preset.Name.ToLowerInvariant().Contains(searchText) ||
            p.Preset.DevicePreset.ToString().ToLowerInvariant().Contains(searchText)
        ).ToList();

        PresetList.ItemsSource = filtered;
    }

    private bool _updatingOrientation = false;

    private void OnOrientationChanged(object? sender, RoutedEventArgs e)
    {
        // Guard against double-fire: when one toggle changes, the other auto-toggles too,
        // which would trigger this handler twice. Process only the user's actual click.
        if (_updatingOrientation) return;
        _updatingOrientation = true;
        try
        {
            // Determine which toggle the user clicked (the one that just got checked)
            bool isPortrait;
            if (sender == PortraitToggle && PortraitToggle.IsChecked == true)
            {
                isPortrait = true;
                LandscapeToggle.IsChecked = false;
            }
            else if (sender == LandscapeToggle && LandscapeToggle.IsChecked == true)
            {
                isPortrait = false;
                PortraitToggle.IsChecked = false;
            }
            else
            {
                // User unchecked the active one - re-check it (one must be active)
                if (sender is ToggleButton tb) tb.IsChecked = true;
                return;
            }

            _currentPreset.ResizeSettings.IsPortrait = isPortrait;

            // Update dimensions if using a device preset
            if (_currentPreset.DevicePreset != DevicePreset.Original &&
                _currentPreset.DevicePreset != DevicePreset.Custom)
            {
                var (w, h) = ResizeSettings.GetPresetDimensions(_currentPreset.DevicePreset, isPortrait);
                _currentPreset.ResizeSettings.TargetWidth = w;
                _currentPreset.ResizeSettings.TargetHeight = h;
            }

            _ = UpdatePreviewAsync();
        }
        finally
        {
            _updatingOrientation = false;
        }
    }

    private async void OnCustomEditClick(object? sender, RoutedEventArgs e)
    {
        // Open ImageEditor with ALL selected artworks, each with their pages loaded.
        // This allows applying the same preset across multiple submissions.
        if (_currentOriginalBitmap == null || _imageResizeService == null) return;

        var artworks = GetSelectedArtworksWithPages();
        if (artworks.Count == 0) return;

        // Show loading indicator
        LoadingSpinner.IsVisible = true;
        CustomPresetButton.IsEnabled = false;

        ImageEditorWindow? editor = null;

        try
        {
            // Load all pages for all artworks (with throttling to avoid overwhelming the network)
            var editableArtworks = await LoadAllArtworksForEditingAsync();

            if (editableArtworks.Count == 0 || (editableArtworks.Count == 1 && editableArtworks[0].PageCount == 0))
            {
                // Fallback: Load current artwork at high resolution
                var currentArtwork = GetCurrentArtwork();
                if (currentArtwork != null)
                {
                    var highResBitmap = await LoadHighResBitmapAsync(currentArtwork.ArtworkId);
                    if (highResBitmap != null)
                        editor = new ImageEditorWindow(_imageResizeService, highResBitmap, _currentPreset);
                    else
                        editor = new ImageEditorWindow(_imageResizeService, _currentOriginalBitmap, _currentPreset);
                }
                else
                {
                    editor = new ImageEditorWindow(_imageResizeService, _currentOriginalBitmap, _currentPreset);
                }
            }
            else if (editableArtworks.Count == 1 && editableArtworks[0].PageCount == 1 && editableArtworks[0].IllustType != 2)
            {
                // Single artwork, single page, NOT ugoira - use simple constructor
                editor = new ImageEditorWindow(_imageResizeService, editableArtworks[0].Pages[0], _currentPreset);
            }
            else
            {
                // Multi-artwork or multi-page - use multi-artwork constructor
                // Check if any artwork is ugoira - if so, force show the ugoira mode dialog
                var hasUgoira = editableArtworks.Any(a => a.IllustType == 2);

                editor = new ImageEditorWindow(
                    _imageResizeService,
                    editableArtworks,
                    initialArtworkIndex: _currentImageIndex,
                    initialPageIndex: _currentPageIndex,
                    initialPreset: _currentPreset,
                    forceShowUgoiraDialog: hasUgoira);
            }

            // Check if this window is still open and valid before showing dialog
            if (this.IsVisible && this.PlatformImpl != null)
            {
                var result = await editor.ShowDialog<ImageEditPreset?>(this);

                if (result != null)
                {
                    _currentPreset = result;
                    UpdatePresetInfo();
                    _ = UpdatePreviewAsync();
                }
            }
            else
            {
                // Window was closed, show as standalone
                editor.Show();
            }
        }
        catch (Exception ex)
        {
            // Log error and show message to user
            System.Diagnostics.Debug.WriteLine($"Error opening ImageEditor: {ex.Message}");
        }
        finally
        {
            LoadingSpinner.IsVisible = false;
            CustomPresetButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Loads all pages for all selected artworks in parallel with throttling.
    /// Returns a list of EditableArtwork objects ready for the ImageEditor.
    /// </summary>
    private async Task<List<EditableArtwork>> LoadAllArtworksForEditingAsync(List<ArtworkPreview>? artworks = null)
    {
        var results = new List<EditableArtwork>();
        var selectedArtworks = artworks ?? GetSelectedArtworksWithPages();
        if (selectedArtworks.Count == 0) return results;

        var client = _pixivClient;
        var loader = _imageLoader;
        if (client == null || loader == null) return results;

        // Semaphore to limit concurrent image loading (max 4 at a time)
        var semaphore = new SemaphoreSlim(4, 4);
        var artworkTasks = selectedArtworks.Select(async artwork =>
        {
            var editable = new EditableArtwork
            {
                ArtworkId = artwork.ArtworkId,
                PageCount = artwork.PageCount,
                IllustType = artwork.IllustType
            };

            try
            {
                // Always fetch pages to get the Original URL for high resolution
                var pages = await client.GetArtworkPagesAsync(artwork.ArtworkId);
                var pageTasks = pages.Select(async (p, idx) =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Use Original URL for full resolution, fallback to Regular or Small
                        var url = p.Urls?.Original ?? p.Urls?.Regular ?? p.Urls?.Small;
                        if (string.IsNullOrEmpty(url)) return (idx, (SKBitmap?)null);

                        var bytes = await loader.FetchBytesAsync(url);
                        if (bytes == null) return (idx, (SKBitmap?)null);

                        return (idx, SKBitmap.Decode(bytes));
                    }
                    finally { semaphore.Release(); }
                }).ToList();

                var pageResults = await Task.WhenAll(pageTasks);
                foreach (var (_, bmp) in pageResults.OrderBy(r => r.idx))
                    if (bmp != null) editable.Pages.Add(bmp);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load artwork {artwork.ArtworkId}: {ex.Message}");
            }

            return editable;
        }).ToList();

        var loadedArtworks = await Task.WhenAll(artworkTasks);
        results.AddRange(loadedArtworks.Where(a => a.PageCount > 0));
        return results;
    }

    /// <summary>
    /// Gets all selected artworks as a list.
    /// </summary>
    private List<ArtworkPreview> GetSelectedArtworksWithPages()
    {
        var result = new List<ArtworkPreview>();
        foreach (var vm in _selectedArtworks)
        {
            if (!string.IsNullOrEmpty(vm.ArtworkId))
                result.Add(vm);
        }
        return result;
    }

    private async void OnPrevImageClick(object? sender, RoutedEventArgs e)
    {
        // TOP BUTTONS: Navigate between submissions only (NOT pages within submission)
        if (_currentImageIndex > 0)
        {
            // Remember current page for this artwork
            _rememberedPageIndex[_currentImageIndex] = _currentPageIndex;
            _currentImageIndex--;
            // Restore remembered page or start at 0
            _currentPageIndex = _rememberedPageIndex.GetValueOrDefault(_currentImageIndex, 0);
            // Reset preview flag so spinner shows for new submission
            _hasInitialPreview = false;
            await LoadCurrentImageAsync();
            UpdateSelectedCount();
        }
    }

    private async void OnNextImageClick(object? sender, RoutedEventArgs e)
    {
        // TOP BUTTONS: Navigate between submissions only (NOT pages within submission)
        if (_currentImageIndex < _selectedArtworks.Count - 1)
        {
            // Remember current page for this artwork
            _rememberedPageIndex[_currentImageIndex] = _currentPageIndex;
            _currentImageIndex++;
            // Restore remembered page or start at 0
            _currentPageIndex = _rememberedPageIndex.GetValueOrDefault(_currentImageIndex, 0);
            // Reset preview flag so spinner shows for new submission
            _hasInitialPreview = false;
            await LoadCurrentImageAsync();
            UpdateSelectedCount();
        }
    }

    /// <summary>
    /// Loads a high-resolution bitmap for the specified artwork ID.
    /// Fetches the original resolution image from Pixiv.
    /// </summary>
    private async Task<SKBitmap?> LoadHighResBitmapAsync(string artworkId)
    {
        if (_pixivClient == null || _imageLoader == null) return null;
        try
        {
            var pages = await _pixivClient.GetArtworkPagesAsync(artworkId);
            if (pages == null || pages.Count == 0) return null;

            var url = pages[0].Urls?.Original ?? pages[0].Urls?.Regular ?? pages[0].Urls?.Small;
            if (string.IsNullOrEmpty(url)) return null;

            var bytes = await _imageLoader.FetchBytesAsync(url);
            if (bytes == null) return null;

            return SKBitmap.Decode(bytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load high-res bitmap: {ex.Message}");
            return null;
        }
    }

    // Click handlers for the on-image navigation overlays
    // These ONLY navigate pages within the current artwork, not other artworks
    private async void OnLeftNavClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        // Only navigate pages within current artwork
        var artwork = GetCurrentArtwork();
        if (artwork == null) return;
        if (artwork.PageCount > 1 && _currentPageIndex > 0)
        {
            _currentPageIndex--;
            StopUgoiraPlayback();
            await LoadCurrentImageAsync();
            UpdateSelectedCount();
        }
    }

    private void StopUgoiraPlayback()
    {
        if (UgoiraPlayer != null)
        {
            UgoiraPlayer.IsVisible = false;
            UgoiraPlayer.IsPlaying = false;
            UgoiraPlayer.SourcePath = null;
        }
        if (PreviewImage != null) PreviewImage.IsVisible = true;
    }

    private async void OnRightNavClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        // Only navigate pages within current artwork
        var artwork = GetCurrentArtwork();
        if (artwork == null) return;
        if (artwork.PageCount > 1 && _currentPageIndex < artwork.PageCount - 1)
        {
            _currentPageIndex++;
            StopUgoiraPlayback();
            await LoadCurrentImageAsync();
            UpdateSelectedCount();
        }
    }

    /// <summary>
    /// Play/Pause the ugoira animation when play button is clicked.
    /// </summary>
    private async void OnPlayAnimationClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (!_isCurrentUgoira || string.IsNullOrEmpty(_currentArtworkId)) return;

        try
        {
            var ugoiraService = AppServices.Get<UgoiraService>();
            var previewPath = await ugoiraService.GetOrCreatePreviewAsync(_currentArtworkId);
            if (previewPath == null) return;

            // Switch from static preview to animation player
            if (PreviewImage != null) PreviewImage.IsVisible = false;
            if (UgoiraPlayer != null)
            {
                UgoiraPlayer.SourcePath = previewPath;
                UgoiraPlayer.IsVisible = true;
                UgoiraPlayer.IsPlaying = true;
            }
            if (PlayAnimationButton != null) PlayAnimationButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to play animation: {ex.Message}");
        }
    }

    private void OnPreviewQualityChanged(object? sender, RoutedEventArgs e)
    {
        PreviewQualityValue.Text = $"{(int)PreviewQualitySlider.Value}px";
        // Re-generate preview with new quality
        _ = UpdatePreviewAsync();
    }

    private void OnShowCropChanged(object? sender, RoutedEventArgs e)
    {
        CropOverlayCanvas.IsVisible = ShowCropOverlayCheck.IsChecked == true;
    }

    private async void OnResetPreviewClick(object? sender, RoutedEventArgs e)
    {
        _currentPreset.Adjustments.Reset();
        // Re-center the crop window when the user hits Reset.
        _currentPreset.ResizeSettings.CropOffsetX = 0;
        _currentPreset.ResizeSettings.CropOffsetY = 0;
        await UpdatePreviewAsync();
    }

    private void OnCustomFolderChanged(object? sender, RoutedEventArgs e)
    {
        var isChecked = UseCustomFolderCheck.IsChecked == true;
        CustomFolderTextBox.IsVisible = isChecked;
        BrowseFolderButton.IsVisible = isChecked;

        if (!isChecked)
        {
            _currentPreset.CustomOutputFolder = null;
        }
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = this.StorageProvider;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Output Folder",
                AllowMultiple = false
            });

            var result = folders?.FirstOrDefault()?.Path.LocalPath;
            if (!string.IsNullOrEmpty(result))
            {
                CustomFolderTextBox.Text = result;
                _currentPreset.CustomOutputFolder = result;
            }
        }
        catch (Exception)
        {
            // Storage provider not available
        }
    }

    private async void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        // Show save preset dialog
        var saveDialog = new SavePresetDialog();
        var saved = await saveDialog.ShowDialog<bool>(this);

        if (saved && !string.IsNullOrEmpty(saveDialog.PresetName))
        {
            _currentPreset.Name = saveDialog.PresetName;
            _currentPreset.Id = Guid.NewGuid();
            _currentPreset.IsBuiltIn = false;

            var newPreset = new PresetViewModel { Preset = _currentPreset, IsBuiltIn = false, IsSelected = true };
            _presets.Add(newPreset);

            // Deselect others
            foreach (var p in _presets) p.IsSelected = false;
            newPreset.IsSelected = true;

            PresetList.ItemsSource = null;
            PresetList.ItemsSource = _presets;

            CurrentPresetName.Text = saveDialog.PresetName;
        }
    }

    private async void OnDeletePresetClick(object? sender, RoutedEventArgs e)
    {
        var selected = _presets.FirstOrDefault(p => p.IsSelected);
        if (selected == null || selected.IsBuiltIn) return;

        var confirm = await _dialogService!.ShowConfirmationAsync(
            "Delete Preset",
            $"Are you sure you want to delete the preset '{selected.Preset.Name}'?");

        if (confirm)
        {
            _presets.Remove(selected);
            PresetList.ItemsSource = null;
            PresetList.ItemsSource = _presets;
        }
    }

    private void OnDownloadScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Show/hide the pages textbox only when "Selected pages" is chosen
        if (SelectedPagesTextBox != null && DownloadScopeComboBox != null)
        {
            SelectedPagesTextBox.IsVisible = DownloadScopeComboBox.SelectedIndex == 3;
        }
    }

    private void InitializeDownloadArtworksSelection()
    {
        if (DownloadArtworksComboBox == null) return;

        DownloadArtworksComboBox.Items.Clear();
        DownloadArtworksComboBox.Items.Add(new ComboBoxItem { Content = "Current artwork only" });

        if (_selectedArtworks.Count > 1)
        {
            DownloadArtworksComboBox.Items.Add(new ComboBoxItem { Content = "All selected artworks" });
            DownloadArtworksComboBox.Items.Add(new ComboBoxItem { Content = "Selected artworks" });
        }

        DownloadArtworksComboBox.SelectedIndex = _selectedArtworks.Count > 1 ? 1 : 0;
        OnDownloadArtworksChanged(DownloadArtworksComboBox, new SelectionChangedEventArgs(null, Array.Empty<object>(), Array.Empty<object>()));
    }

    private void OnDownloadArtworksChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DownloadArtworksComboBox == null || DownloadArtworksTextBox == null) return;

        var selected = DownloadArtworksComboBox.SelectedItem as ComboBoxItem;
        var text = selected?.Content?.ToString() ?? "";
        DownloadArtworksTextBox.IsVisible = text == "Selected artworks";
    }

    /// <summary>
    /// Parses simple artwork selection text (e.g., "1,3,5" or "1-3") into 0-based artwork indices.
    /// Stops at the first colon if advanced artwork:page syntax is present.
    /// </summary>
    private List<int> ParseSelectedArtworks(string input, int totalArtworks)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(input) || totalArtworks <= 0) return result;

        var simplePart = input.Split(':', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        simplePart = System.Text.RegularExpressions.Regex.Replace(simplePart, @"(?i)\bartwork\s*", "").Trim();

        var parts = simplePart.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Contains('-'))
            {
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0].Trim(), out var start) &&
                    int.TryParse(rangeParts[1].Trim(), out var end))
                {
                    start = Math.Max(1, Math.Min(start, totalArtworks));
                    end = Math.Max(1, Math.Min(end, totalArtworks));
                    for (var i = start; i <= end; i++)
                        result.Add(i - 1);
                }
            }
            else if (int.TryParse(trimmed, out var num))
            {
                num = Math.Max(1, Math.Min(num, totalArtworks));
                result.Add(num - 1);
            }
        }

        return result.Distinct().OrderBy(i => i).ToList();
    }

    /// <summary>
    /// Parses advanced artwork:page selection syntax.
    /// Examples: "1-3:1-2,5" or "artwork 1-3: page 1-2,5; artwork 6: page 3".
    /// Returns a dictionary of artwork index -> page indices (null means all pages).
    /// </summary>
    private Dictionary<int, List<int>?> ParseArtworkPageSelections(string input, int totalArtworks)
    {
        var result = new Dictionary<int, List<int>?>();
        if (string.IsNullOrWhiteSpace(input) || totalArtworks <= 0 || !input.Contains(':')) return result;

        var groups = input.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var group in groups)
        {
            var trimmedGroup = group.Trim();
            if (!trimmedGroup.Contains(':')) continue;

            var parts = trimmedGroup.Split(':', 2);
            var artworkPart = System.Text.RegularExpressions.Regex.Replace(parts[0].Trim(), @"(?i)\bartwork\s*", "").Trim();
            var pagePart = System.Text.RegularExpressions.Regex.Replace(parts[1].Trim(), @"(?i)\bpage\s*", "").Trim();

            var artworkIndices = ParseSelectedArtworks(artworkPart, totalArtworks);
            var pageIndices = ParseSelectedPages(pagePart, totalArtworks);

            foreach (var artIndex in artworkIndices)
            {
                var maxPages = artIndex < _selectedArtworks.Count ? _selectedArtworks[artIndex].PageCount : 1;
                var clampedPages = pageIndices.Where(p => p >= 0 && p < maxPages).ToList();
                result[artIndex] = clampedPages.Count > 0 ? clampedPages : null;
            }
        }

        return result;
    }

    /// <summary>
    /// Parses the selected pages text (e.g., "1,3,5" or "1-3,6") into a list of 0-based page indices.
    /// </summary>
    private List<int> ParseSelectedPages(string input, int totalPages)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(input) || totalPages <= 0) return result;

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Handle range (e.g., "1-3")
            if (trimmed.Contains('-'))
            {
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0].Trim(), out var start) &&
                    int.TryParse(rangeParts[1].Trim(), out var end))
                {
                    // Convert to 0-based and clamp to valid range
                    start = Math.Max(1, Math.Min(start, totalPages));
                    end = Math.Max(1, Math.Min(end, totalPages));
                    for (var i = start; i <= end; i++)
                        result.Add(i - 1); // Convert to 0-based
                }
            }
            // Handle single number
            else if (int.TryParse(trimmed, out var pageNum))
            {
                // Clamp to valid range and convert to 0-based
                pageNum = Math.Max(1, Math.Min(pageNum, totalPages));
                result.Add(pageNum - 1);
            }
        }

        // Remove duplicates and sort
        return result.Distinct().OrderBy(p => p).ToList();
    }

    // Batch Mode functionality
    private void OnBatchModeChanged(object? sender, RoutedEventArgs e)
    {
        var isBatchMode = EnableBatchModeCheck?.IsChecked == true;
        if (BatchSubmissionsBorder != null)
            BatchSubmissionsBorder.IsVisible = isBatchMode;

        if (isBatchMode)
            PopulateBatchSubmissionsList();
    }

    /// <summary>
    /// Populates the batch mode list with all selected submissions and page selection inputs.
    /// </summary>
    private void PopulateBatchSubmissionsList()
    {
        if (BatchSubmissionsStack == null) return;

        // Clear existing items
        BatchSubmissionsStack.Children.Clear();

        // Add entry for each submission
        for (int i = 0; i < _selectedArtworks.Count; i++)
        {
            var artwork = _selectedArtworks[i];
            var hasMultiplePages = artwork.PageCount > 1;

            // Main container with left accent border
            var container = new Border
            {
                Background = global::Avalonia.Media.Brushes.Transparent,
                BorderBrush = hasMultiplePages ? global::Avalonia.Media.Brushes.Orange : global::Avalonia.Media.Brushes.Gray,
                BorderThickness = new global::Avalonia.Thickness(3, 0, 0, 0),
                Padding = new global::Avalonia.Thickness(8, 4, 0, 4)
            };

            var itemPanel = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Vertical,
                Spacing = 4
            };

            // Title row with index badge
            var titleRow = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };

            // Index badge
            var indexBadge = new Border
            {
                Background = global::Avalonia.Media.Brushes.Gray,
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(4, 2, 4, 2),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            indexBadge.Child = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10,
                Foreground = global::Avalonia.Media.Brushes.White,
                FontWeight = global::Avalonia.Media.FontWeight.Bold
            };
            titleRow.Children.Add(indexBadge);

            // Title text
            var titleText = new TextBlock
            {
                Text = string.IsNullOrEmpty(artwork.Title) ? "Untitled" : artwork.Title,
                FontSize = 12,
                TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis,
                MaxWidth = 220,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            titleRow.Children.Add(titleText);

            // Page count badge
            var pageBadge = new Border
            {
                Background = hasMultiplePages
                    ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#3B82F6"))
                    : global::Avalonia.Media.Brushes.Gray,
                CornerRadius = new global::Avalonia.CornerRadius(4),
                Padding = new global::Avalonia.Thickness(4, 2, 4, 2),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            pageBadge.Child = new TextBlock
            {
                Text = hasMultiplePages ? $"{artwork.PageCount} pages" : "1 page",
                FontSize = 9,
                Foreground = global::Avalonia.Media.Brushes.White
            };
            titleRow.Children.Add(pageBadge);

            itemPanel.Children.Add(titleRow);

            // Page selection textbox (only for multi-page)
            if (hasMultiplePages)
            {
                var pagesRow = new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new global::Avalonia.Thickness(0, 2, 0, 0)
                };

                pagesRow.Children.Add(new TextBlock
                {
                    Text = "Pages:",
                    FontSize = 11,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                });

                var pagesTextBox = new TextBox
                {
                    PlaceholderText = "all",
                    Text = "all", // Default to all pages
                    FontSize = 11,
                    Width = 140,
                    Height = 26,
                    Tag = i // Store submission index
                };
                pagesTextBox.LostFocus += OnBatchPagesTextChanged;
                pagesTextBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        OnBatchPagesTextChanged(s, new RoutedEventArgs());
                    }
                };
                pagesRow.Children.Add(pagesTextBox);

                // Hint text
                pagesRow.Children.Add(new TextBlock
                {
                    Text = "e.g. 1,3,5-6",
                    FontSize = 9,
                    Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#888888")),
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                });

                itemPanel.Children.Add(pagesRow);
            }
            else
            {
                // Single page - show note
                itemPanel.Children.Add(new TextBlock
                {
                    Text = "Single page submission",
                    FontSize = 10,
                    Foreground = global::Avalonia.Media.Brushes.Gray,
                    FontStyle = global::Avalonia.Media.FontStyle.Italic
                });
            }

            container.Child = itemPanel;
            BatchSubmissionsStack.Children.Add(container);
        }
    }

    /// <summary>
    /// Stores per-submission page selections when text changes in batch mode.
    /// </summary>
    private void OnBatchPagesTextChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (textBox.Tag is not int submissionIndex) return;
        if (submissionIndex >= _selectedArtworks.Count) return;

        var artwork = _selectedArtworks[submissionIndex];
        var text = textBox.Text?.Trim().ToLower() ?? "all";

        // Store the page selection in the dictionary keyed by submission index
        if (text == "all" || string.IsNullOrEmpty(text))
        {
            _batchPageSelections[submissionIndex] = null; // All pages
        }
        else
        {
            _batchPageSelections[submissionIndex] = ParseSelectedPages(text, artwork.PageCount);
        }
    }

    /// <summary>
    /// Gets page selections for all submissions in batch mode.
    /// Returns a dictionary: submission index -> list of page indices (null = all pages).
    /// </summary>
    private Dictionary<int, List<int>?> GetBatchPageSelections()
    {
        var result = new Dictionary<int, List<int>?>();

        for (int i = 0; i < _selectedArtworks.Count; i++)
        {
            result[i] = _batchPageSelections.TryGetValue(i, out var sel) ? sel : null;
        }

        return result;
    }

    private void OnApplyToPageClick(object? sender, RoutedEventArgs e)
    {
        var artwork = GetCurrentArtwork();
        if (artwork == null) return;

        var clone = CloneCurrentPreset();
        _perPagePresets[(_currentImageIndex, _currentPageIndex)] = clone;
        UpdateAppliedStatus();
    }

    private void OnApplyToSelectedPagesClick(object? sender, RoutedEventArgs e)
    {
        var artwork = GetCurrentArtwork();
        if (artwork == null) return;

        PopulatePagePicker(artwork.PageCount);
        (ApplyToSelectedPagesButton?.Flyout as Flyout)?.ShowAt(ApplyToSelectedPagesButton!);
    }

    private bool _isUpdatingPagePickerText;

    private void PopulatePagePicker(int pageCount)
    {
        if (PagePickerPanel == null) return;

        PagePickerPanel.Children.Clear();
        if (pageCount <= 1) return;

        var existingSelection = ParseSelectedPages(SelectedPagesTextBox?.Text ?? "", pageCount);

        if (PagePickerTextBox != null)
        {
            _isUpdatingPagePickerText = true;
            PagePickerTextBox.Text = existingSelection.Count > 0
                ? string.Join(",", existingSelection.Select(p => p + 1))
                : "";
            _isUpdatingPagePickerText = false;
        }

        for (int i = 0; i < pageCount; i++)
        {
            var checkBox = new CheckBox
            {
                Content = $"{i + 1}",
                FontSize = 12,
                Margin = new global::Avalonia.Thickness(4, 2, 4, 2),
                IsChecked = existingSelection.Contains(i),
                Tag = i
            };
            checkBox.IsCheckedChanged += OnPagePickerCheckChanged;
            PagePickerPanel.Children.Add(checkBox);
        }
    }

    private void OnPagePickerTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingPagePickerText || PagePickerTextBox == null || PagePickerPanel == null) return;

        var artwork = GetCurrentArtwork();
        if (artwork == null) return;

        var parsed = ParseSelectedPages(PagePickerTextBox.Text ?? string.Empty, artwork.PageCount);
        _isUpdatingPagePickerText = true;
        foreach (var child in PagePickerPanel.Children)
        {
            if (child is CheckBox { Tag: int pageIndex } cb)
                cb.IsChecked = parsed.Contains(pageIndex);
        }
        _isUpdatingPagePickerText = false;
    }

    private void OnPagePickerCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingPagePickerText || PagePickerTextBox == null || PagePickerPanel == null) return;

        var selected = PagePickerPanel.Children
            .OfType<CheckBox>()
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (int)(cb.Tag ?? 0))
            .OrderBy(i => i)
            .Select(i => i + 1);

        _isUpdatingPagePickerText = true;
        PagePickerTextBox.Text = string.Join(",", selected);
        _isUpdatingPagePickerText = false;
    }

    private void OnPagePickerAllClick(object? sender, RoutedEventArgs e)
    {
        if (PagePickerPanel == null) return;
        foreach (var child in PagePickerPanel.Children)
        {
            if (child is CheckBox cb) cb.IsChecked = true;
        }
    }

    private void OnPagePickerNoneClick(object? sender, RoutedEventArgs e)
    {
        if (PagePickerPanel == null) return;
        foreach (var child in PagePickerPanel.Children)
        {
            if (child is CheckBox cb) cb.IsChecked = false;
        }
    }

    private void OnPagePickerInvertClick(object? sender, RoutedEventArgs e)
    {
        if (PagePickerPanel == null) return;
        foreach (var child in PagePickerPanel.Children)
        {
            if (child is CheckBox cb) cb.IsChecked = !(cb.IsChecked ?? false);
        }
    }

    private void OnPagePickerApplyClick(object? sender, RoutedEventArgs e)
    {
        var artwork = GetCurrentArtwork();
        if (artwork == null || PagePickerTextBox == null) return;

        var selectedPages = ParseSelectedPages(PagePickerTextBox.Text ?? string.Empty, artwork.PageCount);
        if (selectedPages.Count == 0)
        {
            _dialogService?.ShowMessageAsync("Apply to Selected Pages", "No pages selected.");
            return;
        }

        // Update the shared textbox so download scope also sees the selection
        if (SelectedPagesTextBox != null)
        {
            SelectedPagesTextBox.Text = string.Join(",", selectedPages.Select(p => p + 1));
        }

        var clone = CloneCurrentPreset();
        foreach (var p in selectedPages)
        {
            _perPagePresets[(_currentImageIndex, p)] = clone;
        }
        UpdateAppliedStatus();
        (ApplyToSelectedPagesButton?.Flyout as Flyout)?.Hide();
    }

    private void OnApplyToArtworkClick(object? sender, RoutedEventArgs e)
    {
        var artwork = GetCurrentArtwork();
        if (artwork == null) return;

        for (int p = 0; p < artwork.PageCount; p++)
        {
            _perPagePresets[(_currentImageIndex, p)] = CloneCurrentPreset();
        }
        UpdateAppliedStatus();
    }

    private void OnApplyToSelectedArtworksClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedArtworkApplyIndices.Count == 0)
        {
            _dialogService?.ShowMessageAsync("Apply to Selected Artworks", "No artworks are selected. Check at least one artwork in the list.");
            return;
        }

        foreach (var artIndex in _selectedArtworkApplyIndices)
        {
            if (artIndex < 0 || artIndex >= _selectedArtworks.Count) continue;
            var art = _selectedArtworks[artIndex];
            for (int p = 0; p < art.PageCount; p++)
            {
                _perPagePresets[(artIndex, p)] = CloneCurrentPreset();
            }
        }
        UpdateAppliedStatus();
    }

    private void OnApplyToAllClick(object? sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _selectedArtworks.Count; i++)
        {
            var art = _selectedArtworks[i];
            for (int p = 0; p < art.PageCount; p++)
            {
                _perPagePresets[(i, p)] = CloneCurrentPreset();
            }
        }
        UpdateAppliedStatus();
    }

    private void PopulateApplyToArtworksList()
    {
        if (ApplyToArtworksStack == null) return;

        ApplyToArtworksStack.Children.Clear();

        for (int i = 0; i < _selectedArtworks.Count; i++)
        {
            var artwork = _selectedArtworks[i];
            var checkBox = new CheckBox
            {
                Content = $"{(i + 1)}. {(string.IsNullOrEmpty(artwork.Title) ? "Untitled" : artwork.Title)}  ({artwork.PageCount} page{(artwork.PageCount != 1 ? "s" : "")})",
                FontSize = 11,
                IsChecked = _selectedArtworkApplyIndices.Contains(i),
                Tag = i
            };
            checkBox.IsCheckedChanged += (s, e) =>
            {
                if (s is CheckBox cb && cb.Tag is int idx)
                {
                    if (cb.IsChecked == true)
                        _selectedArtworkApplyIndices.Add(idx);
                    else
                        _selectedArtworkApplyIndices.Remove(idx);
                }
            };
            ApplyToArtworksStack.Children.Add(checkBox);
        }
    }

    private ImageEditPreset CloneCurrentPreset()
    {
        return new ImageEditPreset
        {
            Id = Guid.NewGuid(),
            Name = _currentPreset.Name,
            IsBuiltIn = false,
            DevicePreset = _currentPreset.DevicePreset,
            ResizeSettings = new ResizeSettings
            {
                Mode = _currentPreset.ResizeSettings.Mode,
                OutputFormat = _currentPreset.ResizeSettings.OutputFormat,
                JpegQuality = _currentPreset.ResizeSettings.JpegQuality,
                IsPortrait = _currentPreset.ResizeSettings.IsPortrait,
                CropOffsetX = _currentPreset.ResizeSettings.CropOffsetX,
                CropOffsetY = _currentPreset.ResizeSettings.CropOffsetY
            },
            Adjustments = new ImageAdjustments
            {
                Brightness = _currentPreset.Adjustments.Brightness,
                Contrast = _currentPreset.Adjustments.Contrast,
                Saturation = _currentPreset.Adjustments.Saturation,
                HueRotation = _currentPreset.Adjustments.HueRotation,
                Temperature = _currentPreset.Adjustments.Temperature,
                Tint = _currentPreset.Adjustments.Tint,
                Highlights = _currentPreset.Adjustments.Highlights,
                Shadows = _currentPreset.Adjustments.Shadows,
                Sharpness = _currentPreset.Adjustments.Sharpness,
                BlurRadius = _currentPreset.Adjustments.BlurRadius,
                VignetteIntensity = _currentPreset.Adjustments.VignetteIntensity,
                VignetteRadius = _currentPreset.Adjustments.VignetteRadius,
                ColorOverlayHex = _currentPreset.Adjustments.ColorOverlayHex,
                ColorOverlayOpacity = _currentPreset.Adjustments.ColorOverlayOpacity,
                Opacity = _currentPreset.Adjustments.Opacity
            },
            CropRegion = _currentPreset.CropRegion,
            UgoiraMode = _currentPreset.UgoiraMode,
            UgoiraFormats = _currentPreset.UgoiraFormats?.ToList(),
            SaveUgoiraFrames = _currentPreset.SaveUgoiraFrames,
            UgoiraFramesOnly = _currentPreset.UgoiraFramesOnly,
            SaveAsNew = SaveAsNewRadio.IsChecked == true,
            ApplyToAllPages = false,
            DownloadOnlyPagesWithPresets = false,
            SelectedPageIndices = null,
            AlsoDownloadUnprocessed = AlsoDownloadUnprocessedCheck?.IsChecked == true
        };
    }

    private void UpdateAppliedStatus()
    {
        if (AppliedPresetStatus == null) return;

        var appliedCount = _perPagePresets.Count;
        var totalPages = _selectedArtworks.Sum(a => a.PageCount);

        if (appliedCount > 0)
        {
            var currentHasPreset = _perPagePresets.ContainsKey((_currentImageIndex, _currentPageIndex));
            var statusParts = new List<string>();
            statusParts.Add($"{appliedCount}/{totalPages} pages have presets applied");
            if (currentHasPreset)
                statusParts.Add($"Current page: \"{_perPagePresets[(_currentImageIndex, _currentPageIndex)].Name}\"");
            AppliedPresetStatus.Text = string.Join(" | ", statusParts);
            AppliedPresetStatus.IsVisible = true;
        }
        else
        {
            AppliedPresetStatus.IsVisible = false;
        }
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        // Update preset with current options
        _currentPreset.SaveAsNew = SaveAsNewRadio.IsChecked == true;
        _currentPreset.AlsoDownloadUnprocessed = AlsoDownloadUnprocessedCheck?.IsChecked == true;

        var scopeIndex = DownloadScopeComboBox?.SelectedIndex ?? 0;

        // Handle download scope
        switch (scopeIndex)
        {
            case 0: // All pages
                _currentPreset.ApplyToAllPages = true;
                _currentPreset.DownloadOnlyPagesWithPresets = false;
                _currentPreset.SelectedPageIndices = null;
                break;
            case 1: // Pages with presets only
                _currentPreset.ApplyToAllPages = false;
                _currentPreset.DownloadOnlyPagesWithPresets = true;
                _currentPreset.SelectedPageIndices = null;
                break;
            case 2: // Current page only
                _currentPreset.ApplyToAllPages = false;
                _currentPreset.DownloadOnlyPagesWithPresets = false;
                _currentPreset.SelectedPageIndices = new List<int> { _currentPageIndex };
                break;
            case 3: // Selected pages
                _currentPreset.ApplyToAllPages = false;
                _currentPreset.DownloadOnlyPagesWithPresets = false;
                var currentArtwork = GetCurrentArtwork();
                if (currentArtwork != null && SelectedPagesTextBox != null)
                {
                    _currentPreset.SelectedPageIndices = ParseSelectedPages(
                        SelectedPagesTextBox.Text ?? string.Empty, currentArtwork.PageCount);
                }
                break;
            default:
                _currentPreset.ApplyToAllPages = true;
                _currentPreset.DownloadOnlyPagesWithPresets = false;
                _currentPreset.SelectedPageIndices = null;
                break;
        }

        // Handle artwork download selection
        var artworkModeText = (DownloadArtworksComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        switch (artworkModeText)
        {
            case "Current artwork only":
                _currentPreset.DownloadAllArtworks = false;
                _currentPreset.SelectedArtworkIndices = new List<int> { _currentImageIndex };
                _currentPreset.ArtworkPageSelections = null;
                break;
            case "All selected artworks":
                _currentPreset.DownloadAllArtworks = true;
                _currentPreset.SelectedArtworkIndices = null;
                _currentPreset.ArtworkPageSelections = null;
                break;
            case "Selected artworks":
                _currentPreset.DownloadAllArtworks = false;
                var artworkInput = DownloadArtworksTextBox?.Text ?? "";
                _currentPreset.SelectedArtworkIndices = ParseSelectedArtworks(artworkInput, _selectedArtworks.Count);
                _currentPreset.ArtworkPageSelections = artworkInput.Contains(':')
                    ? ParseArtworkPageSelections(artworkInput, _selectedArtworks.Count)
                    : null;
                break;
            default:
                _currentPreset.DownloadAllArtworks = true;
                _currentPreset.SelectedArtworkIndices = null;
                _currentPreset.ArtworkPageSelections = null;
                break;
        }

        // Capture custom folder from textbox (user may type path instead of browsing)
        if (UseCustomFolderCheck.IsChecked == true)
        {
            var folderText = CustomFolderTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(folderText))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(folderText);
                    _currentPreset.CustomOutputFolder = folderText;
                }
                catch
                {
                    _currentPreset.CustomOutputFolder = null;
                }
            }
        }
        else
        {
            _currentPreset.CustomOutputFolder = null;
        }

        var formatIndex = FormatComboBox.SelectedIndex;
        _currentPreset.ResizeSettings.OutputFormat = formatIndex switch
        {
            0 => ResizeOutputFormat.KeepOriginal,
            1 => ResizeOutputFormat.Jpeg,
            2 => ResizeOutputFormat.Jpeg,
            3 => ResizeOutputFormat.Png,
            4 => ResizeOutputFormat.Webp,
            _ => ResizeOutputFormat.KeepOriginal
        };

        if (_currentPreset.ResizeSettings.OutputFormat == ResizeOutputFormat.Jpeg)
        {
            _currentPreset.ResizeSettings.JpegQuality = formatIndex == 1 ? 95 : 75;
        }

        // Capture ugoira format selections if this is an ugoira artwork
        if (_isCurrentUgoira)
        {
            var formats = new List<UgoiraFormat>();
            if (UgoiraMp4Check?.IsChecked == true) formats.Add(UgoiraFormat.Mp4);
            if (UgoiraGifCheck?.IsChecked == true) formats.Add(UgoiraFormat.Gif);
            if (UgoiraWebmCheck?.IsChecked == true) formats.Add(UgoiraFormat.WebM);
            if (UgoiraWebpCheck?.IsChecked == true) formats.Add(UgoiraFormat.WebP);
            
            // Only save if at least one format is selected, otherwise use global settings
            if (formats.Count > 0)
            {
                _currentPreset.UgoiraFormats = formats;
            }
            
            // Capture save frames option
            _currentPreset.SaveUgoiraFrames = UgoiraSaveFramesCheck?.IsChecked == true;
            _currentPreset.UgoiraFramesOnly = UgoiraFramesOnlyCheck?.IsChecked == true;
        }

        SelectedPreset = _currentPreset;
        DownloadClicked = true;

        // If any per-page overrides were applied, attach them to the result preset.
        if (_perPagePresets.Count > 0)
        {
            _currentPreset.PerPagePresets = new Dictionary<(int ArtworkIndex, int PageIndex), ImageEditPreset>(_perPagePresets);
        }

        Close(_currentPreset);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        DownloadClicked = false;
        Close(null);
    }

    private void UpdatePresetInfo()
    {
        CurrentPresetName.Text = _currentPreset.Name;

        var info = new List<string>();
        if (_currentPreset.DevicePreset != DevicePreset.Original)
        {
            // Show device preset name + resolved target dimensions
            var (w, h) = ResizeSettings.GetPresetDimensions(
                _currentPreset.DevicePreset,
                _currentPreset.ResizeSettings.IsPortrait);
            if (w > 0 && h > 0)
            {
                info.Add($"Resize: {ResizeSettings.GetPresetDisplayName(_currentPreset.DevicePreset)} ({w}×{h})");
            }
            else
            {
                info.Add($"Resize: {ResizeSettings.GetPresetDisplayName(_currentPreset.DevicePreset)}");
            }
        }
        else if (_currentPreset.ResizeSettings.TargetWidth > 0 && _currentPreset.ResizeSettings.TargetHeight > 0)
        {
            info.Add($"Resize: {_currentPreset.ResizeSettings.TargetWidth}×{_currentPreset.ResizeSettings.TargetHeight}");
        }

        if (_currentPreset.Adjustments.HasAdjustments)
        {
            var adj = _currentPreset.Adjustments;
            var adjustments = new List<string>();
            if (adj.Brightness != 0) adjustments.Add($"Brightness {adj.Brightness:+#;-#;0}");
            if (adj.Contrast != 0) adjustments.Add($"Contrast {adj.Contrast:+#;-#;0}");
            if (adj.Saturation != 100) adjustments.Add($"Saturation {adj.Saturation}%");
            if (adj.HueRotation != 0) adjustments.Add($"Hue {adj.HueRotation}°");
            if (adj.Temperature != 0) adjustments.Add($"Temp {adj.Temperature:+#;-#;0}");
            if (adj.Sharpness > 0) adjustments.Add($"Sharpness {adj.Sharpness}");
            if (adj.VignetteIntensity > 0) adjustments.Add($"Vignette {adj.VignetteIntensity}%");

            if (adjustments.Count > 0)
            {
                info.Add($"Adjustments: {string.Join(", ", adjustments)}");
            }
        }

        CurrentPresetInfo.Text = info.Count > 0
            ? string.Join("  •  ", info)
            : "No resize or adjustments";
    }

    /// <summary>
    /// ViewModel for preset selection. Nested so the XAML
    /// (`dialogs:DownloadPresetWindow+PresetViewModel`) resolves correctly.
    /// </summary>
    public class PresetViewModel
    {
        public ImageEditPreset Preset { get; set; } = null!;
        public bool IsBuiltIn { get; set; }
        public bool IsSelected { get; set; }
    }
}

/// <summary>
/// Lightweight artwork descriptor used by the download preset / image editor flows.
/// Distinct from <see cref="Pikura.Core.Models.ArtworkPreview"/>.
/// </summary>
public class ArtworkPreview
{
    public string ArtworkId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? LocalPath { get; set; }
    public int PageCount { get; set; } = 1;
    public int IllustType { get; set; } // 0=illust, 1=manga, 2=ugoira (animated)
    public System.Collections.Generic.List<string> PageUrls { get; set; } = new();
}

/// <summary>
/// Represents an artwork with its pages loaded for editing.
/// </summary>
public class EditableArtwork
{
    public string ArtworkId { get; set; } = "";
    public string Title { get; set; } = "";
    public string UserName { get; set; } = "";
    public System.Collections.Generic.List<SkiaSharp.SKBitmap> Pages { get; set; } = new();
    public int PageCount { get; set; }
    public int IllustType { get; set; } // 2 = ugoira (animated)
}
