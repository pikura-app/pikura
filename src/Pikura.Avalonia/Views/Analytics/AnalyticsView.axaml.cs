using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Analytics;

public partial class AnalyticsView : UserControl
{
    public AnalyticsView()
    {
        InitializeComponent();
    }

    private AnalyticsViewModel? VM => DataContext as AnalyticsViewModel;

    private void OnArtworkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (VM is not { } vm || (sender as Control)?.DataContext is not AnalyticsArtwork item) return;
        var list = vm.RecentArtworks.Contains(item) ? vm.RecentArtworks.ToList() : vm.MostViewedArtworks.ToList();
        e.Handled = true;
        var wasBlurred = item.Card.IsR18 && item.Card.IsBlurred;
        vm.OpenArtwork(item, list);
        if (!wasBlurred && TopLevel.GetTopLevel(this) is MainWindow main) main.LoadGalleryView();
    }

    private void OnArticleClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AnalyticsArticle a || TopLevel.GetTopLevel(this) is not MainWindow main) return;
        if (a.Dic is { } d) main.OpenDicArticle(d.Tag, d.Lang);
        else if (a.Pixivision is { } p) main.OpenPixivisionArticle(p);
    }
}
