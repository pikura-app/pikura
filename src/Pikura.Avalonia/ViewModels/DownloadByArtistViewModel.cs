using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using FollowedArtist = Pikura.Core.Models.FollowedArtist;
using Pikura.Core.Settings;
using Pikura.Core.Utilities;
using Pikura.Core.Data;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.Views.Dialogs;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

/// <summary>
/// ViewModel for downloading artworks by artist (followed artists or specific IDs).
/// </summary>
public partial class DownloadByArtistViewModel : ViewModelBase
{
    private readonly PixivClient _client;
    private readonly PixivDownloadService _downloadService;
    private readonly SettingsService _settingsService;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly FilePickerService _filePicker;
    private readonly DownloadPresetRepository _presetRepository;
    private readonly ArtistSettingsRepository _artistSettingsRepository;
    private readonly GalleryViewModel _galleryVm;
    private readonly NotificationService _notificationService;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Ready";

    // Artist selection
    [ObservableProperty] private ObservableCollection<SelectedArtist> _selectedArtists = new();
    [ObservableProperty] private string _newArtistId = "";
    [ObservableProperty] private bool _hasClipboardContent;
    [ObservableProperty] private ObservableCollection<string> _parsedArtistIds = new();

    partial void OnNewArtistIdChanged(string value)
    {
        UpdateHasClipboardContent();
        ParseArtistIds(value);
        // Only search preview if single ID entered
        if (ParsedArtistIds.Count == 1)
        {
            _ = SearchArtistPreviewAsync(value);
        }
        else
        {
            ShowArtistPreview = false;
        }
    }

    /// <summary>
    /// Parses artist IDs from input text (comma, newline, or space separated).
    /// </summary>
    private void ParseArtistIds(string input)
    {
        ParsedArtistIds.Clear();
        if (string.IsNullOrWhiteSpace(input)) return;

        // Split by newlines, commas, or spaces
        var separators = new[] { '\n', '\r', ',', ' ', '\t' };
        var entries = input.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Try to extract ID
            var id = ExtractArtistId(trimmed);
            if (!string.IsNullOrEmpty(id) && !ParsedArtistIds.Contains(id))
            {
                ParsedArtistIds.Add(id);
            }
        }
    }

    private void UpdateHasClipboardContent()
    {
        HasClipboardContent = !string.IsNullOrEmpty(QuickClipboardService.LastCopiedId);
    }

    // Artist preview (for add by ID)
    [ObservableProperty] private bool _showArtistPreview;
    [ObservableProperty] private string? _previewArtistName;
    [ObservableProperty] private string? _previewArtistId;
    [ObservableProperty] private string? _previewArtistAvatarUrl;
    [ObservableProperty] private global::Avalonia.Media.Imaging.Bitmap? _previewArtistAvatar;
    [ObservableProperty] private bool _isSearchingArtist;

    // Page range configuration
    [ObservableProperty] private string _defaultPageRange = "0";
    [ObservableProperty] private bool _usePerArtistPageRanges;

    // Settings override
    [ObservableProperty] private bool _useGlobalSettings = true;
    [ObservableProperty] private SettingsOverride _customSettings = new() { UseGlobalSettings = false };

    // Progress tracking
    [ObservableProperty] private bool _hasActiveJob;
    [ObservableProperty] private Guid? _currentJobId;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int _estimatedItemCount;
    [ObservableProperty] private int _completedItemCount;
    [ObservableProperty] private string _progressStatusMessage = "";

    /// <summary>
    /// Calculates estimated total items to download based on artist selection and page ranges.
    /// </summary>
    public int CalculateEstimatedItemCount()
    {
        if (SelectedArtists.Count == 0) return 0;

        int total = 0;
        foreach (var artist in SelectedArtists)
        {
            var pageRange = artist.PageRange ?? DefaultPageRange;
            if (string.IsNullOrWhiteSpace(pageRange) || pageRange == "0" || pageRange.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // Estimate ~48 artworks per artist for "all" (typical first page load)
                total += 48;
            }
            else if (pageRange.Contains('-'))
            {
                // Range like "1-5" - estimate pages * 48
                var parts = pageRange.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end))
                {
                    total += (end - start + 1) * 48;
                }
            }
            else if (int.TryParse(pageRange, out var pageNum) && pageNum > 0)
            {
                // Single page
                total += 48;
            }
        }
        return total;
    }

    /// <summary>
    /// Returns a user-friendly description of what will be downloaded.
    /// </summary>
    public string DownloadPreviewText
    {
        get
        {
            var count = SelectedArtists.Count;
            if (count == 0) return "No artists selected";

            var estimated = CalculateEstimatedItemCount();
            var pageRangeText = UsePerArtistPageRanges ? "custom page ranges" : $"pages {DefaultPageRange}";

            return $"Download {estimated}+ artworks from {count} artist{(count > 1 ? "s" : "")} ({pageRangeText})";
        }
    }
    [ObservableProperty] private int _completedTargets;
    [ObservableProperty] private int _totalTargets;
    [ObservableProperty] private string? _currentTargetName;

    // Page range validation
    public bool DefaultPageRangeInvalid => !PageRangeParser.IsValid(DefaultPageRange);

    // Available page range options for dropdown
    public string[] PageRangePresets { get; } = { "0 (All)", "1", "1-5", "1-10", "2,4,6-10" };

    partial void OnDefaultPageRangeChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultPageRangeInvalid));
        OnPropertyChanged(nameof(DownloadPreviewText));
    }

    partial void OnUsePerArtistPageRangesChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadPreviewText));
    }

    private async Task SearchArtistPreviewAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowArtistPreview = false;
            return;
        }

        // Extract ID from URL or use as-is
        var id = ExtractArtistId(input);
        if (string.IsNullOrEmpty(id))
        {
            ShowArtistPreview = false;
            return;
        }

        // Check if already added
        if (SelectedArtists.Any(a => a.UserId == id))
        {
            ShowArtistPreview = false;
            return;
        }

        IsSearchingArtist = true;
        PreviewArtistAvatar = null;

        try
        {
            var info = await _client.GetArtistAsync(id);
            if (info != null)
            {
                PreviewArtistId = id;
                PreviewArtistName = info.Name;
                PreviewArtistAvatarUrl = info.ImageBigUrl ?? info.ImageUrl;
                ShowArtistPreview = true;

                // Load avatar bitmap in background
                if (!string.IsNullOrWhiteSpace(PreviewArtistAvatarUrl))
                {
                    var url = PreviewArtistAvatarUrl;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var loader = AppServices.Get<PixivImageLoader>();
                            var bytes = await loader.FetchBytesAsync(url);
                            if (bytes == null) return;
                            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                using var ms = new System.IO.MemoryStream(bytes);
                                PreviewArtistAvatar = new global::Avalonia.Media.Imaging.Bitmap(ms);
                            });
                        }
                        catch { /* non-fatal */ }
                    });
                }
            }
            else
            {
                ShowArtistPreview = false;
            }
        }
        catch
        {
            ShowArtistPreview = false;
        }
        finally
        {
            IsSearchingArtist = false;
        }
    }

    private static string? ExtractArtistId(string input)
    {
        input = input.Trim();

        // Check if it's a URL
        if (input.Contains("pixiv.net"))
        {
            // Extract ID from URL patterns like:
            // https://www.pixiv.net/en/users/12345
            // https://www.pixiv.net/member.php?id=12345
            var match = System.Text.RegularExpressions.Regex.Match(input, @"[/=](\d+)[/&]?|$");
            if (match.Success && match.Groups[1].Value.Length > 3)
            {
                return match.Groups[1].Value;
            }
        }

        // Check if it's just a numeric ID
        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"^\d+$"))
        {
            return input;
        }

        return null;
    }

    public DownloadByArtistViewModel(
        PixivClient client,
        PixivDownloadService downloadService,
        SettingsService settingsService,
        DownloadCoordinator coordinator,
        DialogService dialogService,
        FilePickerService filePicker,
        DownloadPresetRepository presetRepository,
        ArtistSettingsRepository artistSettingsRepository,
        GalleryViewModel galleryVm,
        NotificationService notificationService)
    {
        _client = client;
        _downloadService = downloadService;
        _settingsService = settingsService;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _filePicker = filePicker;
        _presetRepository = presetRepository;
        _artistSettingsRepository = artistSettingsRepository;
        _galleryVm = galleryVm;
        _notificationService = notificationService;

        // Initialize custom settings from global
        CustomSettings = SettingsOverride.FromGlobalSettings(settingsService.Current);
        CustomSettings.UseGlobalSettings = false;

        // Subscribe to job progress
        SubscribeToProgress();

        // Subscribe to clipboard changes
        UpdateHasClipboardContent();
        QuickClipboardService.ClipboardChanged += () => UpdateHasClipboardContent();

        // Subscribe to SelectedArtists changes to update preview text
        SelectedArtists.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(DownloadPreviewText));
            OnPropertyChanged(nameof(HasActiveJob));
        };
    }

    private void SubscribeToProgress()
    {
        // Progress subscription handled when job starts
    }

    #region Artist Selection Commands

    [RelayCommand]
    private async Task AddFollowedArtistsAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading followed artists...";

        try
        {
            var existingIds = SelectedArtists.Select(a => a.UserId).ToHashSet();

            // Reuse the Gallery's already-loaded followed artists list. This is
            // the same authoritative, deduplicated collection used by the
            // Gallery view, so the count here matches Gallery's count.
            if (_galleryVm.Artists.Count == 0)
            {
                StatusMessage = "Loading followed artists from Pixiv...";
                await _galleryVm.LoadFollowedArtistsCommand.ExecuteAsync(null);
            }

            if (_galleryVm.Artists.Count == 0)
            {
                await _dialogService.ShowMessageAsync("Info", "No followed artists found.");
                return;
            }

            var allArtists = _galleryVm.Artists
                .Select(a => new SelectableArtist
                {
                    User = new BookmarkedUser
                    {
                        UserId = a.UserId,
                        UserName = a.Name,
                        ProfileImageUrl = a.ProfileImageUrl
                    },
                    IsSelected = false,
                    IsAlreadyAdded = existingIds.Contains(a.UserId)
                })
                .ToList();

            var dialog = new Views.Dialogs.SelectFollowedArtistsDialog(
                allArtists,
                $"{allArtists.Count} followed artists");
            var ownerWindow = _dialogService.OwnerWindow;
            if (ownerWindow == null) return;

            var result = await dialog.ShowDialog<bool>(ownerWindow);
            if (!result) return;

            // Add selected artists and load their custom settings
            int added = 0;
            foreach (var artist in dialog.SelectedArtists)
            {
                if (!SelectedArtists.Any(a => a.UserId == artist.User.UserId))
                {
                    var customSettings = await _artistSettingsRepository.GetByUserIdAsync(artist.User.UserId);
                    SelectedArtists.Add(new SelectedArtist
                    {
                        UserId = artist.User.UserId,
                        UserName = artist.User.UserName,
                        ProfileImageUrl = artist.User.ProfileImageUrl,
                        PageRange = null,
                        CustomSettings = customSettings
                    });
                    added++;
                }
            }

            StatusMessage = $"Added {added} artists to download list";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load followed artists");
            await _dialogService.ShowMessageAsync("Error", $"Failed to load followed artists: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Helper method to create a SelectableArtist from FollowedArtist.
    /// </summary>
    private static SelectableArtist CreateSelectableArtist(FollowedArtist u, HashSet<string> existingIds)
    {
        return new SelectableArtist
        {
            User = new BookmarkedUser
            {
                UserId = u.UserId,
                UserName = u.UserName,
                ProfileImageUrl = u.ProfileImageUrl
            },
            IsSelected = false,
            IsAlreadyAdded = existingIds.Contains(u.UserId)
        };
    }

    /// <summary>
    /// Loads remaining artists in parallel for better performance.
    /// </summary>
    private async Task<List<SelectableArtist>> LoadRemainingArtistsAsync(
        string userId,
        HashSet<string> seen,
        HashSet<string> existingIds,
        int limit)
    {
        var result = new List<SelectableArtist>();
        var lockObj = new object();

        // Load both public and private in parallel
        var loadTasks = new List<Task>();

        foreach (var hidden in new[] { false, true })
        {
            loadTasks.Add(Task.Run(async () =>
            {
                var offset = limit; // Skip first page (already loaded)
                while (offset < 5000)
                {
                    try
                    {
                        var page = await _client.GetFollowedArtistsAsync(userId, offset, limit, hidden);
                        if (page.Users.Count == 0) break;

                        var pageArtists = new List<SelectableArtist>();
                        foreach (var u in page.Users)
                        {
                            if (!seen.Contains(u.UserId))
                            {
                                pageArtists.Add(CreateSelectableArtist(u, existingIds));
                            }
                        }

                        lock (lockObj)
                        {
                            foreach (var artist in pageArtists)
                            {
                                if (seen.Add(artist.User.UserId))
                                    result.Add(artist);
                            }
                        }

                        offset += page.Users.Count;
                        if (page.Total > 0 && offset >= page.Total) break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error loading page at offset {offset}: {ex.Message}");
                        break;
                    }
                }
            }));
        }

        await Task.WhenAll(loadTasks);
        return result;
    }

    [RelayCommand]
    private async Task AddArtistById()
    {
        if (ParsedArtistIds.Count == 0) return;

        int added = 0;
        int duplicates = 0;

        foreach (var id in ParsedArtistIds)
        {
            // Check for duplicates
            if (SelectedArtists.Any(a => a.UserId == id))
            {
                duplicates++;
                continue;
            }

            // Load custom settings if they exist
            var customSettings = await _artistSettingsRepository.GetByUserIdAsync(id);

            // Add with placeholder name — resolved in background below
            var entry = new SelectedArtist
            {
                UserId = id,
                UserName = $"Artist {id}",
                PageRange = null,
                CustomSettings = customSettings
            };
            SelectedArtists.Add(entry);
            added++;
        }

        NewArtistId = "";
        ParsedArtistIds.Clear();

        if (added > 0 && duplicates > 0)
            StatusMessage = $"Added {added} artists, skipped {duplicates} duplicates";
        else if (added > 0)
            StatusMessage = $"Added {added} artist(s)";
        else
            StatusMessage = "All artists already in list";

        // Resolve real names for newly-added placeholder entries in the background
        if (added > 0)
            _ = ResolveArtistNamesAsync(SelectedArtists
                .Where(a => a.UserName.StartsWith("Artist ") && a.UserName == $"Artist {a.UserId}")
                .ToList());
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        var clipboardId = QuickClipboardService.LastCopiedId;
        if (!string.IsNullOrEmpty(clipboardId))
        {
            NewArtistId = clipboardId;
            StatusMessage = $"Pasted {QuickClipboardService.LastCopiedType} ID: {clipboardId}";
        }
    }

    [RelayCommand]
    private async Task ImportArtistsFromFileAsync()
    {
        try
        {
            var content = await _filePicker.PickAndReadTextFileAsync("Import Artist List");
            if (string.IsNullOrEmpty(content)) return;

            var lines = content.Split('\n');
            var added = 0;
            var newEntries = new List<SelectedArtist>();

            foreach (var line in lines)
            {
                var id = line.Trim();
                if (string.IsNullOrEmpty(id)) continue;

                // Extract ID from URL if needed
                var match = Regex.Match(id, @"pixiv\.net.*users/(\d+)");
                if (match.Success) id = match.Groups[1].Value;

                if (!Regex.IsMatch(id, @"^\d+$")) continue;
                if (SelectedArtists.Any(a => a.UserId == id)) continue;

                var entry = new SelectedArtist { UserId = id, UserName = $"Artist {id}", PageRange = null };
                SelectedArtists.Add(entry);
                newEntries.Add(entry);
                added++;
            }

            StatusMessage = $"Imported {added} artists from file";
            if (newEntries.Count > 0) _ = ResolveArtistNamesAsync(newEntries);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to import artist list");
            await _dialogService.ShowMessageAsync("Error", $"Failed to import: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveArtist(SelectedArtist artist)
    {
        SelectedArtists.Remove(artist);
    }

    [RelayCommand]
    private void ClearArtists()
    {
        SelectedArtists.Clear();
    }

    private async Task ResolveArtistNamesAsync(List<SelectedArtist> entries)
    {
        var tasks = entries.Select(async entry =>
        {
            try
            {
                var info = await _client.GetArtistAsync(entry.UserId);
                if (info != null && !string.IsNullOrWhiteSpace(info.Name))
                    entry.UserName = info.Name;
            }
            catch { /* non-fatal */ }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void ClearInput()
    {
        NewArtistId = "";
        ParsedArtistIds.Clear();
    }

    [RelayCommand]
    private async Task ConfigureArtistSettingsAsync(SelectedArtist artist)
    {
        // Create a copy of the artist's settings or initialize from global
        var settings = artist.CustomSettings != null
            ? new SettingsOverride
            {
                UseGlobalSettings = artist.CustomSettings.UseGlobalSettings,
                DownloadRoot = artist.CustomSettings.DownloadRoot,
                FolderTemplate = artist.CustomSettings.FolderTemplate,
                FilenameTemplate = artist.CustomSettings.FilenameTemplate,
                FilenameMangaFormat = artist.CustomSettings.FilenameMangaFormat,
                FilenameInfoFormat = artist.CustomSettings.FilenameInfoFormat,
                DateFormat = artist.CustomSettings.DateFormat,
                TagsSeparator = artist.CustomSettings.TagsSeparator,
                CreateSubfolderPerSubmission = artist.CustomSettings.CreateSubfolderPerSubmission,
                SeparateR18Folder = artist.CustomSettings.SeparateR18Folder,
                OverwriteMode = artist.CustomSettings.OverwriteMode,
                BackupOldFile = artist.CustomSettings.BackupOldFile,
                MaxConcurrentDownloads = artist.CustomSettings.MaxConcurrentDownloads,
                MinFileSizeKB = artist.CustomSettings.MinFileSizeKB,
                MaxFileSizeKB = artist.CustomSettings.MaxFileSizeKB,
                DownloadTimeout = artist.CustomSettings.DownloadTimeout,
                RetryCount = artist.CustomSettings.RetryCount,
                AutoRetryFailedDownloads = artist.CustomSettings.AutoRetryFailedDownloads,
                MaxRetryAttempts = artist.CustomSettings.MaxRetryAttempts,
                RetryDelaySeconds = artist.CustomSettings.RetryDelaySeconds,
                DownloadDelaySeconds = artist.CustomSettings.DownloadDelaySeconds,
                FilterAiGenerated = artist.CustomSettings.FilterAiGenerated,
                IncludeTags = artist.CustomSettings.IncludeTags,
                ExcludeTagsFilter = artist.CustomSettings.ExcludeTagsFilter,
                DateFrom = artist.CustomSettings.DateFrom,
                DateTo = artist.CustomSettings.DateTo,
                WriteImageJSON = artist.CustomSettings.WriteImageJSON,
                WriteImageInfo = artist.CustomSettings.WriteImageInfo,
                WriteRawJSON = artist.CustomSettings.WriteRawJSON,
                IncludeSeriesJSON = artist.CustomSettings.IncludeSeriesJSON,
                WriteImageXMP = artist.CustomSettings.WriteImageXMP,
                VerifyImage = artist.CustomSettings.VerifyImage,
            }
            : SettingsOverride.FromGlobalSettings(_settingsService.Current);

        // Open dialog to configure settings
        var dialog = new Views.Dialogs.ArtistSettingsDialog(settings, artist.UserName);
        var ownerWindow = _dialogService.OwnerWindow;
        if (ownerWindow == null &&
            global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            ownerWindow = lifetime.MainWindow;
        if (ownerWindow == null) return;

        var result = await dialog.ShowDialog<bool>(ownerWindow);
        if (!result) return;

        // Save the settings
        artist.CustomSettings = dialog.Settings;
        await _artistSettingsRepository.SaveAsync(artist.UserId, artist.UserName, artist.CustomSettings);

        StatusMessage = $"Saved custom settings for {artist.UserName}";
    }

    #endregion

    #region Download Commands

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (SelectedArtists.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Artists", "Please add at least one artist to download.");
            return;
        }

        // Validate page range
        if (!PageRangeParser.IsValid(DefaultPageRange))
        {
            await _dialogService.ShowMessageAsync("Invalid Page Range", PageRangeParser.GetValidationError(DefaultPageRange));
            return;
        }

        IsLoading = true;
        StatusMessage = "Creating download job...";

        try
        {
            // Create targets with per-artist settings
            var targets = SelectedArtists.Select(a => new DownloadTarget
            {
                TargetId = a.UserId,
                Name = a.UserName,
                Type = TargetType.Artist,
                PageRange = UsePerArtistPageRanges ? a.PageRange : DefaultPageRange,
                CustomSettings = a.CustomSettings,
                ThumbnailUrl = a.ProfileImageUrl,
                UserName = a.UserName,
                UserId = a.UserId
            }).ToList();

            Logger.LogInformation("Created {TargetCount} targets from {ArtistCount} selected artists", targets.Count, SelectedArtists.Count);

            // Determine settings override
            var settingsOverride = UseGlobalSettings
                ? new SettingsOverride { UseGlobalSettings = true }
                : CustomSettings;

            // Create and start job
            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.Artist,
                BuildArtistJobName(SelectedArtists),
                targets,
                settingsOverride,
                startImmediately: true);

            CurrentJobId = job.Id;
            HasActiveJob = true;
            TotalTargets = targets.Count;
            CompletedTargets = 0;
            ProgressPercent = 0;

            // Subscribe to progress
            EstimatedItemCount = CalculateEstimatedItemCount();
            var progress = new Progress<JobProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                CompletedTargets = p.CompletedTargets;
                CompletedItemCount = (int)(EstimatedItemCount * p.PercentComplete / 100.0);
                CurrentTargetName = p.CurrentTargetName;
                ProgressStatusMessage = p.Message ?? $"Processing {p.CompletedTargets}/{p.TotalTargets}...";
                StatusMessage = p.Message ?? $"Processing {p.CompletedTargets}/{p.TotalTargets}...";

                if (p.Status == JobStatus.Completed || p.Status == JobStatus.Failed || p.Status == JobStatus.Cancelled)
                {
                    HasActiveJob = false;
                    IsLoading = false;
                }
            });

            _coordinator.SubscribeToProgress(job.Id, progress);

            StatusMessage = "Download started...";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start download");
            await _dialogService.ShowMessageAsync("Error", $"Failed to start download: {ex.Message}");
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CancelDownloadAsync()
    {
        if (CurrentJobId.HasValue)
        {
            await _coordinator.CancelJobAsync(CurrentJobId.Value);
            HasActiveJob = false;
            IsLoading = false;
            StatusMessage = "Download cancelled";
        }
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (SelectedArtists.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Artists", "Please add at least one artist to download.");
            return;
        }

        if (!PageRangeParser.IsValid(DefaultPageRange))
        {
            await _dialogService.ShowMessageAsync("Invalid Page Range", PageRangeParser.GetValidationError(DefaultPageRange));
            return;
        }

        try
        {
            var targets = SelectedArtists.Select(a => new DownloadTarget
            {
                TargetId = a.UserId,
                Name = a.UserName,
                Type = TargetType.Artist,
                PageRange = UsePerArtistPageRanges ? a.PageRange : DefaultPageRange,
                CustomSettings = a.CustomSettings,
                ThumbnailUrl = a.ProfileImageUrl,
                UserName = a.UserName
            }).ToList();

            var settingsOverride = UseGlobalSettings
                ? new SettingsOverride { UseGlobalSettings = true }
                : CustomSettings;

            var job = await _coordinator.CreateJobAsync(
                DownloadJobType.Artist,
                BuildArtistJobName(SelectedArtists),
                targets,
                settingsOverride,
                startImmediately: true);

            var started = job.Status == JobStatus.Running;
            StatusMessage = started
                ? $"Started downloading {SelectedArtists.Count} artists"
                : $"Added {SelectedArtists.Count} artists to queue";

            _notificationService.ShowNotification(
                started ? "Download Started" : "Added to Queue",
                started
                    ? $"{SelectedArtists.Count} artist{(SelectedArtists.Count == 1 ? "" : "s")} started downloading"
                    : $"{SelectedArtists.Count} artist{(SelectedArtists.Count == 1 ? "" : "s")} queued — will start when a slot is free",
                NotificationType.Info,
                SelectedArtists.FirstOrDefault()?.ProfileImageUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to queue download");
            await _dialogService.ShowMessageAsync("Error", $"Failed to queue: {ex.Message}");
        }
    }

    #endregion

    #region Preset Commands

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (SelectedArtists.Count == 0)
        {
            await _dialogService.ShowMessageAsync("Cannot Save", "Please add at least one artist before saving a preset.");
            return;
        }

        var dialog = new SavePresetDialog
        {
            DataContext = this
        };

        var ownerWindow = _dialogService.OwnerWindow;
        if (ownerWindow == null) return;
        var result = await dialog.ShowDialog<bool>(ownerWindow);
        if (!result)
            return;

        var preset = new DownloadPreset
        {
            Name = dialog.PresetName,
            Description = dialog.Description,
            Type = DownloadJobType.Artist,
            Settings = UseGlobalSettings ? null! : CustomSettings,
            DefaultPageRange = DefaultPageRange,
            UsePerArtistPageRanges = UsePerArtistPageRanges,
            Artists = SelectedArtists.Select(a => new PresetArtist
            {
                UserId = a.UserId,
                UserName = a.UserName,
                PageRange = a.PageRange
            }).ToList()
        };

        await _presetRepository.SaveAsync(preset);
        StatusMessage = $"Preset '{preset.Name}' saved";
    }

    [RelayCommand]
    private async Task LoadPresetAsync()
    {
        var presets = await _presetRepository.GetAllAsync();
        var artistPresets = presets.Where(p => p.Type == DownloadJobType.Artist).ToList();

        if (artistPresets.Count == 0)
        {
            await _dialogService.ShowMessageAsync("No Presets", "No saved presets found. Create one first!");
            return;
        }

        var dialog = new LoadPresetDialog(artistPresets)
        {
            DataContext = this
        };

        var ownerWindow = _dialogService.OwnerWindow;
        if (ownerWindow == null) return;
        var result = await dialog.ShowDialog<bool>(ownerWindow);
        if (!result || dialog.SelectedPreset == null)
            return;

        if (dialog.ShouldDelete)
        {
            await _presetRepository.DeleteAsync(dialog.SelectedPreset.Id);
            StatusMessage = $"Preset '{dialog.SelectedPreset.Name}' deleted";
            return;
        }

        // Load the preset
        var preset = dialog.SelectedPreset;
        DefaultPageRange = preset.DefaultPageRange ?? "0";
        UsePerArtistPageRanges = preset.UsePerArtistPageRanges;

        if (preset.Settings != null)
        {
            UseGlobalSettings = preset.Settings.UseGlobalSettings;
            CustomSettings = preset.Settings;
        }

        SelectedArtists.Clear();
        foreach (var artist in preset.Artists)
        {
            SelectedArtists.Add(new SelectedArtist
            {
                UserId = artist.UserId,
                UserName = artist.UserName,
                PageRange = artist.PageRange
            });
        }

        await _presetRepository.RecordUsageAsync(preset.Id);
        StatusMessage = $"Loaded preset '{preset.Name}' with {preset.Artists.Count} artists";
    }

    #endregion

    #region Helper Methods

    public void UpdateArtistPageRange(SelectedArtist artist, string pageRange)
    {
        artist.PageRange = pageRange;
    }

    #endregion

    private static string BuildArtistJobName(IReadOnlyList<SelectedArtist> artists)
    {
        if (artists.Count == 0) return "Download Artists";
        if (artists.Count == 1)
            return $"{artists[0].UserName} ({artists[0].UserId})";
        if (artists.Count <= 3)
            return string.Join(", ", artists.Select(a => a.UserName));
        var first3 = string.Join(", ", artists.Take(3).Select(a => a.UserName));
        return $"{first3} and {artists.Count - 3} others";
    }
}

/// <summary>
/// Represents an artist selected for download.
/// </summary>
public sealed partial class SelectedArtist : ObservableObject
{
    public string UserId { get; set; } = string.Empty;
    [ObservableProperty] private string _userName = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? PageRange { get; set; }

    /// <summary>
    /// Custom download settings for this specific artist.
    /// If null, uses job-level or global settings.
    /// </summary>
    public SettingsOverride? CustomSettings { get; set; }

    public bool HasCustomPageRange => !string.IsNullOrEmpty(PageRange);
    public string DisplayPageRange => PageRange ?? "Default";
    public bool HasCustomSettings => CustomSettings != null && !CustomSettings.UseGlobalSettings;
}

/// <summary>
/// ViewModel for artist selection dialog.
/// </summary>
public partial class ArtistSelectionViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<SelectableArtist> _artists = new();
    [ObservableProperty] private string _searchText = "";

    public List<BookmarkedUser> SelectedUsers =>
        Artists.Where(a => a.IsSelected).Select(a => a.User).ToList();

    public ArtistSelectionViewModel(IEnumerable<BookmarkedUser> users, ObservableCollection<SelectedArtist> alreadySelected)
    {
        var existingIds = alreadySelected.Select(a => a.UserId).ToHashSet();

        foreach (var user in users)
        {
            Artists.Add(new SelectableArtist
            {
                User = user,
                IsSelected = false,
                IsAlreadyAdded = existingIds.Contains(user.UserId)
            });
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        // Filter artists based on search text
        foreach (var artist in Artists)
        {
            artist.IsVisible = string.IsNullOrEmpty(value) ||
                               artist.User.UserName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                               artist.User.UserId.Contains(value);
        }
    }
}

public sealed partial class SelectableArtist : ObservableObject
{
    public required BookmarkedUser User { get; set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isAlreadyAdded;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private global::Avalonia.Media.Imaging.Bitmap? _avatarBitmap;

    /// <summary>Avatar URL for the artist.</summary>
    public string AvatarUrl => User.ProfileImageUrl;

    /// <summary>Load avatar image asynchronously.</summary>
    public async Task LoadAvatarAsync(System.Net.Http.HttpClient httpClient)
    {
        if (string.IsNullOrEmpty(AvatarUrl) || AvatarBitmap != null)
            return;

        try
        {
            var bytes = await httpClient.GetByteArrayAsync(AvatarUrl);
            using var stream = new MemoryStream(bytes);
            AvatarBitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch
        {
            // Ignore loading errors - will show placeholder
        }
    }
}

