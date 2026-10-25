using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Core.Utilities;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// ViewModel for batch downloading artworks by image ID.
/// </summary>
public partial class DownloadByImageIdViewModel : ViewModelBase
{
    private readonly PixivClient _client;
    private readonly PixivDownloadService _downloadService;
    private readonly SettingsService _settingsService;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly FilePickerService _filePicker;
    // Input
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private ObservableCollection<string> _parsedIds = new();
    [ObservableProperty] private string _invalidEntriesSummary = "";
    [ObservableProperty] private bool _hasInvalidInput;

    // Options
    [ObservableProperty] private string _selectedPageRange = "0 (All)";
    [ObservableProperty] private bool _skipExisting = true;

    // Settings override
    [ObservableProperty] private bool _useGlobalSettings = true;
    [ObservableProperty] private SettingsOverride _customSettings;

    // Progress tracking
    [ObservableProperty] private bool _hasActiveJob;
    [ObservableProperty] private Guid? _currentJobId;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string? _currentItemName;
    [ObservableProperty] private string _statusMessage = "Ready";

    // Options
    public string[] PageRangeOptions { get; } = { "0 (All)", "1", "1-5", "1-10", "First page only" };

    public DownloadByImageIdViewModel(
        PixivClient client,
        PixivDownloadService downloadService,
        SettingsService settingsService,
        DownloadCoordinator coordinator,
        DialogService dialogService,
        FilePickerService filePicker)
    {
        _client = client;
        _downloadService = downloadService;
        _settingsService = settingsService;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _filePicker = filePicker;

        // Initialize custom settings from global
        CustomSettings = SettingsOverride.FromGlobalSettings(settingsService.Current);
        CustomSettings.UseGlobalSettings = false;

    }

    // Called whenever InputText changes
    partial void OnInputTextChanged(string value)
    {
        ParseInput();
    }

    private void ParseInput()
    {
        ParsedIds.Clear();
        var invalidEntries = new List<string>();

        if (string.IsNullOrWhiteSpace(InputText))
        {
            HasInvalidInput = false;
            InvalidEntriesSummary = "";
            return;
        }

        // Split by newlines, commas, or spaces
        var separators = new[] { '\n', '\r', ',', ' ', '\t' };
        var entries = InputText.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Try to extract ID
            var id = ExtractImageId(trimmed);

            if (!string.IsNullOrEmpty(id) && !ParsedIds.Contains(id))
            {
                ParsedIds.Add(id);
            }
            else if (string.IsNullOrEmpty(id))
            {
                invalidEntries.Add(trimmed.Length > 30 ? trimmed[..30] + "..." : trimmed);
            }
        }

        HasInvalidInput = invalidEntries.Count > 0;
        InvalidEntriesSummary = invalidEntries.Count > 0
            ? string.Join(", ", invalidEntries.Take(5)) + (invalidEntries.Count > 5 ? $" and {invalidEntries.Count - 5} more..." : "")
            : "";
    }

    private static string? ExtractImageId(string input)
    {
        // Direct numeric ID
        if (Regex.IsMatch(input, @"^\d+$"))
            return input;

        // URL patterns
        // https://www.pixiv.net/en/artworks/12345678
        // https://www.pixiv.net/artworks/12345678
        // https://www.pixiv.net/member_illust.php?mode=medium&illust_id=12345678
        var patterns = new[]
        {
            @"pixiv\.net.*artworks/(\d+)",
            @"illust_id=(\d+)",
            @"pixiv\.net/i/(\d+)",
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    #region Commands

    /// <summary>
    /// Called from the code-behind with clipboard text (Avalonia clipboard requires a TopLevel reference).
    /// </summary>
    public void PasteFromClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        InputText = string.IsNullOrEmpty(InputText)
            ? text
            : InputText + "\n" + text;
        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_PastedFromClipboard"];
    }

    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        try
        {
            var content = await _filePicker.PickAndReadTextFileAsync("Import Image IDs");
            if (string.IsNullOrEmpty(content)) return;

            // Append to existing input
            InputText = string.IsNullOrEmpty(InputText)
                ? content
                : InputText + "\n" + content;

            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_ImportedIDsFromFile"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to import from file");
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_Error"], $"Failed to import: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearInput()
    {
        InputText = "";
        ParsedIds.Clear();
        HasInvalidInput = false;
        InvalidEntriesSummary = "";
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (ParsedIds.Count == 0)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_NoIDs"], Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_PleaseEnterAtLeastOneImage"]);
            return;
        }

        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_CreatingDownloadJob"];

        try
        {
            // Create targets
            var targets = ParsedIds.Select(id => new DownloadTarget
            {
                TargetId = id,
                Name = $"Artwork {id}",
                Type = TargetType.Artwork,
                PageRange = ExtractPageRange(SelectedPageRange)
            }).ToList();

            // Determine settings override
            var settingsOverride = UseGlobalSettings
                ? new SettingsOverride { UseGlobalSettings = true }
                : CustomSettings;

            // Create and start job
            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.ImageId,
                $"Download {ParsedIds.Count} Images",
                targets,
                settingsOverride,
                startImmediately: true);

            CurrentJobId = job.Id;
            HasActiveJob = true;
            TotalCount = targets.Count;
            CompletedCount = 0;
            ProgressPercent = 0;

            // Subscribe to progress
            var progress = new Progress<JobProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                CompletedCount = p.CompletedTargets;
                CurrentItemName = p.CurrentTargetName;
                StatusMessage = p.Message ?? $"Downloading {p.CompletedTargets}/{p.TotalTargets}...";

                if (p.Status == JobStatus.Completed || p.Status == JobStatus.Failed)
                {
                    HasActiveJob = false;
                }
            });

            _coordinator.SubscribeToProgress(job.Id, progress);
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_DownloadStarted"];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start download");
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_Error"], $"Failed to start download: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (CurrentJobId.HasValue)
        {
            await _coordinator.CancelJobAsync(CurrentJobId.Value);
            HasActiveJob = false;
            StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_DownloadCancelled"];
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_PresetSaved"], Pikura.Avalonia.Localization.LocalizationService.Instance["DlId_ConfigurationSavedNotYetImplemented"]).ConfigureAwait(false);
    }

    #endregion

    private static string ExtractPageRange(string option)
    {
        return option switch
        {
            "0 (All)" => "0",
            "First page only" => "1",
            _ => option
        };
    }
}
