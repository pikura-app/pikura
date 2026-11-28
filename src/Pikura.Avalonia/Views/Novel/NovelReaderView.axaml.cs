using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Models;
using Pikura.Core.Services;

namespace Pikura.Avalonia.Views.Novel;

public partial class NovelReaderView : UserControl
{
    private NovelReaderViewModel? _vm;

    public NovelReaderView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as NovelReaderViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            Render();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NovelReaderViewModel.DisplayPage) or nameof(NovelReaderViewModel.FontSize))
            Dispatcher.UIThread.Post(Render);
        if (e.PropertyName == nameof(NovelReaderViewModel.BlurCover))
            CoverImage.Effect = _vm?.BlurCover == true ? new BlurEffect { Radius = 18 } : null;
    }

    private IBrush Muted => this.TryFindResource("DefaultForegroundBrushTertiary", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.Gray;
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#4EA1F3"));

    private void Render()
    {
        PageHost.Children.Clear();
        if (_vm is null || _vm.Pages.Count == 0) return;
        var size = _vm.FontSize;
        foreach (var block in _vm.DisplayPage)
        {
            switch (block.Kind)
            {
                case NovelBlockKind.Chapter:
                    PageHost.Children.Add(new SelectableTextBlock { Text = string.Concat(block.Spans.Select(s => s.Text)), FontSize = size + 4, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) });
                    break;
                case NovelBlockKind.Text:
                    var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size * 1.8, Inlines = new InlineCollection() };
                    foreach (var s in block.Spans)
                    {
                        if (s.Url is { } url)
                        {
                            var link = new TextBlock { Text = s.Text, FontSize = size, Foreground = LinkBrush, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand) };
                            link.PointerPressed += (_, e) => { e.Handled = true; Open(url); };
                            tb.Inlines.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.Baseline });
                        }
                        else
                        {
                            tb.Inlines.Add(new Run(s.Text));
                            if (s.Ruby is { Length: > 0 } ruby)
                                tb.Inlines.Add(new Run($"（{ruby}）") { FontSize = size * 0.65, Foreground = Muted });
                        }
                    }
                    PageHost.Children.Add(tb);
                    break;
                case NovelBlockKind.Jump:
                    var page = int.TryParse(block.Value, out var p) ? p : 1;
                    var jump = new Button { Content = $"→ {page}", HorizontalAlignment = HorizontalAlignment.Left };
                    jump.Click += (_, _) => _vm.GoToPage(page);
                    PageHost.Children.Add(jump);
                    break;
                case NovelBlockKind.PixivImage:
                case NovelBlockKind.UploadedImage:
                    var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 600, HorizontalAlignment = HorizontalAlignment.Center };
                    PageHost.Children.Add(img);
                    _ = LoadImageAsync(img, block);
                    break;
            }
        }
        // Keep the reading position when only the text changed (translation, font size).
        var pageKey = $"{_vm.Novel?.Id}|{_vm.PageIndex}";
        if (pageKey != _renderedPage) TextScroll.Offset = default;
        _renderedPage = pageKey;
    }

    private string? _renderedPage;

    private async System.Threading.Tasks.Task LoadImageAsync(Image target, NovelBlock block)
    {
        if (_vm is null) return;
        try
        {
            string? url = null;
            if (block.Kind == NovelBlockKind.UploadedImage)
                _vm.Novel?.EmbeddedImages.TryGetValue(block.Value, out url);
            else
            {
                var parts = block.Value.Split('-');
                var pages = await _vm.Client.GetArtworkPagesAsync(parts[0]);
                var index = parts.Length > 1 && int.TryParse(parts[1], out var pi) ? pi - 1 : 0;
                url = pages.ElementAtOrDefault(Math.Max(0, index))?.Urls.Regular;
            }
            if (url is null) return;
            var bytes = await _vm.Loader.FetchBytesAsync(url);
            if (bytes is null) return;
            using var ms = new System.IO.MemoryStream(bytes);
            target.Source = new global::Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch { }
    }

    private static void Open(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
