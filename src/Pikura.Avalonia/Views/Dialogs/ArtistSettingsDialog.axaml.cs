using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pikura.Core.Models;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class ArtistSettingsDialog : Window
{
    public SettingsOverride Settings { get; private set; }
    public string ArtistName { get; }

    public ArtistSettingsDialog()
    {
        InitializeComponent();
        Settings = null!;
        ArtistName = string.Empty;
    }

    public ArtistSettingsDialog(SettingsOverride settings, string artistName)
    {
        Settings = settings;
        ArtistName = artistName;
        InitializeComponent();
        LoadSettings();

        UseGlobalSettingsCheckBox.IsCheckedChanged += (_, _) =>
            SettingsPanel.IsEnabled = !(UseGlobalSettingsCheckBox.IsChecked ?? true);
    }

    private void LoadSettings()
    {
        TitleTextBlock.Text = $"Custom Settings for {ArtistName}";
        UseGlobalSettingsCheckBox.IsChecked = Settings.UseGlobalSettings;
        SettingsPanel.IsEnabled = !Settings.UseGlobalSettings;

        DownloadRootTextBox.Text = Settings.DownloadRoot ?? string.Empty;
        FolderTemplateTextBox.Text = Settings.FolderTemplate ?? string.Empty;
        FilenameTemplateTextBox.Text = Settings.FilenameTemplate ?? string.Empty;
        IncludeTagsTextBox.Text = Settings.IncludeTags ?? string.Empty;
        ExcludeTagsTextBox.Text = Settings.ExcludeTagsFilter ?? string.Empty;
        DateFromTextBox.Text = Settings.DateFrom?.ToString("yyyy-MM-dd") ?? string.Empty;
        DateToTextBox.Text = Settings.DateTo?.ToString("yyyy-MM-dd") ?? string.Empty;
        FilterAiCheckBox.IsChecked = Settings.FilterAiGenerated;
        CreateSubfolderCheckBox.IsChecked = Settings.CreateSubfolderPerSubmission;
        SeparateR18CheckBox.IsChecked = Settings.SeparateR18Folder;
        AllowRedownloadCheckBox.IsChecked = Settings.AllowRedownload;
    }

    private async void BrowseFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Select download folder for {ArtistName}",
            AllowMultiple = false,
            SuggestedStartLocation = !string.IsNullOrEmpty(DownloadRootTextBox.Text)
                ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(DownloadRootTextBox.Text)
                : null
        });

        var folder = folders.FirstOrDefault();
        if (folder != null)
            DownloadRootTextBox.Text = folder.Path.LocalPath;
    }

    private void ClearDatesButton_Click(object? sender, RoutedEventArgs e)
    {
        DateFromTextBox.Text = string.Empty;
        DateToTextBox.Text = string.Empty;
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        UseGlobalSettingsCheckBox.IsChecked = true;
        DownloadRootTextBox.Text = string.Empty;
        FolderTemplateTextBox.Text = string.Empty;
        FilenameTemplateTextBox.Text = string.Empty;
        IncludeTagsTextBox.Text = string.Empty;
        ExcludeTagsTextBox.Text = string.Empty;
        DateFromTextBox.Text = string.Empty;
        DateToTextBox.Text = string.Empty;
        FilterAiCheckBox.IsChecked = false;
        CreateSubfolderCheckBox.IsChecked = false;
        SeparateR18CheckBox.IsChecked = false;
        AllowRedownloadCheckBox.IsChecked = false;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        Settings.UseGlobalSettings = UseGlobalSettingsCheckBox.IsChecked ?? true;

        if (!Settings.UseGlobalSettings)
        {
            Settings.DownloadRoot = NullIfEmpty(DownloadRootTextBox.Text);
            Settings.FolderTemplate = NullIfEmpty(FolderTemplateTextBox.Text);
            Settings.FilenameTemplate = NullIfEmpty(FilenameTemplateTextBox.Text);
            Settings.IncludeTags = NullIfEmpty(IncludeTagsTextBox.Text);
            Settings.ExcludeTagsFilter = NullIfEmpty(ExcludeTagsTextBox.Text);
            Settings.DateFrom = ParseDate(DateFromTextBox.Text);
            Settings.DateTo = ParseDate(DateToTextBox.Text);
            Settings.FilterAiGenerated = FilterAiCheckBox.IsChecked;
            Settings.CreateSubfolderPerSubmission = CreateSubfolderCheckBox.IsChecked;
            Settings.SeparateR18Folder = SeparateR18CheckBox.IsChecked;
            Settings.AllowRedownload = AllowRedownloadCheckBox.IsChecked;
        }
        else
        {
            Settings.DownloadRoot = null;
            Settings.FolderTemplate = null;
            Settings.FilenameTemplate = null;
            Settings.IncludeTags = null;
            Settings.ExcludeTagsFilter = null;
            Settings.DateFrom = null;
            Settings.DateTo = null;
            Settings.FilterAiGenerated = null;
            Settings.CreateSubfolderPerSubmission = null;
            Settings.SeparateR18Folder = null;
            Settings.AllowRedownload = null;
        }

        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s.Trim(), out var dt)) return dt;
        return null;
    }
}
