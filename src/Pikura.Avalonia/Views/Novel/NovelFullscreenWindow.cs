using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Novel;

/// <summary>Fullscreen view of a viewer tab's novel. Shares the tab's reader, so closing it
/// (Esc or Exit fullscreen) returns to the same page in the inline viewer.</summary>
public sealed class NovelFullscreenWindow : Window
{
    public NovelFullscreenWindow(NovelReaderViewModel reader)
    {
        Title = reader.Title.Length > 0 ? reader.Title : "pixiv novel";
        WindowState = WindowState.FullScreen;
        // Same theme-aware background as the image fullscreen window and the inline reader.
        Bind(BackgroundProperty, this.GetResourceObservable("ImageCanvasBackgroundBrush"));
        var exit = new Button
        {
            Content = LocalizationService.Instance["Novel_ExitFullscreen"],
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 6),
        };
        exit.Click += (_, _) => Close();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(exit);
        var view = new NovelReaderView { DataContext = reader };
        Grid.SetRow(view, 1);
        grid.Children.Add(view);
        Content = grid;
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F11) { Close(); e.Handled = true; }
        };
    }
}
