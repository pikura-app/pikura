using Avalonia.Controls;
using Avalonia.Interactivity;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views.Dialogs;

/// <summary>Rename preview for Library files; closes with the plan to apply, or null on cancel.</summary>
public partial class LibraryRenameDialog : Window
{
    private LibraryRenameDialogViewModel? VM => DataContext as LibraryRenameDialogViewModel;

    public LibraryRenameDialog() => InitializeComponent();

    public LibraryRenameDialog(LibraryRenameDialogViewModel vm) : this() => DataContext = vm;

    private void OnFolderTokenPicked(object? sender, SelectionChangedEventArgs e) => InsertToken(FolderTokenPicker, FolderBox);
    private void OnFilenameTokenPicked(object? sender, SelectionChangedEventArgs e) => InsertToken(FilenameTokenPicker, FilenameBox);

    /// <summary>Inserts the picked token at the caret, then resets the picker so the same token can be picked again.</summary>
    private static void InsertToken(ComboBox picker, TextBox box)
    {
        if (picker.SelectedItem is not string token) return;
        var text = box.Text ?? "";
        var at = System.Math.Clamp(box.CaretIndex, 0, text.Length);
        box.Text = text.Insert(at, token);
        box.CaretIndex = at + token.Length;
        picker.SelectedIndex = -1;
        box.Focus();
    }

    private void OnRename(object? sender, RoutedEventArgs e) => Close(VM?.Plan);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
