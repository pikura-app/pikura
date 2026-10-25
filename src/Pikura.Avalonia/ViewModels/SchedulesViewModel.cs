using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public enum ScheduleTriggerMode { Daily, Startup, Once }

// ── A single artist staged in the Add-Schedule form ───────────────────────────
public sealed class StagedArtist
{
    public string UserId   { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string PageRange { get; set; } = "0";
    public string DisplayLabel => string.IsNullOrEmpty(UserName) ? UserId : $"{UserName} (ID {UserId})";
}

// ── Row VM shown in the schedule list ─────────────────────────────────────────
public partial class ScheduleRowViewModel : ObservableObject
{
    public DownloadSchedule Model { get; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isRunning;

    public string Name           => Model.Name;
    public string TypeLabel      => Model.Type switch
    {
        ScheduleType.SpecificArtists  => "Specific Artists",
        ScheduleType.FollowedArtists  => "Followed Artists",
        ScheduleType.DailyRankings    => "Daily Rankings",
        ScheduleType.Bookmarks        => "Bookmarks",
        _                             => Model.Type.ToString()
    };
    public string TriggerLabel   => Model.Trigger switch
    {
        ScheduleTrigger.DailyAtTime => $"Daily at {To12h(Model.TriggerHour, Model.TriggerMinute)}",
        ScheduleTrigger.Interval    => $"Every {Model.Interval?.TotalHours:N0} h",
        ScheduleTrigger.OnStartup   => "On Startup",
        ScheduleTrigger.Once        => $"Once — {Model.TriggerDateTime?.ToLocalTime():MMM d, yyyy h:mm tt}",
        _                           => Model.Trigger.ToString()
    };

    private static string To12h(int? hour24, int? minute)
    {
        if (hour24 == null || minute == null) return "—";
        var h = hour24.Value % 12;
        if (h == 0) h = 12;
        var ampm = hour24.Value < 12 ? "AM" : "PM";
        return $"{h}:{minute.Value:D2} {ampm}";
    }
    public string ArtistsSummary => Model.Artists.Count > 0
        ? string.Join(", ", Model.Artists.Take(3).Select(a => a.UserName)) +
          (Model.Artists.Count > 3 ? $" +{Model.Artists.Count - 3} more" : "")
        : string.Empty;
    public bool HasArtists       => Model.Artists.Count > 0;
    public string LastRun        => Model.LastRunAt.HasValue
        ? Model.LastRunAt.Value.ToLocalTime().ToString("MMM d, h:mm tt")
        : "Never";
    public string NextRun        => Model.NextRunAt.HasValue
        ? Model.NextRunAt.Value.ToLocalTime().ToString("MMM d, h:mm tt")
        : "—";
    public bool HasError         => !string.IsNullOrEmpty(Model.LastError);
    public string? LastError     => Model.LastError;

    public ScheduleRowViewModel(DownloadSchedule model)
    {
        Model      = model;
        _isEnabled = model.IsEnabled;
    }

    public void RefreshDisplayProperties()
    {
        OnPropertyChanged(nameof(LastRun));
        OnPropertyChanged(nameof(NextRun));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(LastError));
    }
}

// ── Main ViewModel ─────────────────────────────────────────────────────────────
public partial class SchedulesViewModel : ViewModelBase
{
    private readonly DownloadScheduleRepository _repo;
    private readonly PixivClient _client;
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogService;
    private readonly ScheduleExecutorService _executor;

    public ObservableCollection<ScheduleRowViewModel> Schedules { get; } = new();

    // ── Staging artist list (built before saving) ──────────────────────────
    public ObservableCollection<StagedArtist> StagedArtists { get; } = new();
    public bool HasStagedArtists => StagedArtists.Count > 0;

    // ── Add-form: artist entry ─────────────────────────────────────────────
    [ObservableProperty] private string _newArtistId   = string.Empty;
    [ObservableProperty] private string _newArtistName = string.Empty;
    [ObservableProperty] private bool   _isResolvingArtist;
    [ObservableProperty] private string _resolveError  = string.Empty;
    [ObservableProperty] private string _newScheduleName = string.Empty;

    // ── Trigger config ─────────────────────────────────────────────────────
    [ObservableProperty] private int    _triggerHour12  = 5;    // 1–12
    [ObservableProperty] private int    _triggerMinute  = 0;
    [ObservableProperty] private string _triggerAmPm    = "PM"; // "AM" or "PM"
    [ObservableProperty] private int    _checksPerDay   = 1;
    [ObservableProperty] private ScheduleTriggerMode _triggerMode = ScheduleTriggerMode.Daily;
    [ObservableProperty] private DateTime? _onceDateTime = DateTime.Now.AddDays(1);

    public bool IsTriggerDaily   { get => TriggerMode == ScheduleTriggerMode.Daily;   set { if (value) TriggerMode = ScheduleTriggerMode.Daily; } }
    public bool IsTriggerStartup { get => TriggerMode == ScheduleTriggerMode.Startup; set { if (value) TriggerMode = ScheduleTriggerMode.Startup; } }
    public bool IsTriggerOnce    { get => TriggerMode == ScheduleTriggerMode.Once;    set { if (value) TriggerMode = ScheduleTriggerMode.Once; } }

    partial void OnTriggerModeChanged(ScheduleTriggerMode value)
    {
        OnPropertyChanged(nameof(IsTriggerDaily));
        OnPropertyChanged(nameof(IsTriggerStartup));
        OnPropertyChanged(nameof(IsTriggerOnce));
    }

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(AddButtonLabel));

    /// <summary>Converts 12-h inputs to 0–23 hour for storage.</summary>
    private int TriggerHour24 =>
        TriggerAmPm == "AM"
            ? (TriggerHour12 == 12 ? 0 : TriggerHour12)
            : (TriggerHour12 == 12 ? 12 : TriggerHour12 + 12);

    public string[] AmPmOptions { get; } = { "AM", "PM" };

    // ── Per-schedule custom settings ─────────────────────────────────────────────────────
    [ObservableProperty] private bool   _useCustomSettings;
    [ObservableProperty] private string _customFolderTemplate   = string.Empty;
    [ObservableProperty] private string _customFilenameTemplate = string.Empty;
    [ObservableProperty] private string _pageRange = "0";

    // ── Content filters (always active, no need for UseCustomSettings toggle) ──────
    [ObservableProperty] private bool _filterSkipAi;
    [ObservableProperty] private bool _filterSkipManga;
    [ObservableProperty] private bool _filterSkipUgoira;
    [ObservableProperty] private bool _filterSkipR18;
    [ObservableProperty] private bool _filterSkipR18G;
    [ObservableProperty] private int  _filterDateLimitMode; // 0=None 1=SinceLastRun 2=Last1Day 3=Last7Days 4=Last30Days
    [ObservableProperty] private string _filterMaxArtworks  = string.Empty; // empty = no limit
    [ObservableProperty] private string _filterIncludeTags  = string.Empty;
    [ObservableProperty] private string _filterExcludeTags  = string.Empty;
    [ObservableProperty] private bool   _useCustomNaming;
    [ObservableProperty] private bool   _customSeparateR18;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool   _isBusy;

    // ── Edit state ────────────────────────────────────────────────────────────
    private Guid? _editingId;
    [ObservableProperty] private bool _isEditing;
    public string AddButtonLabel => IsEditing ? "💾 Save Changes" : "+ Add Schedule";

    public int[] Hour12Options  { get; } = Enumerable.Range(1, 12).ToArray();
    public int[] MinuteOptions  { get; } = { 0, 15, 30, 45 };

    public SchedulesViewModel(
        DownloadScheduleRepository repo,
        PixivClient client,
        SettingsService settingsService,
        DialogService dialogService,
        ScheduleExecutorService executor)
    {
        _repo            = repo;
        _client          = client;
        _settingsService = settingsService;
        _dialogService   = dialogService;
        _executor        = executor;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Schedules.Clear();
            var all = await _repo.GetAllAsync();
            foreach (var s in all)
                Schedules.Add(new ScheduleRowViewModel(s));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load schedules");
            StatusMessage = $"Error loading: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddArtistToStagingAsync()
    {
        if (string.IsNullOrWhiteSpace(NewArtistId)) return;
        IsResolvingArtist = true;
        ResolveError      = string.Empty;
        try
        {
            var id = ExtractId(NewArtistId.Trim());
            if (StagedArtists.Any(a => a.UserId == id))
            {
                ResolveError = "Artist already in the list.";
                return;
            }
            var profile = await _client.GetArtistAsync(id);
            var name = profile?.Name ?? id;
            StagedArtists.Add(new StagedArtist { UserId = id, UserName = name, PageRange = PageRange });
            OnPropertyChanged(nameof(HasStagedArtists));
            NewArtistId   = string.Empty;
            NewArtistName = string.Empty;
            StatusMessage = $"Added {name} to staging list ({StagedArtists.Count} artists).";
        }
        catch (Exception ex)
        {
            ResolveError = $"Could not resolve: {ex.Message}";
        }
        finally { IsResolvingArtist = false; }
    }

    [RelayCommand]
    private void RemoveArtistFromStaging(StagedArtist artist)
    {
        StagedArtists.Remove(artist);
        OnPropertyChanged(nameof(HasStagedArtists));
    }

    [RelayCommand]
    private void ClearStagedArtists()
    {
        StagedArtists.Clear();
        OnPropertyChanged(nameof(HasStagedArtists));
    }

    [RelayCommand]
    private async Task AddScheduleAsync()
    {
        if (StagedArtists.Count == 0)
        {
            await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_Validation"], Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_AddAtLeastOneArtistTo"]);
            return;
        }

        var scheduleName = string.IsNullOrWhiteSpace(NewScheduleName)
            ? (StagedArtists.Count == 1 ? $"{StagedArtists[0].UserName} — Auto" : $"{StagedArtists.Count} Artists — Auto")
            : NewScheduleName;

        // If editing, update the existing schedule in-place
        DownloadSchedule schedule;
        ScheduleRowViewModel? existingRow = null;
        if (IsEditing && _editingId.HasValue)
        {
            existingRow = Schedules.FirstOrDefault(r => r.Model.Id == _editingId.Value);
            schedule = existingRow?.Model ?? new DownloadSchedule();
        }
        else
        {
            schedule = new DownloadSchedule { IsEnabled = true };
        }

        // Always build a SettingsOverride so filters are stored even without naming overrides
        var customSettings = SettingsOverride.FromGlobalSettings(_settingsService.Current);
        customSettings.UseGlobalSettings = false;

        // Content-type filters
        customSettings.FilterAiGenerated = FilterSkipAi   ? true : (bool?)null;
        customSettings.SkipManga         = FilterSkipManga  ? true : (bool?)null;
        customSettings.SkipUgoira        = FilterSkipUgoira ? true : (bool?)null;
        customSettings.SkipR18           = FilterSkipR18    ? true : (bool?)null;
        customSettings.SkipR18G          = FilterSkipR18G   ? true : (bool?)null;
        customSettings.DateLimitMode = (ScheduleDateLimitMode)FilterDateLimitMode;
        if (int.TryParse(FilterMaxArtworks, out var maxArt) && maxArt > 0)
            customSettings.MaxArtworksPerArtist = maxArt;
        else
            customSettings.MaxArtworksPerArtist = null;

        // Tag filters
        if (!string.IsNullOrWhiteSpace(FilterIncludeTags))
            customSettings.IncludeTags = FilterIncludeTags.Trim();
        if (!string.IsNullOrWhiteSpace(FilterExcludeTags))
            customSettings.ExcludeTagsFilter = FilterExcludeTags.Trim();

        // Naming overrides (only if opted in)
        if (UseCustomNaming)
        {
            if (!string.IsNullOrWhiteSpace(CustomFolderTemplate))
                customSettings.FolderTemplate = CustomFolderTemplate;
            if (!string.IsNullOrWhiteSpace(CustomFilenameTemplate))
                customSettings.FilenameTemplate = CustomFilenameTemplate;
            customSettings.SeparateR18Folder = CustomSeparateR18;
        }

        schedule.Name     = scheduleName;
        schedule.Type     = ScheduleType.SpecificArtists;
        schedule.Artists  = StagedArtists.Select(a => new ScheduledArtist
        {
            UserId    = a.UserId,
            UserName  = a.UserName,
            PageRange = a.PageRange,
        }).ToList();
        schedule.PageRange = PageRange;
        schedule.Settings  = customSettings;

        if (TriggerMode == ScheduleTriggerMode.Startup)
        {
            schedule.Trigger = ScheduleTrigger.OnStartup;
        }
        else if (TriggerMode == ScheduleTriggerMode.Once)
        {
            if (OnceDateTime == null)
            {
                await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_Validation"], Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_PickADateAndTimeFor"]);
                return;
            }
            // Merge the chosen date with the chosen time
            var d = OnceDateTime.Value.Date;
            var combined = new DateTime(d.Year, d.Month, d.Day, TriggerHour24, TriggerMinute, 0, DateTimeKind.Local);
            if (combined <= DateTime.Now)
            {
                await _dialogService.ShowMessageAsync(Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_Validation"],
                    $"The chosen date and time ({combined:MMM d, yyyy h:mm tt}) is in the past. Please pick a future time.");
                return;
            }
            schedule.Trigger         = ScheduleTrigger.Once;
            schedule.TriggerDateTime = combined.ToUniversalTime();
        }
        else if (ChecksPerDay == 1)
        {
            schedule.Trigger       = ScheduleTrigger.DailyAtTime;
            schedule.TriggerHour   = TriggerHour24;
            schedule.TriggerMinute = TriggerMinute;
        }
        else
        {
            schedule.Trigger       = ScheduleTrigger.Interval;
            schedule.Interval      = TimeSpan.FromHours(24.0 / ChecksPerDay);
            schedule.TriggerHour   = TriggerHour24;
            schedule.TriggerMinute = TriggerMinute;
        }

        await _repo.SaveAsync(schedule);

        if (IsEditing && existingRow != null)
        {
            existingRow.RefreshDisplayProperties();
            StatusMessage = $"Schedule '{scheduleName}' updated.";
            _editingId = null;
            IsEditing  = false;
        }
        else
        {
            Schedules.Insert(0, new ScheduleRowViewModel(schedule));
            StatusMessage = $"Schedule '{scheduleName}' added ({StagedArtists.Count} artists).";
        }

        StagedArtists.Clear();
        OnPropertyChanged(nameof(HasStagedArtists));
        NewScheduleName = string.Empty;
        UseCustomSettings = false;
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(ScheduleRowViewModel row)
    {
        row.Model.IsEnabled = row.IsEnabled;
        await _repo.SaveAsync(row.Model);
        StatusMessage = $"{row.Name} {(row.IsEnabled ? "enabled" : "disabled")}.";
    }

    [RelayCommand]
    private async Task RunNowAsync(ScheduleRowViewModel row)
    {
        row.IsRunning = true;
        try
        {
            StatusMessage = $"Running '{row.Name}'\u2026";
            await _executor.ExecuteOneAsync(row.Model);
            row.Model.LastRunAt = DateTime.UtcNow;
            row.RefreshDisplayProperties();
            StatusMessage = $"'{row.Name}' completed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Run failed: {ex.Message}";
        }
        finally
        {
            row.IsRunning = false;
        }
    }

    [RelayCommand]
    private void BeginEdit(ScheduleRowViewModel row)
    {
        var s = row.Model;
        _editingId = s.Id;
        IsEditing  = true;

        NewScheduleName = s.Name;

        // Artists
        StagedArtists.Clear();
        foreach (var a in s.Artists)
            StagedArtists.Add(new StagedArtist { UserId = a.UserId, UserName = a.UserName, PageRange = a.PageRange ?? "0" });
        OnPropertyChanged(nameof(HasStagedArtists));

        // Trigger
        switch (s.Trigger)
        {
            case ScheduleTrigger.OnStartup:
                TriggerMode = ScheduleTriggerMode.Startup;
                break;
            case ScheduleTrigger.Once:
                TriggerMode   = ScheduleTriggerMode.Once;
                OnceDateTime  = s.TriggerDateTime?.ToLocalTime();
                break;
            case ScheduleTrigger.DailyAtTime:
                TriggerMode    = ScheduleTriggerMode.Daily;
                ChecksPerDay   = 1;
                var h = s.TriggerHour ?? 17;
                TriggerAmPm    = h >= 12 ? "PM" : "AM";
                TriggerHour12  = h == 0 ? 12 : h > 12 ? h - 12 : h;
                TriggerMinute  = s.TriggerMinute ?? 0;
                break;
            case ScheduleTrigger.Interval:
                TriggerMode   = ScheduleTriggerMode.Daily;
                ChecksPerDay  = s.Interval.HasValue ? Math.Max(1, (int)Math.Round(24.0 / s.Interval.Value.TotalHours)) : 1;
                break;
        }

        // Filters
        var cfg = s.Settings;
        FilterSkipAi     = cfg?.FilterAiGenerated == true;
        FilterSkipManga  = cfg?.SkipManga         == true;
        FilterSkipUgoira = cfg?.SkipUgoira        == true;
        FilterSkipR18    = cfg?.SkipR18           == true;
        FilterSkipR18G   = cfg?.SkipR18G          == true;
        FilterDateLimitMode = (int)(cfg?.DateLimitMode ?? ScheduleDateLimitMode.None);
        FilterMaxArtworks  = cfg?.MaxArtworksPerArtist is > 0 ? cfg.MaxArtworksPerArtist.Value.ToString() : string.Empty;
        FilterIncludeTags = cfg?.IncludeTags       ?? string.Empty;
        FilterExcludeTags = cfg?.ExcludeTagsFilter ?? string.Empty;

        // Naming overrides
        UseCustomNaming       = !string.IsNullOrWhiteSpace(cfg?.FolderTemplate) || !string.IsNullOrWhiteSpace(cfg?.FilenameTemplate);
        CustomFolderTemplate  = cfg?.FolderTemplate   ?? string.Empty;
        CustomFilenameTemplate = cfg?.FilenameTemplate ?? string.Empty;
        CustomSeparateR18     = cfg?.SeparateR18Folder == true;

        PageRange = s.PageRange ?? "0";

        StatusMessage = $"Editing '{s.Name}' — make changes then click Save.";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _editingId = null;
        IsEditing  = false;
        NewScheduleName = string.Empty;
        StagedArtists.Clear();
        OnPropertyChanged(nameof(HasStagedArtists));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void AddNew()
    {
        _editingId = null;
        IsEditing = true;
        NewScheduleName = string.Empty;
        StagedArtists.Clear();
        // Reset to defaults
        IsTriggerDaily = true;
        TriggerHour12 = 9;
        TriggerMinute = 0;
        TriggerAmPm = "AM";
        ChecksPerDay = 1;
        FilterSkipAi = false;
        FilterSkipManga = false;
        FilterSkipUgoira = false;
        FilterSkipR18 = false;
        FilterSkipR18G = false;
        FilterDateLimitMode = 0;
        FilterMaxArtworks = string.Empty;
        FilterIncludeTags = string.Empty;
        FilterExcludeTags = string.Empty;
        UseCustomNaming = false;
        CustomFolderTemplate = string.Empty;
        CustomFilenameTemplate = string.Empty;
        CustomSeparateR18 = false;
        OnPropertyChanged(nameof(HasStagedArtists));
        OnPropertyChanged(nameof(AddButtonLabel));
        StatusMessage = Pikura.Avalonia.Localization.LocalizationService.Instance["Batch_CreateANewScheduleAddArtists"];
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduleRowViewModel row)
    {
        var ok = await _dialogService.ShowConfirmationAsync(
            "Delete Schedule", $"Remove '{row.Name}'?");
        if (!ok) return;
        await _repo.DeleteAsync(row.Model.Id);
        Schedules.Remove(row);
        StatusMessage = $"Removed '{row.Name}'.";
    }

    public Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private static string ExtractId(string input)
    {
        var m = System.Text.RegularExpressions.Regex.Match(input, @"users/(\d+)");
        return m.Success ? m.Groups[1].Value : input;
    }
}
