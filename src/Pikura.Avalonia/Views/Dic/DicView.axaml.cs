using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Services;

namespace Pikura.Avalonia.Views.Dic;

public partial class DicView : UserControl
{
    private DicViewModel? _vm;
    private readonly Dictionary<int, Control> _headings = new();

    public DicView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook(DataContext as DicViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            if (_vm is null) return;
            _vm.Service.Attach(Web);
            _ = _vm.EnsureLoadedAsync();
        };
    }

    private void Hook(DicViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        if (TopLevel.GetTopLevel(this) is not null)
        {
            vm.Service.Attach(Web);
            _ = vm.EnsureLoadedAsync();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DicViewModel.Article) or nameof(DicViewModel.ArticleInfo))
            Dispatcher.UIThread.Post(RenderArticle);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null) _vm.SearchCommand.Execute(null);
    }

    private void OnArticleCardClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DicArticleCard card) _ = _vm?.OpenArticleAsync(card.Tag);
    }

    private void OnTagChipClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string tag) _ = _vm?.OpenArticleAsync(tag);
    }

    private void OnOpenInBrowser(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) OpenUrl(_vm.CurrentUrl);
    }

    private void OnSearchPixiv(object? sender, RoutedEventArgs e)
    {
        var tag = _vm?.PixivTag ?? _vm?.ArticleTag;
        if (string.IsNullOrWhiteSpace(tag)) return;
        var gallery = AppServices.Get<GalleryViewModel>();
        _ = gallery.SearchByTagAsync(tag);
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.LoadGalleryView();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ── Article rendering ────────────────────────────────────────────────

    private IBrush Accent => this.TryFindResource("AccentBrush", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.DodgerBlue;
    private IBrush Muted => this.TryFindResource("DefaultForegroundBrushTertiary", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.Gray;
    private IBrush Panel2 => this.TryFindResource("Container2BackgroundBrush", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.Transparent;
    private IBrush Border2 => this.TryFindResource("Container2BorderBrush", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.Gray;

    private void RenderArticle()
    {
        ArticleHost.Children.Clear();
        _headings.Clear();
        if (_vm?.Article is not { } a) return;

        var lang = _vm.Language;
        var title = Str(a, "tagName") ?? _vm.ArticleTag;
        ArticleHost.Children.Add(new SelectableTextBlock { Text = title, FontSize = 26, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        var sub = string.Join("  ·  ", new[] { Str(a, "translatedTagName"), Str(a, "yomigana") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (sub.Length > 0) ArticleHost.Children.Add(new TextBlock { Text = sub, Foreground = Muted, FontSize = 13 });

        if (_vm.ArticleInfo is { } info)
        {
            var parts = new List<string>();
            void Add(string key, string label) { if (info.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number) parts.Add($"{label}: {v.GetInt64():N0}"); }
            Add("articleViewCount", Tr("Dic_Views"));
            Add("pixivWorkCount", Tr("Dic_Works"));
            Add("checklistCount", Tr("Dic_Checklist"));
            if (a.TryGetProperty("updatedAtTimestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
                parts.Add($"{Tr("Dic_UpdatedAt")}: {DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64()).ToLocalTime():yyyy-MM-dd}");
            ArticleHost.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Foreground = Muted, FontSize = 11 });
        }

        var actions = new WrapPanel();
        var search = new Button { Content = Tr("Dic_SearchPixiv"), Margin = new Thickness(0, 4, 6, 4) };
        search.Click += OnSearchPixiv;
        actions.Children.Add(search);
        var open = new Button { Content = Tr("Dic_OpenInBrowser"), Margin = new Thickness(0, 4, 6, 4) };
        open.Click += OnOpenInBrowser;
        actions.Children.Add(open);
        if (a.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
                if (c.GetString() is { Length: > 0 } cat)
                    actions.Children.Add(new Border { Background = Panel2, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), Margin = new Thickness(0, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = cat, FontSize = 11 } });
        ArticleHost.Children.Add(actions);

        var sensitive = a.TryGetProperty("isSensitive", out var sv) && sv.ValueKind == JsonValueKind.True;
        if (a.TryGetProperty("mainIllust", out var main) && main.ValueKind == JsonValueKind.Object)
            ArticleHost.Children.Add(MainIllust(main, sensitive));

        if (Str(a, "abstract") is { Length: > 0 } abs)
            ArticleHost.Children.Add(new Border
            {
                BorderBrush = Accent, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 4),
                Child = new SelectableTextBlock { Text = abs, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontStyle = FontStyle.Italic },
            });

        if (Str(a, "toc") is { Length: > 2 } tocJson)
        {
            try
            {
                using var toc = JsonDocument.Parse(tocJson);
                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(new TextBlock { Text = Tr("Dic_Contents"), FontWeight = FontWeight.SemiBold });
                foreach (var t in toc.RootElement.EnumerateArray())
                {
                    var id = t.TryGetProperty("link_id", out var li) && li.ValueKind == JsonValueKind.Number ? li.GetInt32() : -1;
                    var link = Link(t.GetProperty("text").GetString() ?? "", () => { if (_headings.TryGetValue(id, out var h)) h.BringIntoView(); });
                    panel.Children.Add(link);
                }
                ArticleHost.Children.Add(new Border { Background = Panel2, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Left, Child = panel });
            }
            catch { }
        }

        if (Str(a, "nodes") is { Length: > 2 } nodesJson)
        {
            try
            {
                using var nodes = JsonDocument.Parse(nodesJson);
                RenderBlocks(nodes.RootElement, ArticleHost, lang);
            }
            catch { }
        }

        if (a.TryGetProperty("relatedArticles", out var rel) && rel.ValueKind == JsonValueKind.Object)
            RenderRelated(rel);

        ArticleScroll.Offset = default;
    }

    private Control MainIllust(JsonElement main, bool sensitive)
    {
        var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 260, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        if (sensitive && _vm!.BlurSensitive) img.Effect = new BlurEffect { Radius = 24 };
        var url = Str(main, "imageUrl");
        if (url is not null) _ = LoadImageAsync(img, url);
        var caption = new StackPanel { Spacing = 2 };
        caption.Children.Add(new TextBlock { Text = Str(main, "illustTitle") ?? "", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (Str(main, "author") is { } author) caption.Children.Add(new TextBlock { Text = author, FontSize = 11, Foreground = Muted });
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        var button = new Button { Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = img, Cursor = new Cursor(StandardCursorType.Hand) };
        var page = Str(main, "illustPageUrl");
        button.Click += (_, _) => { if (page is not null) OpenUrl(page); };
        stack.Children.Add(button);
        stack.Children.Add(caption);
        return stack;
    }

    private async System.Threading.Tasks.Task LoadImageAsync(Image target, string url)
    {
        try
        {
            using var sk = await AppServices.Get<PixivImageLoader>().FetchBitmapAsync(url, ThumbnailSize.Medium);
            if (sk is null) return;
            target.Source = BitmapInterop.SkiaToAvalonia(sk);
        }
        catch { }
    }

    private void RenderRelated(JsonElement rel)
    {
        var cards = new List<(string Label, string Tag)>();
        if (rel.TryGetProperty("parent_article", out var p) && p.ValueKind == JsonValueKind.Object && Str(p, "tagName") is { } pt)
            cards.Add(("↑ " + pt, pt));
        foreach (var key in new[] { "child_articles", "sibling_articles" })
            if (rel.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    if (Str(c, "tagName") is { } t) cards.Add((t, t));
        if (cards.Count == 0) return;
        ArticleHost.Children.Add(new TextBlock { Text = Tr("Dic_Related"), FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 0) });
        var wrap = new WrapPanel();
        foreach (var (label, tag) in cards)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 3), FontSize = 12, Margin = new Thickness(0, 2, 6, 2) };
            b.Click += (_, _) => _ = _vm?.OpenArticleAsync(tag);
            wrap.Children.Add(b);
        }
        ArticleHost.Children.Add(wrap);
    }

    /// <summary>Renders the encyclopedia's node tree (headers, paragraphs, lists, tables, links).</summary>
    private void RenderBlocks(JsonElement nodes, Panel host, string lang)
    {
        SelectableTextBlock? para = null;
        foreach (var n in nodes.EnumerateArray())
        {
            var tag = Str(n, "tag") ?? "";
            switch (tag)
            {
                case "header":
                case "sub_header":
                case "sub_sub_header":
                {
                    para = null;
                    var size = tag == "header" ? 21 : tag == "sub_header" ? 17 : 15;
                    var text = new TextBlock { Text = PlainText(n), FontSize = size, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
                    Control head = tag == "header"
                        ? new Border { BorderBrush = Border2, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 4), Margin = new Thickness(0, 14, 0, 2), Child = text }
                        : new Border { Margin = new Thickness(0, 8, 0, 0), Child = text };
                    if (n.TryGetProperty("link_id", out var id) && id.ValueKind == JsonValueKind.Number) _headings[id.GetInt32()] = head;
                    host.Children.Add(head);
                    break;
                }
                case "p":
                    para = null;
                    host.Children.Add(Paragraph(n, lang));
                    break;
                case "br":
                    para = null;
                    break;
                case "unordered_list":
                case "numbered_list":
                {
                    para = null;
                    var list = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
                    var i = 0;
                    if (n.TryGetProperty("children", out var items))
                        foreach (var item in items.EnumerateArray())
                        {
                            i++;
                            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                            row.Children.Add(new TextBlock { Text = tag == "numbered_list" ? $"{i}." : "•", Margin = new Thickness(0, 0, 6, 0), FontSize = 14 });
                            var body = new StackPanel();
                            Grid.SetColumn(body, 1);
                            if (item.TryGetProperty("children", out var ic)) RenderBlocks(ic, body, lang);
                            row.Children.Add(body);
                            list.Children.Add(row);
                        }
                    host.Children.Add(list);
                    break;
                }
                case "table":
                    para = null;
                    host.Children.Add(Table(n, lang));
                    break;
                case "pixiv_image":
                {
                    para = null;
                    var id = n.TryGetProperty("id", out var iv) ? iv.ToString() : "";
                    host.Children.Add(ChipButton("🖼 pixiv #" + id, () => OpenUrl($"https://www.pixiv.net/artworks/{id}")));
                    break;
                }
                case "youtube":
                {
                    para = null;
                    var id = Str(n, "id") ?? "";
                    host.Children.Add(ChipButton("▶ YouTube", () => OpenUrl($"https://youtu.be/{id}")));
                    break;
                }
                default:
                    // Inline nodes at block level (bare text or links inside a list item) share one paragraph.
                    if (para is null)
                    {
                        para = NewParagraph();
                        host.Children.Add(para);
                    }
                    AddInline(para.Inlines!, n, lang, FontWeight.Normal, FontStyle.Normal);
                    break;
            }
        }
    }

    private SelectableTextBlock NewParagraph() =>
        new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22, Inlines = new InlineCollection() };

    private SelectableTextBlock Paragraph(JsonElement p, string lang)
    {
        var tb = NewParagraph();
        if (p.TryGetProperty("children", out var ch))
            foreach (var c in ch.EnumerateArray()) AddInline(tb.Inlines!, c, lang, FontWeight.Normal, FontStyle.Normal);
        return tb;
    }

    private void AddInline(InlineCollection inlines, JsonElement n, string lang, FontWeight weight, FontStyle style)
    {
        switch (Str(n, "tag"))
        {
            case "text":
                inlines.Add(new Run(Str(n, "text") ?? "") { FontWeight = weight, FontStyle = style });
                break;
            case "br":
                inlines.Add(new LineBreak());
                break;
            case "bold":
            case "italic":
            case "p":
            case "list_item":
                var w = Str(n, "tag") == "bold" ? FontWeight.Bold : weight;
                var s = Str(n, "tag") == "italic" ? FontStyle.Italic : style;
                if (n.TryGetProperty("children", out var ch))
                    foreach (var c in ch.EnumerateArray()) AddInline(inlines, c, lang, w, s);
                break;
            case "article_link":
            {
                var to = Str(n, "to") ?? Str(n, "text") ?? "";
                var exists = !n.TryGetProperty("articleExists", out var ex) || ex.ValueKind != JsonValueKind.False;
                var link = new TextBlock
                {
                    Text = Str(n, "text") ?? to, FontSize = 14, FontWeight = weight, FontStyle = style,
                    Foreground = exists ? Accent : Muted, TextDecorations = TextDecorations.Underline,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                link.PointerPressed += (_, e) => { e.Handled = true; _ = _vm?.OpenArticleAsync(to); };
                inlines.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
                break;
            }
            case "external_link":
            {
                var to = Str(n, "to") ?? "";
                var link = new TextBlock
                {
                    Text = (Str(n, "text") ?? to) + " ↗", FontSize = 14, Foreground = Accent,
                    TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand),
                };
                link.PointerPressed += (_, e) => { e.Handled = true; if (to.StartsWith("http", StringComparison.OrdinalIgnoreCase)) OpenUrl(to); };
                inlines.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
                break;
            }
            default:
                if (Str(n, "text") is { } t) inlines.Add(new Run(t) { FontWeight = weight, FontStyle = style });
                else if (n.TryGetProperty("children", out var cc))
                    foreach (var c in cc.EnumerateArray()) AddInline(inlines, c, lang, weight, style);
                break;
        }
    }

    private Control Table(JsonElement table, string lang)
    {
        var grid = new Grid();
        var rows = table.TryGetProperty("children", out var r) ? r.EnumerateArray().ToList() : [];
        var cols = rows.Count == 0 ? 0 : rows.Max(row => row.TryGetProperty("children", out var c) ? c.GetArrayLength() : 0);
        for (var i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var y = 0; y < rows.Count; y++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (!rows[y].TryGetProperty("children", out var cells)) continue;
            var x = 0;
            foreach (var cell in cells.EnumerateArray())
            {
                var isHeader = Str(cell, "tag") == "table_header";
                var tb = NewParagraph();
                tb.FontSize = 13;
                if (cell.TryGetProperty("children", out var cc))
                    foreach (var c in cc.EnumerateArray()) AddInline(tb.Inlines!, c, lang, isHeader ? FontWeight.SemiBold : FontWeight.Normal, FontStyle.Normal);
                var border = new Border
                {
                    BorderBrush = Border2, BorderThickness = new Thickness(0.5), Padding = new Thickness(8, 4),
                    Background = isHeader ? Panel2 : Brushes.Transparent, MaxWidth = 420, Child = tb,
                };
                Grid.SetRow(border, y);
                Grid.SetColumn(border, x++);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = grid, Margin = new Thickness(0, 4) };
    }

    private Control Link(string text, Action onClick)
    {
        var tb = new TextBlock { Text = text, Foreground = Accent, Cursor = new Cursor(StandardCursorType.Hand), FontSize = 13 };
        tb.PointerPressed += (_, e) => { e.Handled = true; onClick(); };
        return tb;
    }

    private static Button ChipButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3), FontSize = 12, Margin = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static string PlainText(JsonElement n)
    {
        if (Str(n, "text") is { } t) return t;
        return n.TryGetProperty("children", out var ch) ? string.Concat(ch.EnumerateArray().Select(PlainText)) : "";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Tr(string key) => LocalizationService.Instance[key];
}
