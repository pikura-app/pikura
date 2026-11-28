using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.Views.Gallery;
using Pikura.Core.Models;
using Pikura.Core.Services;

namespace Pikura.Avalonia.Controls;

/// <summary>Renders a pixiv comment body: a stamp image, or text with "(shortcode)" emoji shown as images.</summary>
public sealed class PixivCommentBody : ContentControl
{
    public static readonly StyledProperty<PixivComment?> CommentProperty =
        AvaloniaProperty.Register<PixivCommentBody, PixivComment?>(nameof(Comment));

    private static readonly Regex Shortcode = new(@"\((" + string.Join("|", InlineArtworkViewer.EmojiCatalog.Select(e => Regex.Escape(e.Shortcode))) + @")\)", RegexOptions.Compiled);

    public PixivComment? Comment { get => GetValue(CommentProperty); set => SetValue(CommentProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommentProperty) Content = Build(Comment);
    }

    private static Control? Build(PixivComment? c)
    {
        if (c is null) return null;
        if (c.HasStamp && string.IsNullOrEmpty(c.Comment))
        {
            var stamp = new Image { Width = 56, Height = 56, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            _ = LoadAsync(stamp, $"https://source.pixiv.net/common/images/stamp/generated-stamps/{c.StampId}_s.jpg?20180605");
            return stamp;
        }
        var text = c.Comment;
        if (!Shortcode.IsMatch(text))
            return new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var panel = new WrapPanel();
        var last = 0;
        foreach (Match m in Shortcode.Matches(text))
        {
            if (m.Index > last) panel.Children.Add(new TextBlock { Text = text[last..m.Index], FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var id = InlineArtworkViewer.EmojiCatalog.First(e => e.Shortcode == m.Groups[1].Value).Id;
            var img = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform, Margin = new Thickness(1, 0) };
            _ = LoadAsync(img, $"https://source.pixiv.net/common/images/emoji/{id}.png");
            panel.Children.Add(img);
            last = m.Index + m.Length;
        }
        if (last < text.Length) panel.Children.Add(new TextBlock { Text = text[last..], FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static async Task LoadAsync(Image img, string url)
    {
        try
        {
            using var sk = await AppServices.Get<PixivImageLoader>().FetchBitmapAsync(url, ThumbnailSize.Small, CancellationToken.None);
            if (sk is null) return;
            img.Source = BitmapInterop.SkiaToAvalonia(sk);
        }
        catch { /* decorative */ }
    }
}
