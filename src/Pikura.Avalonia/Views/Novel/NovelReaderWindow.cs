using Avalonia.Controls;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Views.Novel;

/// <summary>Stand-alone novel reader with tabs (Search results, Rankings popup and Fullscreen).</summary>
public sealed class NovelReaderWindow : Window
{
    private static NovelReaderWindow? _current;
    private readonly NovelReaderHostViewModel _host;

    private NovelReaderWindow()
    {
        _host = new NovelReaderHostViewModel(AppServices.Get<PixivClient>(), AppServices.Get<PixivImageLoader>(), AppServices.Get<SettingsService>(), canExpand: false);
        Width = 860;
        Height = 920;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "pixiv novel";
        Content = new NovelReaderHostView { DataContext = _host };
        _host.CloseRequested += (_, _) => Close();
        _host.FullscreenRequested += (_, _) => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        _host.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NovelReaderHostViewModel.SelectedTab)) Title = _host.SelectedTab?.Title is { Length: > 0 } t ? t : "pixiv novel"; };
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    /// <summary>Opens the novel as a new tab in the reader window, creating the window if needed.</summary>
    public static void Open(string novelId, Control? owner, bool fullscreen = false)
    {
        var w = _current;
        if (w is null)
        {
            w = _current = new NovelReaderWindow();
            if (owner is not null && TopLevel.GetTopLevel(owner) is Window parent) w.Show(parent);
            else w.Show();
        }
        w._host.Open(novelId, newTab: true);
        if (fullscreen) w.WindowState = WindowState.FullScreen;
        w.Activate();
    }
}
