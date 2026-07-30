using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using Pikura.Avalonia.Services;

namespace Pikura.Avalonia.ViewModels;

public partial class HistoryViewModel : ViewModelBase
{
    private readonly DownloadJobRepository _jobRepository;
    private readonly DownloadCoordinator _coordinator;
    private readonly DialogService _dialogService;
    private readonly PixivImageLoader _imageLoader;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private ObservableCollection<DownloadJobViewModel> _activeJobs = new();
    [ObservableProperty] private ObservableCollection<DownloadJobViewModel> _completedJobs = new();
    [ObservableProperty] private ObservableCollection<DownloadJobViewModel> _failedJobs = new();
    [ObservableProperty] private ObservableCollection<DownloadJobViewModel> _cancelledJobs = new();

    // Date-grouped, virtualization-friendly views for the archival tabs. The flat
    // source collections above remain the source of truth (counts, empty-state,
    // add/remove logic); these derived views interleave collapsible date headers
    // with their jobs for display in a single virtualizing ListBox.
    private readonly HistoryTabGrouping _completedGrouping;
    private readonly HistoryTabGrouping _failedGrouping;
    private readonly HistoryTabGrouping _cancelledGrouping;
    public ObservableCollection<object> CompletedView => _completedGrouping.View;
    public ObservableCollection<object> FailedView => _failedGrouping.View;
    public ObservableCollection<object> CancelledView => _cancelledGrouping.View;

    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _activeListView;
    [ObservableProperty] private bool _completedListView;
    [ObservableProperty] private bool _failedListView;
    [ObservableProperty] private bool _cancelledListView;

    public string ActiveSummary
    {
        get
        {
            var running = ActiveJobs.Count(j => j.Job.Status == JobStatus.Running);
            var paused  = ActiveJobs.Count(j => j.Job.Status == JobStatus.Paused);
            var queued  = ActiveJobs.Count(j => j.Job.Status == JobStatus.Pending);

            // Artwork-level progress — seeded from DB on construction, updated live via progress
            var artworksDone  = ActiveJobs.Sum(j => j.CompletedArtworks);
            var artworksTotal = ActiveJobs.Sum(j => j.TotalArtworks);

            // Artist/target-level progress (Job.Targets gives the authoritative list)
            var artistsTotal = ActiveJobs.Sum(j => j.Job.Targets.Count);
            var artistsDone  = ActiveJobs.Sum(j => j.Job.Targets.Count(t =>
                t.Status == TargetStatus.Completed || t.Status == TargetStatus.Failed || t.Status == TargetStatus.Skipped));

            var parts = new List<string>();
            if (running > 0) parts.Add($"{running} running");
            if (paused > 0)  parts.Add($"{paused} paused");
            if (queued > 0)  parts.Add($"{queued} queued");
            var status = string.Join(" · ", parts);

            var details = new List<string>();
            // Only show artist count when there are multiple targets worth tracking
            if (artistsTotal > 1)
                details.Add($"{artistsDone}/{artistsTotal} artists");
            if (artworksTotal > 0)
                details.Add($"{artworksDone}/{artworksTotal} artworks");

            var totalBytes = ActiveJobs.Sum(j => j.TotalDownloadedBytes);
            if (totalBytes > 0)
                details.Add(DownloadJobViewModel.FormatBytesStatic(totalBytes));

            var detailStr = details.Count > 0 ? " · " + string.Join(" · ", details) : "";
            return $"{status}{detailStr}";
        }
    }

    // When true, CollectionChanged-triggered regroups are deferred (used during
    // bulk LoadJobsAsync population so we regroup once instead of per-add).
    private bool _suppressRegroup;

    public HistoryViewModel(DownloadJobRepository jobRepository, DownloadCoordinator coordinator, DialogService dialogService, PixivImageLoader imageLoader, SettingsService settingsService)
    {
        _jobRepository = jobRepository;
        _coordinator = coordinator;
        _dialogService = dialogService;
        _imageLoader = imageLoader;
        _settingsService = settingsService;

        _completedGrouping = new HistoryTabGrouping(_completedJobs, useCompletedDate: true);
        _failedGrouping    = new HistoryTabGrouping(_failedJobs, useCompletedDate: true);
        _cancelledGrouping = new HistoryTabGrouping(_cancelledJobs, useCompletedDate: true);

        coordinator.JobStarted  += OnJobStarted;
        coordinator.JobCompleted += OnJobCompleted;
        coordinator.JobCreated   += OnJobCreated;
        _activeJobs.CollectionChanged    += (_, _) => { UpdateQueuePositions(); OnPropertyChanged(nameof(ActiveSummary)); };
        _completedJobs.CollectionChanged += (_, _) => RegroupCompleted();
        _failedJobs.CollectionChanged    += (_, _) => RegroupFailed();
        _cancelledJobs.CollectionChanged += (_, _) => RegroupCancelled();
    }

    private void RegroupCompleted() { if (!_suppressRegroup) _completedGrouping.Regroup(); }
    private void RegroupFailed()    { if (!_suppressRegroup) _failedGrouping.Regroup(); }
    private void RegroupCancelled() { if (!_suppressRegroup) _cancelledGrouping.Regroup(); }

    private void UpdateQueuePositions()
    {
        for (int i = 0; i < ActiveJobs.Count; i++)
            ActiveJobs[i].QueuePosition = i + 1;
    }

    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadJobsAsync();
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        var ids = CompletedJobs.Select(j => j.Job.Id).ToList();
        foreach (var id in ids) await _jobRepository.DeleteJobAsync(id);
        CompletedJobs.Clear();
    }

    [RelayCommand]
    private async Task ClearFailedAsync()
    {
        var ids = FailedJobs.Select(j => j.Job.Id).ToList();
        foreach (var id in ids) await _jobRepository.DeleteJobAsync(id);
        FailedJobs.Clear();
    }

    [RelayCommand]
    private async Task ClearCancelledAsync()
    {
        var ids = CancelledJobs.Select(j => j.Job.Id).ToList();
        foreach (var id in ids) await _jobRepository.DeleteJobAsync(id);
        CancelledJobs.Clear();
    }

    [RelayCommand]
    private async Task RemoveJobAsync(DownloadJobViewModel jobVm)
    {
        await _jobRepository.DeleteJobAsync(jobVm.Job.Id);
        CompletedJobs.Remove(jobVm);
        FailedJobs.Remove(jobVm);
        CancelledJobs.Remove(jobVm);
    }

    [RelayCommand]
    private async Task CancelJobAsync(DownloadJobViewModel jobVm)
    {
        await _coordinator.CancelJobAsync(jobVm.Job.Id);
    }

    /// <summary>Removes a job from the active queue entirely: cancels it if running and
    /// deletes it from the database (it does NOT move to the Cancelled tab).</summary>
    [RelayCommand]
    private async Task RemoveActiveJobAsync(DownloadJobViewModel jobVm)
    {
        // DeleteJobAsync cancels any running task, waits briefly, then deletes from DB.
        await _coordinator.DeleteJobAsync(jobVm.Job.Id);
        ActiveJobs.Remove(jobVm);
        // The cancel that DeleteJobAsync triggers may have routed the job into the
        // Cancelled list via the progress event — remove it there too so it's fully gone.
        var stray = CancelledJobs.FirstOrDefault(j => j.Job.Id == jobVm.Job.Id);
        if (stray != null) CancelledJobs.Remove(stray);
    }

    [RelayCommand]
    private async Task RetryJobAsync(DownloadJobViewModel jobVm)
    {
        var retryable = jobVm.Job.Status == JobStatus.Failed
                     || jobVm.Job.Status == JobStatus.Cancelled
                     || jobVm.HasFailedItems;
        if (!retryable) return;
        try
        {
            jobVm.Job.Status = JobStatus.Pending;
            jobVm.Job.LastRetriedAt = DateTime.UtcNow;
            jobVm.Job.RetryCount++;

            foreach (var target in jobVm.Job.Targets.Where(
                t => t.Status == TargetStatus.Failed || t.Status == TargetStatus.Cancelled))
            {
                target.Status = TargetStatus.Pending;
                target.ErrorMessage = null;
            }

            await _jobRepository.SaveJobAsync(jobVm.Job);
            FailedJobs.Remove(jobVm);
            CancelledJobs.Remove(jobVm);

            // Add to ActiveJobs so it appears in the queue immediately
            if (!ActiveJobs.Any(j => j.Job.Id == jobVm.Job.Id))
            {
                jobVm.OnReordered = () => Dispatcher.UIThread.InvokeAsync(LoadJobsAsync);
                var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, jobVm));
                _coordinator.SubscribeToProgress(jobVm.Job.Id, progressHandler);
                ActiveJobs.Insert(0, jobVm);
            }

            // Update UI status immediately
            jobVm.UpdateStatus();

            // Try to start - coordinator will handle concurrent limit
            await _coordinator.StartJobAsync(jobVm.Job.Id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Retry failed: {ex.Message}");
        }
    }

    public async Task PersistActiveJobOrderAsync(IReadOnlyList<Guid> orderedIds)
    {
        for (int i = 0; i < orderedIds.Count; i++)
            await _coordinator.SetJobSortOrderAsync(orderedIds[i], i);
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var job in ActiveJobs.ToList())
            await job.PauseCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        // Resume Paused jobs and force-start Pending (queued) jobs
        foreach (var job in ActiveJobs.ToList().Where(j => j.Job.Status is JobStatus.Paused or JobStatus.Pending))
            await job.ResumeCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void SetActiveListView(string value) => ActiveListView = value == "true";

    [RelayCommand]
    private void SetCompletedListView(string value) => CompletedListView = value == "true";

    [RelayCommand]
    private void SetFailedListView(string value) => FailedListView = value == "true";

    [RelayCommand]
    private void SetCancelledListView(string value) => CancelledListView = value == "true";

    [RelayCommand]
    private async Task RetryAllFailedAsync()
    {
        var jobs = FailedJobs.ToList();
        if (jobs.Count == 0) return;

        var confirmed = await _dialogService.ShowConfirmationAsync(
            "Retry All Failed",
            $"Retry {jobs.Count} failed jobs?");
        if (!confirmed) return;

        foreach (var jobVm in jobs)
            await RetryJobAsync(jobVm);
    }

    private void OnJobCreated(object? sender, JobCompletedEventArgs e)
    {
        // New job was queued — reload so it appears in the Active tab immediately.
        Dispatcher.UIThread.Post(() => _ = LoadJobsAsync());
    }

    private void OnJobStarted(object? sender, JobCompletedEventArgs e)
    {
        void AddToActive()
        {
            var existing = ActiveJobs.FirstOrDefault(j => j.Job.Id == e.Job.Id);
            if (existing != null)
            {
                // Reuse the existing VM (preserves progress counters) — update in-place, no reorder
                existing.IsPausable    = true;
                existing.IsResumable   = false;
                existing.IsCancellable = true;
                existing.StatusText    = "▶ Running";
                existing.Job.Status    = JobStatus.Running;
                existing.RefreshStatusIndicators();
                // Re-subscribe progress so events flow again after resume
                var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, existing));
                _coordinator.SubscribeToProgress(e.Job.Id, progressHandler);
            }
            else
            {
                // Truly new job — create fresh VM
                var jobVm = new DownloadJobViewModel(e.Job, _imageLoader, _coordinator, _settingsService)
                    { OnReordered = () => Dispatcher.UIThread.InvokeAsync(LoadJobsAsync) };
                var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, jobVm));
                _coordinator.SubscribeToProgress(e.Job.Id, progressHandler);
                ActiveJobs.Insert(0, jobVm);
            }
        }
        if (Dispatcher.UIThread.CheckAccess())
            AddToActive();
        else
            Dispatcher.UIThread.Post(AddToActive, global::Avalonia.Threading.DispatcherPriority.Send);
    }

    private void OnJobCompleted(object? sender, JobCompletedEventArgs e)
    {
        Console.Error.WriteLine($"[History] OnJobCompleted: {e.Job.Id} '{e.Job.Name}' status={e.Job.Status}");
        var job = e.Job;
        void Route()
        {
            Console.Error.WriteLine($"[History] Route: status={job.Status} activeCount={ActiveJobs.Count}");
            var active = ActiveJobs.FirstOrDefault(j => j.Job.Id == job.Id);
            
            switch (job.Status)
            {
                case JobStatus.Paused:
                    // For paused jobs, update the existing VM in place instead of removing/recreating
                    if (active != null)
                    {
                        active.IsPausable = false;
                        active.IsResumable = true;
                        active.IsCancellable = true;
                        active.StatusText = "⏸ Paused";
                        active.Job.Status = JobStatus.Paused;
                        active.RefreshStatusIndicators();
                        // Move it after all Running jobs
                        ActiveJobs.Remove(active);
                        var insertIdx = ActiveJobs.Count(j => j.Job.Status == JobStatus.Running);
                        ActiveJobs.Insert(insertIdx, active);
                    }
                    break;
                    
                case JobStatus.Completed:
                case JobStatus.Failed:
                case JobStatus.Cancelled:
                    // Remove from active and move to appropriate list
                    var liveBytes = active?.TotalDownloadedBytes ?? 0;
                    if (active != null) ActiveJobs.Remove(active);
                    var vm = new DownloadJobViewModel(job, _imageLoader, coordinator: null, settingsService: _settingsService)
                        { OnReordered = null };
                    // Prefer the live-accumulated byte count over the DB value, which may
                    // lag behind if DownloadedBytes wasn't flushed for every artwork.
                    if (liveBytes > vm.TotalDownloadedBytes)
                        vm.TotalDownloadedBytes = liveBytes;

                    if (job.Status == JobStatus.Completed && !CompletedJobs.Any(j => j.Job.Id == job.Id))
                        CompletedJobs.Insert(0, vm);
                    else if (job.Status == JobStatus.Failed && !FailedJobs.Any(j => j.Job.Id == job.Id))
                        FailedJobs.Insert(0, vm);
                    else if (job.Status == JobStatus.Cancelled && !CancelledJobs.Any(j => j.Job.Id == job.Id))
                        CancelledJobs.Insert(0, vm);
                    break;
            }
            Console.Error.WriteLine($"[History] After Route: completed={CompletedJobs.Count} failed={FailedJobs.Count}");
        }
        if (Dispatcher.UIThread.CheckAccess()) Route();
        else Dispatcher.UIThread.Post(Route, global::Avalonia.Threading.DispatcherPriority.Normal);
    }

    private void OnProgressReceived(JobProgress progress, DownloadJobViewModel jobVm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            jobVm.ApplyProgress(progress);
            // Keep the header summary live as artworks complete
            OnPropertyChanged(nameof(ActiveSummary));

            // Move cancelled jobs out of Active list immediately
            // (Paused jobs are handled by OnJobCompleted to avoid duplicate processing)
            if (progress.Status == JobStatus.Cancelled)
            {
                var active = ActiveJobs.FirstOrDefault(j => j.Job.Id == progress.JobId);
                if (active != null)
                {
                    ActiveJobs.Remove(active);
                    active.Job.Status = progress.Status;
                    active.Job.CompletedAt = DateTime.UtcNow;
                    if (!CancelledJobs.Any(j => j.Job.Id == active.Job.Id))
                        CancelledJobs.Insert(0, active);
                }
            }
        });
    }

    public Task ReloadAsync() => LoadJobsAsync();

    private void LoadJobs()
    {
        _ = LoadJobsAsync();
    }

    private async Task LoadJobsAsync()
    {
        var activeJobs = await _jobRepository.GetAllActiveJobsAsync();

        ActiveJobs.Clear();
        CompletedJobs.Clear();
        FailedJobs.Clear();
        CancelledJobs.Clear();

        // Honor the user's explicit queue order (drag-to-reorder / Move commands persist
        // SortOrder). CreatedAt is only a tiebreaker for jobs never manually reordered,
        // giving FIFO order. This lets a running job be freely repositioned among queued
        // ones and have that order survive reloads, instead of snapping back to the top.
        var sortedActive = activeJobs
            .OrderBy(j => j.SortOrder)
            .ThenBy(j => j.CreatedAt);

        foreach (var job in sortedActive)
        {
            var jobVm = new DownloadJobViewModel(job, _imageLoader, _coordinator, _settingsService)
                { OnReordered = () => Dispatcher.UIThread.InvokeAsync(LoadJobsAsync) };
            var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, jobVm));
            _coordinator.SubscribeToProgress(job.Id, progressHandler);
            ActiveJobs.Add(jobVm);
        }

        // Load completed/failed/cancelled separately (these are terminal states)
        var completedJobs = await _jobRepository.GetJobsAsync(status: JobStatus.Completed);
        var failedJobs = await _jobRepository.GetJobsAsync(status: JobStatus.Failed);
        var cancelledJobs = await _jobRepository.GetJobsAsync(status: JobStatus.Cancelled);

        _suppressRegroup = true;
        try
        {
            foreach (var job in completedJobs.OrderByDescending(j => j.CompletedAt))
            {
                var vm = new DownloadJobViewModel(job, _imageLoader, settingsService: _settingsService);
                CompletedJobs.Add(vm);
            }
            foreach (var job in failedJobs.OrderByDescending(j => j.CompletedAt))
            {
                var vm = new DownloadJobViewModel(job, _imageLoader, settingsService: _settingsService);
                FailedJobs.Add(vm);
            }
            foreach (var job in cancelledJobs.OrderByDescending(j => j.CompletedAt))
            {
                var vm = new DownloadJobViewModel(job, _imageLoader, settingsService: _settingsService);
                CancelledJobs.Add(vm);
            }
        }
        finally
        {
            _suppressRegroup = false;
        }
        _completedGrouping.Regroup();
        _failedGrouping.Regroup();
        _cancelledGrouping.Regroup();
    }
}

public partial class DownloadJobViewModel : ObservableObject
{
    public DownloadJob Job { get; }

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _hasFailedItems;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string? _currentTargetName;
    [ObservableProperty] private string? _currentArtist;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _completedArtworks;
    [ObservableProperty] private int _totalArtworks;
    [ObservableProperty] private long _totalDownloadedBytes;

    public string? DownloadedSizeText => TotalDownloadedBytes > 0 ? FormatBytes(TotalDownloadedBytes) : null;

    partial void OnTotalDownloadedBytesChanged(long value) => OnPropertyChanged(nameof(DownloadedSizeText));

    public static string FormatBytesStatic(long bytes) => FormatBytes(bytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F2} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
    [ObservableProperty] private string? _currentFileLabel;
    [ObservableProperty] private double _currentFilePct;
    [ObservableProperty] private Bitmap? _currentFileThumbnail;
    [ObservableProperty] private bool _hasCurrentFile;
    [ObservableProperty] private string? _speedText;
    [ObservableProperty] private string? _etaText;

    private string? _lastThumbnailUrl;
    private PixivImageLoader? _imageLoader;
    private DownloadCoordinator? _coordinator;
    private SettingsService? _settingsService;

    [ObservableProperty] private bool _isCancellable;
    [ObservableProperty] private bool _isPausable;
    [ObservableProperty] private bool _isResumable;
    [ObservableProperty] private int _queuePosition;

    public bool HasOutputFolder => !string.IsNullOrWhiteSpace(ResolvedOutputFolder)
                                   && Directory.Exists(ResolvedOutputFolder);

    /// <summary>Hex color for the status accent bar: Running=blue, Paused=amber, Pending=gray.</summary>
    public string StatusAccentColor => Job.Status switch
    {
        JobStatus.Running => "#4FC3F7",
        JobStatus.Paused  => "#FFB74D",
        JobStatus.Pending => "#90A4AE",
        JobStatus.Completed => "#81C784",
        JobStatus.Failed    => "#E57373",
        JobStatus.Cancelled => "#B0BEC5",
        _ => "#90A4AE"
    };

    /// <summary>Summary line shown under the job name in compact mode.</summary>
    public string CompactSummary => $"{StatusText}  ·  {CompletedCount}/{TotalCount}";

    private string? ResolvedOutputFolder
    {
        get
        {
            var downloadRoot = _settingsService?.Current.DownloadRoot;

            // The user id for an Artist target lives in TargetId; for other target types
            // it's in UserId. Collect every plausible artist id from both so old jobs
            // (which often never populated UserId) still resolve.
            var artistIds = Job.Targets
                .SelectMany(t => new[]
                {
                    t.UserId,
                    t.Type == TargetType.Artist ? t.TargetId : null
                })
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct()
                .ToList();

            // Multi-artist job: targets span more than one distinct artist → open DownloadRoot
            if (artistIds.Count > 1)
                return !string.IsNullOrWhiteSpace(downloadRoot) && Directory.Exists(downloadRoot)
                    ? downloadRoot : null;

            // Single-artist: use stored OutputFolder if available
            if (!string.IsNullOrWhiteSpace(Job.OutputFolder))
                return ResolveArtistRootFolder(Job.OutputFolder, downloadRoot);

            // Fallback: search DownloadRoot for the artist folder (covers previously-completed
            // jobs that never persisted an OutputFolder). The default folder template is
            // "%artist% (%member_id%)", so match on the artist id (most reliable), the display
            // name, or the artist-name prefix parsed from the job name (e.g. "Yuki: 21 artworks").
            if (string.IsNullOrWhiteSpace(downloadRoot) || !Directory.Exists(downloadRoot)) return null;
            var artistId   = artistIds.FirstOrDefault();
            var artistName = Job.Targets.FirstOrDefault()?.UserName;
            var nameFromJob = ParseArtistNameFromJobName(Job.Name);
            if (string.IsNullOrWhiteSpace(artistId)
                && string.IsNullOrWhiteSpace(artistName)
                && string.IsNullOrWhiteSpace(nameFromJob)) return null;

            bool Matches(string dir)
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(artistId) && name.Contains(artistId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(artistName) && name.Contains(artistName, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(nameFromJob) && name.Contains(nameFromJob, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            try
            {
                // Search top-level first
                var topLevel = Directory.EnumerateDirectories(downloadRoot, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(Matches);
                if (topLevel != null) return topLevel;

                // Search one level deeper (in case artists are grouped in subfolders)
                foreach (var subDir in Directory.EnumerateDirectories(downloadRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    var found = Directory.EnumerateDirectories(subDir, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(Matches);
                    if (found != null) return found;
                }

                // If still not found, return downloadRoot as last resort
                return downloadRoot;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Extracts a likely artist-name portion from a job name such as
    /// "Yuki: 21 artworks" → "Yuki" or "Weber老師 (56899141)" → "Weber老師".
    /// Returns null when nothing usable can be parsed.
    /// </summary>
    private static string? ParseArtistNameFromJobName(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return null;
        var name = jobName.Trim();
        // Strip a trailing " (123456)" id segment.
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) name = name[..paren];
        // Take the part before a ":" count suffix ("Yuki: 21 artworks").
        var colon = name.IndexOf(':');
        if (colon > 0) name = name[..colon];
        name = name.Trim();
        // Ignore generic placeholders that won't match a folder.
        if (name.Length < 2 || name.StartsWith("Download", StringComparison.OrdinalIgnoreCase))
            return null;
        return name;
    }

    public bool HasThumbnail => Thumbnail != null;
    public bool HasAnyThumbnail => Thumbnail != null || CurrentFileThumbnail != null;

    partial void OnThumbnailChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasAnyThumbnail));
    }

    partial void OnCurrentFileThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAnyThumbnail));

    /// <summary>Total artworks/images actually downloaded across every target in the job
    /// (e.g. summed over all artists in a multi-artist job).</summary>
    public int TotalArtworksDownloaded => Job.Targets.Sum(t => t.DownloadedItems);

    /// <summary>Right-column count line: artwork progress when known, else artist/target count.</summary>
    public string TargetSummaryText
    {
        get
        {
            if (TotalArtworks > 0)
                return $"{CompletedArtworks} / {TotalArtworks} artworks";
            var t = Job.Targets.Count;
            return t == 1 ? "1 artist" : $"{t} artists";
        }
    }

    /// <summary>
    /// True only while the job is actively enumerating its artwork list (Running with no
    /// known total yet). Used to drive the "Preparing…" hint + indeterminate bar so they
    /// don't keep spinning once the job is Paused/Pending.
    /// </summary>
    public bool IsPreparing => Job.Status == JobStatus.Running && TotalCount == 0 && TotalArtworks == 0;

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(IsPreparing));
    partial void OnTotalArtworksChanged(int value)   { OnPropertyChanged(nameof(IsPreparing)); OnPropertyChanged(nameof(TargetSummaryText)); }
    partial void OnCompletedArtworksChanged(int value) => OnPropertyChanged(nameof(TargetSummaryText));

    /// <summary>Raises change notification for status-derived properties (e.g. after the
    /// owning view model mutates <see cref="Job"/>.Status directly on resume/pause).</summary>
    public void RefreshStatusIndicators()
    {
        OnPropertyChanged(nameof(IsPreparing));
        OnPropertyChanged(nameof(StatusAccentColor));
    }

    public string TypeLabel => Job.Type switch
    {
        DownloadJobType.Artist        => "Artist",
        DownloadJobType.ImageId       => "Image",
        DownloadJobType.BookmarkArtist => "Bookmarks",
        DownloadJobType.BookmarkImage  => "Bookmarks",
        DownloadJobType.ListFile      => "List",
        _                             => Job.Type.ToString()
    };

    public string? ArtistInfo
    {
        get
        {
            var t = Job.Targets.FirstOrDefault();
            if (t == null) return null;
            if (!string.IsNullOrEmpty(t.UserName) && !string.IsNullOrEmpty(t.UserId))
                return $"{t.UserName} (ID {t.UserId})";
            if (!string.IsNullOrEmpty(t.UserName)) return t.UserName;
            return null;
        }
    }
    public bool HasArtistInfo => !string.IsNullOrEmpty(ArtistInfo);

    public DownloadJobViewModel(DownloadJob job, PixivImageLoader imageLoader, DownloadCoordinator? coordinator = null, SettingsService? settingsService = null)
    {
        Job = job;
        _imageLoader = imageLoader;
        _coordinator = coordinator;
        _settingsService = settingsService;
        UpdateStatus();
        var isPlaceholder = job.Name != null && job.Name.StartsWith("(Queued) ");
        IsCancellable = job.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Paused;
        IsPausable    = !isPlaceholder && job.Status == JobStatus.Running;
        IsResumable   = !isPlaceholder && job.Status is JobStatus.Pending or JobStatus.Paused;
        var firstTarget = job.Targets.FirstOrDefault();
        if (firstTarget != null && !string.IsNullOrEmpty(firstTarget.UserName))
            CurrentArtist = firstTarget.UserName;
        var thumbUrl = firstTarget?.ThumbnailUrl
            ?? job.Targets.FirstOrDefault(t => !string.IsNullOrEmpty(t.ThumbnailUrl))?.ThumbnailUrl;
        if (thumbUrl != null)
            _ = LoadThumbnailAsync(thumbUrl, imageLoader);
        // Seed artwork counts and downloaded size from persisted DB data so paused/restarted
        // jobs show progress immediately without waiting for the next progress tick.
        // Use CompletedArtworkIds.Count as fallback when DownloadedItems=0 (paused mid-run,
        // before the target reached Completed status where DownloadedItems is written).
        CompletedArtworks    = job.Targets.Sum(t => t.DownloadedItems > 0 ? t.DownloadedItems : t.CompletedArtworkIds.Count);
        TotalArtworks        = job.Targets.Sum(t => t.FoundItems > 0 ? t.FoundItems : t.DownloadedItems);
        TotalDownloadedBytes = job.Targets.Sum(t => t.DownloadedBytes);
        if (TotalArtworks > 0)
            ProgressPercent = CompletedArtworks * 100.0 / TotalArtworks;

        // For completed jobs with no stored byte data (pre-dates byte tracking), scan the
        // output folder on disk to derive the total size asynchronously.
        if (TotalDownloadedBytes == 0 && job.Status == JobStatus.Completed && !string.IsNullOrEmpty(job.OutputFolder))
            _ = ScanOutputFolderSizeAsync(job.OutputFolder);
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_coordinator == null) return;
        await _coordinator.CancelJobAsync(Job.Id);
        IsCancellable = false;
        IsPausable    = false;
        IsResumable   = false;
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        if (_coordinator == null) return;
        if (Job.Status is JobStatus.Paused or JobStatus.Pending or JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled) return;
        // Optimistic update — immediate UI feedback
        IsPausable    = false;
        IsResumable   = true;
        IsCancellable = true;
        StatusText    = "⏸ Paused";
        Job.Status    = JobStatus.Paused;
        OnPropertyChanged(nameof(IsPreparing));
        OnPropertyChanged(nameof(StatusAccentColor));
        var paused = await _coordinator.PauseJobAsync(Job.Id);
        if (!paused)
        {
            // Coordinator didn't find the job running (race or already finished) — revert.
            IsPausable    = true;
            IsResumable   = false;
            IsCancellable = true;
            StatusText    = "▶ Running";
            Job.Status    = JobStatus.Running;
            OnPropertyChanged(nameof(IsPreparing));
            OnPropertyChanged(nameof(StatusAccentColor));
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (_coordinator == null) return;
        // Optimistic update — immediate UI feedback
        IsPausable    = true;
        IsResumable   = false;
        IsCancellable = true;
        StatusText    = "▶ Running";
        Job.Status    = JobStatus.Running;
        OnPropertyChanged(nameof(IsPreparing));
        // forceStart=true: user explicitly pressed ▶, bypass the concurrent-slot limit
        var started = await _coordinator.StartJobAsync(Job.Id, forceStart: true);
        if (!started)
        {
            // Failed for another reason (job not found, etc.) — revert
            IsPausable    = false;
            IsResumable   = true;
            IsCancellable = true;
            StatusText    = "⏳ Queued";
            Job.Status    = JobStatus.Pending;
            OnPropertyChanged(nameof(IsPreparing));
            OnPropertyChanged(nameof(StatusAccentColor));
        }
    }

    public Func<Task>? OnReordered { get; set; }

    [RelayCommand]
    private async Task MoveToTopAsync()    => await ReorderAsync(DownloadCoordinator.ReorderAction.MoveToTop);
    [RelayCommand]
    private async Task MoveUpAsync()       => await ReorderAsync(DownloadCoordinator.ReorderAction.MoveUp);
    [RelayCommand]
    private async Task MoveDownAsync()     => await ReorderAsync(DownloadCoordinator.ReorderAction.MoveDown);
    [RelayCommand]
    private async Task MoveToBottomAsync() => await ReorderAsync(DownloadCoordinator.ReorderAction.MoveToBottom);

    private async Task ReorderAsync(DownloadCoordinator.ReorderAction action)
    {
        if (_coordinator == null) return;
        await _coordinator.ReorderJobAsync(Job.Id, action);
        if (OnReordered != null) await OnReordered();
    }

    private async Task ScanOutputFolderSizeAsync(string folder)
    {
        try
        {
            var bytes = await Task.Run(() =>
            {
                if (!Directory.Exists(folder)) return 0L;
                return new DirectoryInfo(folder)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            });
            if (bytes > 0)
                await Dispatcher.UIThread.InvokeAsync(() => TotalDownloadedBytes = bytes);
        }
        catch { }
    }

    private async Task LoadThumbnailAsync(string url, PixivImageLoader loader)
    {
        try
        {
            var bytes = await loader.FetchBytesAsync(url);
            if (bytes is null && url.Contains("_master1200"))
                bytes = await loader.FetchBytesAsync(url.Replace("_master1200", "_square1200"));
            if (bytes is null && url.Contains("_square1200"))
                bytes = await loader.FetchBytesAsync(url.Replace("_square1200", "_master1200"));
            if (bytes != null)
            {
                // Decode on background thread to avoid UI-thread jank
                var bmp = await Task.Run(() => new Bitmap(new System.IO.MemoryStream(bytes)));
                await Dispatcher.UIThread.InvokeAsync(() => Thumbnail = bmp);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = ResolvedOutputFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            // Single-image jobs: select the downloaded file in Explorer so it points
            // directly at the image instead of just opening the containing folder.
            var fileToSelect = TryGetSingleImageFile(folder);
            if (fileToSelect != null && OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fileToSelect}\"",
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch { }
    }

    /// <summary>
    /// For a single-artwork job, returns the path of the downloaded image file within
    /// <paramref name="folder"/> so Explorer can highlight it. Returns null for
    /// multi-item jobs or when no matching file is found.
    /// </summary>
    private string? TryGetSingleImageFile(string folder)
    {
        try
        {
            // Only meaningful when the job downloaded a single artwork target.
            var artworkTargets = Job.Targets.Where(t => t.Type == TargetType.Artwork).ToList();
            if (Job.Targets.Count != 1 || artworkTargets.Count != 1) return null;
            if (!Directory.Exists(folder)) return null;

            var targetId = artworkTargets[0].TargetId;
            var images = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Prefer a file whose name contains the artwork id, else the only image present.
            var match = images.FirstOrDefault(f =>
                !string.IsNullOrWhiteSpace(targetId) &&
                Path.GetFileName(f).Contains(targetId, StringComparison.OrdinalIgnoreCase));
            return match ?? (images.Count == 1 ? images[0] : null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the artist-level folder for a given output path.
    /// When <paramref name="downloadRoot"/> is known, walks up until the path is a
    /// direct child of DownloadRoot (handles R-18 and per-artwork subfolders).
    /// Otherwise walks up until an existing directory is found.
    /// </summary>
    private static string ResolveArtistRootFolder(string outputFolder, string? downloadRoot = null)
    {
        // Strips trailing directory separators so "D:\Pixiv\" and "D:\Pixiv" compare equal.
        static string Norm(string p) => Path.GetFullPath(p)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            var current = Norm(outputFolder);

            if (!string.IsNullOrWhiteSpace(downloadRoot))
            {
                var root = Norm(downloadRoot);

                // Files saved directly in the root (no per-artist subfolder) → open the root.
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                    return Directory.Exists(root) ? root : outputFolder;

                // Walk up until current's parent IS the download root (current = artist folder).
                while (!string.IsNullOrEmpty(current))
                {
                    var parent = Path.GetDirectoryName(current);
                    if (parent == null || parent == current) break;
                    parent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                        return Directory.Exists(current) ? current : root;
                    current = parent;
                }
                // Overshot: the output folder isn't under the (possibly changed) download root.
                // Fall through to the nearest-existing-ancestor logic below rather than jumping
                // all the way to the root — that keeps "Open Folder" pointing at the artwork's
                // actual location instead of the top-level download directory.
            }

            // No DownloadRoot known (or we overshot) — walk up until we find an existing directory.
            current = Norm(outputFolder);
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (parent == null || parent == current) break;
                current = parent;
            }
            if (Directory.Exists(current)) return current;
        }
        catch { }
        return outputFolder;
    }

    public void ApplyProgress(JobProgress progress)
    {
        if (progress.PercentComplete > 0 || ProgressPercent == 0)
            ProgressPercent = progress.PercentComplete;
        StatusText        = progress.Status switch
        {
            JobStatus.Running => "▶ Running",
            JobStatus.Cancelled => "🚫 Cancelled",
            JobStatus.Paused => "⏸ Paused",
            JobStatus.Pending => "⏳ Queued",
            _ => progress.Status.ToString()
        };
        IsCancellable     = progress.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Paused;
        IsPausable        = progress.Status == JobStatus.Running;
        IsResumable       = progress.Status is JobStatus.Pending or JobStatus.Paused;
        CompletedCount    = progress.CompletedTargets;
        TotalCount        = progress.TotalTargets;
        // DownloadArtistAsync now sends job-wide artwork counts directly in
        // CompletedTargets/TotalTargets (when TotalTargets > number of job targets).
        // Never clobber a non-zero total with zero from an early "preparing" tick.
        if (progress.TotalTargets > Job.Targets.Count || Job.Targets.Count <= 1)
        {
            if (progress.TotalTargets > 0 || TotalArtworks == 0)
                TotalArtworks = progress.TotalTargets;
            if (progress.CompletedTargets > 0 || CompletedArtworks == 0)
                CompletedArtworks = progress.CompletedTargets;
        }
        if (progress.CurrentTargetName != null)
            CurrentTargetName = progress.CurrentTargetName;
        ProgressText = TotalArtworks > 0
            ? $"{CompletedArtworks} / {TotalArtworks}"
            : "Running…";

        // For multi-target jobs update CurrentArtist live from the active target name.
        // For single-target jobs fall back to the static first-target UserName.
        if (Job.Targets.Count > 1 && progress.CurrentTargetName != null)
            CurrentArtist = progress.CurrentTargetName;
        else
        {
            var t = Job.Targets.FirstOrDefault();
            if (t != null && !string.IsNullOrEmpty(t.UserName))
                CurrentArtist = t.UserName;
        }

        // Per-file detail
        if (progress.CurrentArtworkId != null)
        {
            var pageLabel = progress.CurrentPageTotal > 1
                ? $"p{progress.CurrentPageIndex + 1}/{progress.CurrentPageTotal}"
                : null;
            var pct = progress.CurrentTotalBytes > 0
                ? (int)(100 * progress.CurrentBytesSoFar / progress.CurrentTotalBytes.Value)
                : 0;
            var sizeLabel = progress.CurrentTotalBytes > 0
                ? $"{progress.CurrentBytesSoFar / 1024} / {progress.CurrentTotalBytes.Value / 1024} KB"
                : progress.CurrentBytesSoFar > 0 ? $"{progress.CurrentBytesSoFar / 1024} KB" : null;

            CurrentFileLabel = string.Join("  ",
                new[] { progress.CurrentArtworkId, pageLabel, sizeLabel }
                    .Where(s => !string.IsNullOrEmpty(s)));
            CurrentFilePct   = pct;
            HasCurrentFile   = true;

            // Swap live thumbnail when artwork changes
            if (progress.CurrentThumbnailUrl != null && progress.CurrentThumbnailUrl != _lastThumbnailUrl)
            {
                _lastThumbnailUrl = progress.CurrentThumbnailUrl;
                if (_imageLoader != null)
                {
                    _ = LoadCurrentThumbnailAsync(progress.CurrentThumbnailUrl);
                    // If the static job thumbnail never loaded (cold start), use the first
                    // artwork thumbnail as a fallback so the card isn't blank.
                    if (Thumbnail == null)
                        _ = LoadThumbnailAsync(progress.CurrentThumbnailUrl, _imageLoader);
                }
            }
        }

        // Accumulate completed-artwork bytes into the running total
        if (progress.ArtworkBytesCompleted > 0)
            TotalDownloadedBytes += progress.ArtworkBytesCompleted;

        // Speed / ETA
        SpeedText = progress.SpeedMbps > 0.01
            ? $"{progress.SpeedMbps:F1} MB/s"
            : null;
        EtaText = progress.EtaSeconds > 0
            ? $"~{progress.EtaSeconds / 60}m {progress.EtaSeconds % 60}s left"
            : null;

        // Status may have changed (e.g. Running) — refresh the "Preparing…" indicator.
        OnPropertyChanged(nameof(IsPreparing));
    }

    private async Task LoadCurrentThumbnailAsync(string url)
    {
        try
        {
            var bytes = await _imageLoader!.FetchBytesAsync(url);
            if (bytes is null && url.Contains("_master1200"))
                bytes = await _imageLoader.FetchBytesAsync(url.Replace("_master1200", "_square1200"));
            if (bytes != null)
            {
                // Decode on background thread, then assign on UI thread
                var bmp = await Task.Run(() => new Bitmap(new System.IO.MemoryStream(bytes)));
                await Dispatcher.UIThread.InvokeAsync(() => CurrentFileThumbnail = bmp);
            }
        }
        catch { }
    }

    public void UpdateStatus()
    {
        StatusText = Job.Status switch
        {
            JobStatus.Pending => "⏳ Queued",
            JobStatus.Running => "▶ Running",
            JobStatus.Paused => "⏸ Paused",
            JobStatus.Completed => "✅ Completed",
            JobStatus.Failed => "❌ Failed",
            JobStatus.Cancelled => "🚫 Cancelled",
            _ => Job.Status.ToString()
        };

        IsCancellable = Job.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Paused;
        IsPausable    = Job.Status == JobStatus.Running;
        IsResumable   = Job.Status is JobStatus.Pending or JobStatus.Paused;

        if (Job.Status == JobStatus.Completed ||
            Job.Status == JobStatus.Failed)
        {
            var failed = Job.FailedItems;
            // Total artworks/images actually downloaded across every target (e.g. all
            // artists in a multi-artist job), not just the number of targets completed.
            var totalArtworks = TotalArtworksDownloaded;
            var artworkLabel = totalArtworks == 1 ? "artwork" : "artworks";

            if (Job.Type is DownloadJobType.Artist or DownloadJobType.BookmarkArtist)
            {
                var artistLabel = Job.TotalItems == 1 ? "artist" : "artists";
                ResultSummary = $"{totalArtworks} {artworkLabel} from {Job.TotalItems} {artistLabel}";
            }
            else
            {
                ResultSummary = $"{totalArtworks} {artworkLabel} downloaded";
            }

            if (failed > 0)
                ResultSummary += $" · {failed} failed";

            HasFailedItems = failed > 0;
            ProgressPercent = 100;
            ProgressText = ResultSummary;
        }
        else if (Job.Status == JobStatus.Running)
        {
            ProgressPercent = Job.ProgressPercent;
            ProgressText = $"{Job.CompletedItems} of {Job.TotalItems} completed";
        }
    }
}
