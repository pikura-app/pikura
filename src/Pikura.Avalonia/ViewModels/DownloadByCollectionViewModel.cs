using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// ViewModel for batch downloading one or more Pixiv Collections. Each collection becomes its
/// own download job (named after the collection) built from
/// <see cref="CollectionDownloadHelper.BuildTargets"/> — the same logic the Collections tab's
/// "Download this collection" button uses.
/// </summary>
public partial class DownloadByCollectionViewModel : ViewModelBase
{
    private readonly PixivClient _client;
    private readonly SettingsService _settingsService;
    private readonly DownloadCoordinator _coordinator;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private ObservableCollection<string> _parsedIds = new();
    [ObservableProperty] private bool _hasInvalidInput;
    [ObservableProperty] private string _invalidEntriesSummary = "";

    /// <summary>When true, each collection's works are grouped into their own
    /// Collections\{title} folder. When false, downloads use the normal global folder/filename
    /// template like any other artwork download.</summary>
    [ObservableProperty] private bool _useCollectionFolder = true;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _statusMessage = "Ready";

    public DownloadByCollectionViewModel(
        PixivClient client,
        SettingsService settingsService,
        DownloadCoordinator coordinator)
    {
        _client = client;
        _settingsService = settingsService;
        _coordinator = coordinator;
    }

    partial void OnInputTextChanged(string value) => ParseInput();

    private void ParseInput()
    {
        ParsedIds.Clear();
        var invalid = new List<string>();

        if (string.IsNullOrWhiteSpace(InputText))
        {
            HasInvalidInput = false;
            InvalidEntriesSummary = "";
            return;
        }

        var entries = InputText.Split(['\n', '\r', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            var id = ExtractCollectionId(trimmed);
            if (!string.IsNullOrEmpty(id) && !ParsedIds.Contains(id))
                ParsedIds.Add(id);
            else if (string.IsNullOrEmpty(id))
                invalid.Add(trimmed.Length > 30 ? trimmed[..30] + "..." : trimmed);
        }

        HasInvalidInput = invalid.Count > 0;
        InvalidEntriesSummary = invalid.Count > 0 ? $"Couldn't parse: {string.Join(", ", invalid)}" : "";
    }

    private static string? ExtractCollectionId(string input)
    {
        var m = Regex.Match(input, @"collections/(\d+)");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(input, @"^\d+$") ? input : null;
    }

    [RelayCommand]
    private void ClearInput()
    {
        InputText = "";
        ParsedIds.Clear();
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (ParsedIds.Count == 0) return;
        IsDownloading = true;
        var succeeded = 0;
        var failed = new List<string>();
        try
        {
            foreach (var id in ParsedIds.ToList())
            {
                StatusMessage = $"Loading collection {id}…";
                var collection = await _client.GetCollectionAsync(id);
                if (collection == null || collection.Works.Count == 0)
                {
                    failed.Add(id);
                    continue;
                }

                var (targets, _) = CollectionDownloadHelper.BuildTargets(
                    collection, UseCollectionFolder, _settingsService.Current.DownloadRoot);

                await _coordinator.CreateJobAsync(
                    DownloadJobType.ImageId,
                    $"Collection: {collection.Title}",
                    targets,
                    settingsOverride: null,
                    startImmediately: true);
                succeeded++;
            }

            StatusMessage = failed.Count == 0
                ? $"Started {succeeded} collection download job(s)."
                : $"Started {succeeded} job(s); couldn't load: {string.Join(", ", failed)}";
        }
        finally { IsDownloading = false; }
    }
}
