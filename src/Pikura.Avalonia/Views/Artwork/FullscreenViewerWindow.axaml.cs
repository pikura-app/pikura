using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Models;

namespace Pikura.Avalonia.Views.Artwork;

public partial class FullscreenViewerWindow : Window
{
    private double _imgX, _imgY, _imgW, _imgH;
    private double _scale = 1.0;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;
    private DateTime _pressTime;
    private readonly GalleryViewModel _gallery;

    public FullscreenViewerWindow()
    {
        InitializeComponent();
        _gallery = null!;
    }

    public FullscreenViewerWindow(ArtworkPreview artwork, GalleryViewModel gallery)
    {
        InitializeComponent();
        _gallery = gallery;
        KeyDown += OnKeyDown;
        Canvas.PointerWheelChanged += OnWheel;
        Canvas.PointerPressed += OnPressed;
        Canvas.PointerMoved += OnMoved;
        Canvas.PointerReleased += OnReleased;
        Canvas.SizeChanged += (_, _) =>
        {
            if (DataContext is ArtworkViewerViewModel cur && (cur.CurrentPageBitmap != null || cur.IsUgoira))
                FitToCanvas();
        };

        AttachViewModel(new ArtworkViewerViewModel(artwork, gallery, this));
    }

    private void AttachViewModel(ArtworkViewerViewModel vm)
    {
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArtworkViewerViewModel.CurrentPageBitmap) ||
                e.PropertyName == nameof(ArtworkViewerViewModel.UgoiraPreviewPath))
                Dispatcher.UIThread.Post(FitToCanvasWhenReady);
        };
        _ = vm.LoadFirstPageAsync();
    }

    private void FitToCanvasWhenReady()
    {
        if (Canvas == null) return;
        if (Canvas.Bounds.Width > 0 && Canvas.Bounds.Height > 0)
        {
            FitToCanvas();
            return;
        }
        // Canvas hasn't been laid out yet — retry after the next layout pass.
        void OnLayoutUpdated(object? s, EventArgs _)
        {
            Canvas.LayoutUpdated -= OnLayoutUpdated;
            FitToCanvas();
        }
        Canvas.LayoutUpdated += OnLayoutUpdated;
    }

    private void FitToCanvas()
    {
        if (Canvas == null) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;

        var cw = Canvas.Bounds.Width;
        var ch = Canvas.Bounds.Height;
        if (cw <= 0 || ch <= 0) return;

        if (vm.IsUgoira)
        {
            // Ugoira: the AnimatedImage intrinsic size isn't known until rendered.
            // Use the canvas size and let Stretch=Uniform handle aspect; just center it.
            var ugoira = UgoiraImage;
            if (ugoira == null) return;
            var iw = ugoira.DesiredSize.Width > 0 ? ugoira.DesiredSize.Width : cw;
            var ih = ugoira.DesiredSize.Height > 0 ? ugoira.DesiredSize.Height : ch;
            var scaleX = cw / iw;
            var scaleY = ch / ih;
            var s = Math.Min(scaleX, scaleY);
            var fw = iw * s;
            var fh = ih * s;
            ugoira.Width  = cw;   // fill canvas width; Stretch=Uniform keeps aspect
            ugoira.Height = ch;
            Canvas.SetLeft(ugoira, 0);
            Canvas.SetTop(ugoira, 0);
            _ = (fw, fh); // suppress unused warning
            return;
        }

        if (Image == null) return;
        var bmp = vm.CurrentPageBitmap;
        if (bmp == null) return;

        var bw = bmp.PixelSize.Width;
        var bh = bmp.PixelSize.Height;

        var scX = cw / bw;
        var scY = ch / bh;
        _scale = Math.Min(scX, scY);
        _imgW = bw * _scale;
        _imgH = bh * _scale;
        _imgX = (cw - _imgW) / 2;
        _imgY = (ch - _imgH) / 2;

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (Image == null || Canvas == null) return;

        // Size the image element to its logical display size
        Image.Width = _imgW;
        Image.Height = _imgH;

        // Position on canvas
        Canvas.SetLeft(Image, _imgX);
        Canvas.SetTop(Image, _imgY);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        var pos = e.GetPosition(Canvas);
        ZoomAroundPoint(factor, pos.X, pos.Y);
        e.Handled = true;
    }

    private void ZoomAroundPoint(double factor, double cx, double cy)
    {
        if (Canvas == null || Image == null) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;
        var bmp = vm.CurrentPageBitmap;
        if (bmp == null) return;

        var newScale = _scale * factor;
        if (newScale < 0.1) newScale = 0.1;
        if (newScale > 50) newScale = 50;

        var bw = bmp.PixelSize.Width;
        var bh = bmp.PixelSize.Height;

        var oldW = bw * _scale;
        var oldH = bh * _scale;
        var newW = bw * newScale;
        var newH = bh * newScale;

        _imgX = cx - (cx - _imgX) * (newW / oldW);
        _imgY = cy - (cy - _imgY) * (newH / oldH);
        _scale = newScale;
        _imgW = newW;
        _imgH = newH;

        ApplyTransform();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _pressTime = DateTime.Now;
        _panStart = e.GetPosition(Canvas);
        _panStartX = _imgX;
        _panStartY = _imgY;
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || Canvas == null) return;
        var pos = e.GetPosition(Canvas);
        _imgX = _panStartX + (pos.X - _panStart.X);
        _imgY = _panStartY + (pos.Y - _panStart.Y);
        ApplyTransform();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;

        if (WindowState == WindowState.FullScreen && DataContext is ArtworkViewerViewModel vm)
        {
            var clickDuration = DateTime.Now - _pressTime;
            if (clickDuration.TotalMilliseconds < 300)
            {
                var pos = e.GetPosition(Canvas);
                var canvasWidth = Canvas?.Bounds.Width ?? 0;
                
                if (pos.X < canvasWidth * 0.3 && vm.PrevPageCommand.CanExecute(null))
                    vm.PrevPageCommand.Execute(null);
                else if (pos.X > canvasWidth * 0.7 && vm.NextPageCommand.CanExecute(null))
                    vm.NextPageCommand.Execute(null);
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (DataContext is not ArtworkViewerViewModel vm) return;

        var settings = AppServices.Get<Pikura.Core.Settings.SettingsService>().Current;

        // Left/Right: page navigation within the artwork first.
        // If no page turn is possible (single-page or at the edge) and gallery keyboard
        // nav is enabled, fall through to artwork-level navigation in the nav list.
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (vm.CurrentPageIndex > 0)
                vm.PrevPageCommand.Execute(null);
            else if (settings.FullscreenKeyboardNavEnabled)
                NavigateGalleryArtwork(-1, vm);
            return;
        }

        if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (vm.CurrentPageIndex < vm.PageCount - 1)
                vm.NextPageCommand.Execute(null);
            else if (settings.FullscreenKeyboardNavEnabled)
                NavigateGalleryArtwork(+1, vm);
            return;
        }
    }

    private void NavigateGalleryArtwork(int direction, ArtworkViewerViewModel currentVm)
    {
        var tab = _gallery.SelectedViewerTab;
        IReadOnlyList<ArtworkCardViewModel> list;
        if (tab?.NavList.Count > 0)
            list = tab.NavList;
        else if (_gallery.InlineViewerCardList is { } ext)
            list = ext;
        else
            list = _gallery.FilteredArtworks;
        if (list.Count == 0) return;

        var currentCard = _gallery.InlineViewerCard;
        if (currentCard == null) return;
        var idx = list.TakeWhile(c => c.Id != currentCard.Id).Count();
        if (idx >= list.Count) return;

        var nextIdx = idx + direction;
        if (nextIdx < 0 || nextIdx >= list.Count) return;

        var nextCard = list[nextIdx];
        tab?.NavigateTo(nextCard);
        _gallery.InlineViewerCard = nextCard;

        // Reset canvas state so stale image/position from previous artwork is cleared
        _scale = 1.0;
        _imgX = _imgY = _imgW = _imgH = 0;
        if (Image != null) { Image.Width = 0; Image.Height = 0; }
        AttachViewModel(new ArtworkViewerViewModel(nextCard.Artwork, _gallery, this));
    }
}
