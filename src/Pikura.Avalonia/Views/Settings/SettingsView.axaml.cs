using Avalonia.Controls;
using Avalonia.Interactivity;
using Pikura.Avalonia.ViewModels;
using System.Diagnostics;
using System.IO;

namespace Pikura.Avalonia.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnPixivLocaleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string locale) return;
        if (DataContext is SettingsViewModel vm)
            vm.SetPixivLocaleCommand.Execute(locale);
    }

    private void OnAppLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string language) return;
        if (DataContext is SettingsViewModel vm)
            vm.SetAppLanguageCommand.Execute(language);
    }

    private void OnFolderTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        // Extract token from format: "%token% — Description"
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FolderTemplate += token;

        // Reset selection
        cb.SelectedIndex = -1;
    }

    private void OnFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameTemplate += token;
        cb.SelectedIndex = -1;
    }

    private void OnMangaFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameMangaFormat += token;
        cb.SelectedIndex = -1;
    }

    private void OnInfoFilenameTokenSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        if (DataContext is not SettingsViewModel vm) return;

        var content = item.Content?.ToString() ?? "";
        var token = content.Split('—')[0].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith('%')) return;

        vm.FilenameInfoFormat += token;
        cb.SelectedIndex = -1;
    }

    private void OnR18TypeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string type) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.R18Type = type;
    }

    private void OnR18ModeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string mode) return;
        if (DataContext is not SettingsViewModel vm) return;
        vm.R18Mode = mode;
    }

    private void OnOverwriteModeButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modeStr) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (int.TryParse(modeStr, out var mode))
            vm.OverwriteMode = mode;
    }

    private void OnCopyAppLogPath(object? sender, RoutedEventArgs e)
        => CopyTextToClipboard(SettingsViewModel.AppLogPath);

    private void OnCopyCrashLogPath(object? sender, RoutedEventArgs e)
        => CopyTextToClipboard(SettingsViewModel.CrashLogPath);

    private void CopyTextToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(text));
        _ = clipboard.SetDataAsync(dt);
    }

    private void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        var folder = SettingsViewModel.AppDataFolder;
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnRemoveOverlayImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        if (DataContext is not SettingsViewModel vm) return;

        var index = vm.OverlayService.ImagePaths.IndexOf(path);
        if (index >= 0)
            vm.OverlayService.RemoveImage(index);
    }

    private void OnClearAllOverlayImagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.OverlayService.ClearImages();
    }
}
