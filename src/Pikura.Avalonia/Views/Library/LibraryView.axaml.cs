using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Library;

public partial class LibraryView : UserControl
{
    public LibraryView() { InitializeComponent(); }

    private void BrowseTab_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is LibraryViewModel vm) vm.TabIndex = 0; }

    private void DuplicatesTab_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is LibraryViewModel vm) vm.TabIndex = 1; }

    private void OnLibraryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm || sender is not ScrollViewer sv) return;
        if (e.ExtentDelta.Y > 0 && sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 50)
            vm.OnScrolledToEnd();
    }
}
