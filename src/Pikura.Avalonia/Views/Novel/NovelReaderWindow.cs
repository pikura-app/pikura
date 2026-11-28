using Avalonia.Controls;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Novel;

/// <summary>Stand-alone novel reader (Search results and Rankings "Open in popup").</summary>
public sealed class NovelReaderWindow : Window
{
    public NovelReaderWindow(string novelId)
    {
        var vm = AppServices.Get<NovelReaderViewModel>();
        Width = 820;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "pixiv novel";
        Content = new NovelReaderView { DataContext = vm };
        vm.CloseRequested += (_, _) => Close();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NovelReaderViewModel.Title) && vm.Title.Length > 0) Title = vm.Title; };
        _ = vm.LoadAsync(novelId);
    }

    public static void Open(string novelId, Control? owner)
    {
        var w = new NovelReaderWindow(novelId);
        if (owner is not null && TopLevel.GetTopLevel(owner) is Window parent) w.Show(parent);
        else w.Show();
    }
}
