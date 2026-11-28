using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Novel;

public partial class NovelReaderHostView : UserControl
{
    public NovelReaderHostView() => InitializeComponent();

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (sender is Control { Tag: NovelReaderViewModel tab } && DataContext is NovelReaderHostViewModel host)
        {
            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) host.CloseTab(tab);
            else host.SelectedTab = tab;
        }
    }
}
