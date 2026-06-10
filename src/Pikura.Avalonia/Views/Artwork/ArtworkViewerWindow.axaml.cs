using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Models;

namespace Pikura.Avalonia.Views.Artwork;

public partial class ArtworkViewerWindow : Window
{
    // Current image position and scale (image top-left in canvas coords)
    private double _imgX, _imgY, _imgW, _imgH;   // logical size of image at current scale
    private double _scale = 1.0;
    private bool   _isPanning;
    private Point  _panStart;
    private double _panStartX, _panStartY;
    private DateTime _pressTime;

    public ArtworkViewerWindow() { InitializeComponent(); }

    public ArtworkViewerWindow(ArtworkPreview artwork, GalleryViewModel gallery)
    {
        InitializeComponent();
        var vm = new ArtworkViewerViewModel(artwork, gallery, this);
        DataContext = vm;

        // Zoom toolbar
        PopupZoomInBtn.Click  += (_, _) => ZoomAroundCenter(1.25);
        PopupZoomOutBtn.Click += (_, _) => ZoomAroundCenter(1.0 / 1.25);
        PopupZoomFitBtn.Click += (_, _) => FitToCanvas();

        // Fullscreen keys
        KeyDown += OnKeyDown;

        // Pointer events on canvas
        PopupCanvas.PointerWheelChanged += OnWheel;
        PopupCanvas.PointerPressed      += OnPressed;
        PopupCanvas.PointerMoved        += OnMoved;
        PopupCanvas.PointerReleased     += OnReleased;

        // When the canvas is measured we know its size — fit on first load
        PopupCanvas.SizeChanged += (_, _) =>
        {
            if (DataContext is ArtworkViewerViewModel v && v.CurrentPageBitmap != null)
                FitToCanvas();
        };

        // When bitmap changes: update res label and fit
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArtworkViewerViewModel.CurrentPageBitmap))
                Dispatcher.UIThread.Post(OnBitmapChanged);
        };

        _ = vm.LoadFirstPageAsync();
    }

    private void OnBitmapChanged()
    {
        if (DataContext is ArtworkViewerViewModel vm && vm.CurrentPageBitmap is Bitmap bmp)
        {
            if (PopupResLabel != null)
                PopupResLabel.Text = $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height}";
        }
        FitToCanvas();
    }

    // Fit image inside canvas, centered, maintaining aspect ratio
    private void FitToCanvas()
    {
        if (PopupCanvas == null || PopupImage == null) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;
        var bmp = vm.CurrentPageBitmap;
        if (bmp == null) return;

        var cw = PopupCanvas.Bounds.Width;
        var ch = PopupCanvas.Bounds.Height;
        if (cw <= 0 || ch <= 0) return;

        var bw = (double)bmp.PixelSize.Width;
        var bh = (double)bmp.PixelSize.Height;

        // Scale to fit, never upscale beyond 100%
        var fitScale = System.Math.Min(cw / bw, ch / bh);
        fitScale = System.Math.Min(fitScale, 1.0);

        _scale = fitScale;
        _imgW  = bw * _scale;
        _imgH  = bh * _scale;
        _imgX  = (cw - _imgW) / 2;
        _imgY  = (ch - _imgH) / 2;

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (PopupImage == null || PopupCanvas == null) return;

        // Size the image element to its logical display size
        PopupImage.Width  = _imgW;
        PopupImage.Height = _imgH;

        // Position on canvas
        Canvas.SetLeft(PopupImage, _imgX);
        Canvas.SetTop(PopupImage,  _imgY);

        if (PopupZoomLabel != null)
            PopupZoomLabel.Text = $"{_scale * 100:F0}%";
    }

    private void ZoomAroundPoint(double factor, double pivotX, double pivotY)
    {
        var newScale = System.Math.Clamp(_scale * factor, 0.02, 20.0);
        var actualFactor = newScale / _scale;
        // Pivot in canvas space: keep the point under cursor fixed
        _imgX = pivotX - (pivotX - _imgX) * actualFactor;
        _imgY = pivotY - (pivotY - _imgY) * actualFactor;
        _imgW *= actualFactor;
        _imgH *= actualFactor;
        _scale = newScale;
        ApplyTransform();
    }

    private void ZoomAroundCenter(double factor)
    {
        if (PopupCanvas == null) return;
        ZoomAroundPoint(factor, PopupCanvas.Bounds.Width / 2, PopupCanvas.Bounds.Height / 2);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (PopupCanvas == null) return;
        var factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        var pos = e.GetPosition(PopupCanvas);
        ZoomAroundPoint(factor, pos.X, pos.Y);
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PopupCanvas).Properties.IsLeftButtonPressed) return;
        _isPanning  = true;
        _pressTime  = DateTime.Now;
        _panStart   = e.GetPosition(PopupCanvas);
        _panStartX  = _imgX;
        _panStartY  = _imgY;
        e.Handled   = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || PopupCanvas == null) return;
        var pos = e.GetPosition(PopupCanvas);
        _imgX = _panStartX + (pos.X - _panStart.X);
        _imgY = _panStartY + (pos.Y - _panStart.Y);
        ApplyTransform();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;

        // In fullscreen, if it was a quick click (not a drag), navigate based on position
        if (WindowState == WindowState.FullScreen && DataContext is ArtworkViewerViewModel vm)
        {
            var clickDuration = DateTime.Now - _pressTime;
            if (clickDuration.TotalMilliseconds < 300) // Quick click, not a drag
            {
                var pos = e.GetPosition(PopupCanvas);
                var canvasWidth = PopupCanvas?.Bounds.Width ?? 0;
                
                // Click on left 30% = previous page, right 30% = next page
                if (pos.X < canvasWidth * 0.3 && vm.PrevPageCommand.CanExecute(null))
                {
                    vm.PrevPageCommand.Execute(null);
                }
                else if (pos.X > canvasWidth * 0.7 && vm.NextPageCommand.CanExecute(null))
                {
                    vm.NextPageCommand.Execute(null);
                }
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            // If already fullscreen, F11 closes the window (like ESC)
            if (WindowState == WindowState.FullScreen)
            {
                Close();
                e.Handled = true;
                return;
            }
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            // ESC in fullscreen closes the window entirely
            Close();
            e.Handled = true;
            return;
        }

        // Arrow keys for page navigation
        if (DataContext is ArtworkViewerViewModel vm)
        {
            if (e.Key == Key.Left)
            {
                if (vm.PrevPageCommand.CanExecute(null))
                    vm.PrevPageCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                if (vm.NextPageCommand.CanExecute(null))
                    vm.NextPageCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
    }

    private void OnFullscreenClicked(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        var goingFull = WindowState != WindowState.FullScreen;
        WindowState = goingFull ? WindowState.FullScreen : WindowState.Normal;

        // Hide ALL chrome in fullscreen - just show the image
        if (this.FindControl<Border>("BottomPanel") is { } bottom)
            bottom.IsVisible = !goingFull;
        if (this.FindControl<Border>("TopToolbar") is { } top)
            top.IsVisible = !goingFull;
        if (this.FindControl<StackPanel>("LoadingSpinner") is { } spinner)
            spinner.IsVisible = !goingFull && (DataContext as ArtworkViewerViewModel)?.IsLoadingPage == true;

        // In fullscreen: just the image on black background
        // Navigation: arrow keys or click left/right edges
        if (goingFull)
        {
            FitToCanvas();
        }
    }
}
