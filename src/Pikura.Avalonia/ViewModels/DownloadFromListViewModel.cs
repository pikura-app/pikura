using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;

namespace Pikura.Avalonia.ViewModels;

public partial class DownloadFromListViewModel : ViewModelBase
{
    // ── Dependencies ───────────────────────────────────────────────────────
    private readonly PixivClient _client;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;

    // ── Properties ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _selectedFilePath = string.Empty;
    [ObservableProperty] private string _listContent = string.Empty;
    [ObservableProperty] private int _itemCount = 0;
    public ObservableCollection<ListItem> ParsedItems { get; } = [];
    public bool HasItems => ParsedItems.Count > 0;

    public class ListItem
    {
        public string OriginalText { get; set; } = string.Empty;
        public ListItemType Type { get; set; }
        public string Value { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public enum ListItemType
    {
        ArtworkUrl,
        ArtworkId,
        Tag
    }

    // ── Constructor ───────────────────────────────────────────────────────
    public DownloadFromListViewModel(
        PixivClient client,
        DownloadCoordinator coordinator,
        DialogService dialogService)
    {
        _client = client;
        _coordinator = coordinator;
        _dialogService = dialogService;
    }

    // ── Commands ───────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task SelectListFileAsync()
    {
        var window = _dialogService.OwnerWindow;
        if (window?.StorageProvider == null)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_StorageProviderNotAvailable"]);
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_SelectListFile"],
            AllowMultiple = false,
            FileTypeFilter = 
            [
                new FilePickerFileType("Text Files")
                {
                    Patterns = ["*.txt"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file != null)
        {
            SelectedFilePath = file.Path.LocalPath;
            await LoadListFileAsync();
        }
    }

    private async Task LoadListFileAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_Error"], Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_PleaseSelectAValidFile"]);
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(SelectedFilePath);
            ListContent = content;
            ParsedItems.Clear();
            
            // Parse the file - support URLs, IDs, or tags
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    var item = ParseListItem(trimmed);
                    if (item != null)
                        ParsedItems.Add(item);
                }
            }
            
            ItemCount = ParsedItems.Count;
            OnPropertyChanged(nameof(HasItems));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_Error"], $"Failed to read file: {ex.Message}");
        }
    }

    private ListItem? ParseListItem(string line)
    {
        // Check for Pixiv artwork URL
        var urlMatch = Regex.Match(line, @"pixiv\.net/artworks/(\d+)", RegexOptions.IgnoreCase);
        if (urlMatch.Success)
        {
            return new ListItem
            {
                OriginalText = line,
                Type = ListItemType.ArtworkUrl,
                Value = urlMatch.Groups[1].Value,
                DisplayName = $"URL: {urlMatch.Groups[1].Value}"
            };
        }

        // Check for numeric ID
        if (Regex.IsMatch(line, @"^\d+$"))
        {
            return new ListItem
            {
                OriginalText = line,
                Type = ListItemType.ArtworkId,
                Value = line,
                DisplayName = $"ID: {line}"
            };
        }

        // Treat as tag
        return new ListItem
        {
            OriginalText = line,
            Type = ListItemType.Tag,
            Value = line,
            DisplayName = $"Tag: {line}"
        };
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (!HasItems)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_NoItems"], Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_NoItemsToDownloadPleaseSelect"]);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Start Download",
            $"Download {ItemCount} items from the list?");
        
        if (!confirmed) return;

        try
        {
            // Create download targets
            var targets = new List<DownloadTarget>();
            var urlCount = 0;
            var idCount = 0;
            var tagCount = 0;

            foreach (var item in ParsedItems)
            {
                if (item.Type == ListItemType.ArtworkUrl || item.Type == ListItemType.ArtworkId)
                {
                    targets.Add(new DownloadTarget
                    {
                        TargetId = item.Value,
                        Name = $"Artwork {item.Value}",
                        Type = TargetType.Artwork
                    });
                    if (item.Type == ListItemType.ArtworkUrl)
                        urlCount++;
                    else
                        idCount++;
                }
                else if (item.Type == ListItemType.Tag)
                {
                    targets.Add(new DownloadTarget
                    {
                        TargetId = item.Value,
                        Name = $"Tag: {item.Value}",
                        Type = TargetType.Tag
                    });
                    tagCount++;
                }
            }

            // Create download job
            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.ListFile,
                $"List File Download - {Path.GetFileNameWithoutExtension(SelectedFilePath)}",
                targets);

            // Start the job
            await _coordinator.StartJobAsync(job.Id);

            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_DownloadStarted"], 
                $"Queued {urlCount} URLs, {idCount} IDs, and {tagCount} tags for download.\n\nCheck the History tab for progress.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlList_Error"], $"Failed to start download: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearList()
    {
        SelectedFilePath = string.Empty;
        ListContent = string.Empty;
        ParsedItems.Clear();
        ItemCount = 0;
        OnPropertyChanged(nameof(HasItems));
    }
}
