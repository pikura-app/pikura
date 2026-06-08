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
        _activeJobs.CollectionChanged    += (_, _) => UpdateQueuePositions();
        _completedJobs.CollectionChanged += (_, _) => RegroupCompleted();
        _failedJobs.CollectionChanged    += (_, _) => RegroupFailed();
        _cancelledJobs.CollectionChanged += (_, _) => RegroupCancelled();
    }

    private void RegroupCompleted() { if (!_suppressRegroup) _completedGrouping.Regroup(); }
    private void RegroupFailed()    { if (!_suppressRegroup) _failedGrouping.Regroup(); }
    private void RegroupCancelled() { if (!_suppressRegroup) _cancelledGrouping.Regroup(); }

    private void UpdateQueuePositions()
    {
        for (int i = 0; i < _activeJobs.Count; i++)
            _activeJobs[i].QueuePosition = i + 1;
    }

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
        foreach (var job in ActiveJobs.ToList())
            await job.ResumeCommand.ExecuteAsync(null);
    }

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

    private void OnJobStarted(object? sender, JobCompletedEventArgs e)
    {
        void AddToActive()
        {
            var existing = ActiveJobs.FirstOrDefault(j => j.Job.Id == e.Job.Id);
            if (existing != null)
            {
                // Reuse the existing VM (preserves progress counters) — just update status and move to top
                existing.IsPausable    = true;
                existing.IsResumable   = false;
                existing.IsCancellable = true;
                existing.StatusText    = "▶ Running";
                existing.Job.Status    = JobStatus.Running;
                // Re-subscribe progress so events flow again after resume
                var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, existing));
                _coordinator.SubscribeToProgress(e.Job.Id, progressHandler);
                ActiveJobs.Remove(existing);
                ActiveJobs.Insert(0, existing);
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
                    if (active != null) ActiveJobs.Remove(active);
                    var vm = new DownloadJobViewModel(job, _imageLoader, null)
                        { OnReordered = null };
                    
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

            // Move cancelled jobs out of Active list immediately
            // (Paused jobs are handled by OnJobCompleted to avoid duplicate processing)
            if (progress.Status == JobStatus.Cancelled)
            {
                var active = ActiveJobs.FirstOrDefault(j => j.Job.Id == progress.JobId);
                if (active != null)
                {
                    ActiveJobs.Remove(active);
                    active.Job.Status = progress.Status;
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
        var activeJobs    = await _jobRepository.GetAllActiveJobsAsync();
        var completedJobs = await _jobRepository.GetJobsAsync();

        ActiveJobs.Clear();
        CompletedJobs.Clear();

        // Sort: Running first, then Paused, then Pending
        // (CollectionChanged will call UpdateQueuePositions after each Add)
        var sortedActive = activeJobs
            .OrderBy(j => j.Status switch { JobStatus.Running => 0, JobStatus.Paused => 1, _ => 2 })
            .ThenBy(j => j.CreatedAt);
        foreach (var job in sortedActive)
        {
            var jobVm = new DownloadJobViewModel(job, _imageLoader, _coordinator, _settingsService)
                { OnReordered = () => Dispatcher.UIThread.InvokeAsync(LoadJobsAsync) };
            var progressHandler = new Progress<JobProgress>(p => OnProgressReceived(p, jobVm));
            _coordinator.SubscribeToProgress(job.Id, progressHandler);
            ActiveJobs.Add(jobVm);
        }

        FailedJobs.Clear();
        CancelledJobs.Clear();

        // Defer regrouping until all rows are added so we group once, not per-add
        // (O(n) instead of O(n²) — critical for histories with 1000+ entries).
        _suppressRegroup = true;
        try
        {
            foreach (var job in completedJobs.OrderByDescending(j => j.CompletedAt))
            {
                var vm = new DownloadJobViewModel(job, _imageLoader, settingsService: _settingsService);
                switch (job.Status)
                {
                    case JobStatus.Completed:  CompletedJobs.Add(vm);  break;
                    case JobStatus.Failed:     FailedJobs.Add(vm);     break;
                    case JobStatus.Cancelled:  CancelledJobs.Add(vm);  break;
                }
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
    [ObservableProperty] private string? _currentFileLabel;
    [ObservableProperty] private double _currentFilePct;
    [ObservableProperty] private Bitmap? _currentFileThumbnail;
    [ObservableProperty] private bool _hasCurrentFile;

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

    private string? ResolvedOutputFolder
    {
        get
        {
            var downloadRoot = _settingsService?.Current.DownloadRoot;

            // Multi-artist job: targets span more than one distinct user → open DownloadRoot
            var distinctUsers = Job.Targets
                .Select(t => t.UserId)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct()
                .ToList();
            if (distinctUsers.Count > 1)
                return !string.IsNullOrWhiteSpace(downloadRoot) && Directory.Exists(downloadRoot)
                    ? downloadRoot : null;

            // Single-artist: use stored OutputFolder if available
            if (!string.IsNullOrWhiteSpace(Job.OutputFolder))
                return ResolveArtistRootFolder(Job.OutputFolder, downloadRoot);

            // Fallback: search DownloadRoot for a folder matching the artist's UserId
            if (string.IsNullOrWhiteSpace(downloadRoot) || !Directory.Exists(downloadRoot)) return null;
            var userId = distinctUsers.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userId)) return null;
            try
            {
                return Directory.EnumerateDirectories(downloadRoot, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(d => Path.GetFileName(d).Contains(userId, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }
    }
    public bool HasThumbnail => Thumbnail != null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

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
        IsCancellable = job.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;
        IsPausable    = !isPlaceholder && job.Status == JobStatus.Running;
        IsResumable   = !isPlaceholder && job.Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;
        var firstTarget = job.Targets.FirstOrDefault();
        if (firstTarget != null && !string.IsNullOrEmpty(firstTarget.UserName))
            CurrentArtist = firstTarget.UserName;
        var thumbUrl = firstTarget?.ThumbnailUrl
            ?? job.Targets.FirstOrDefault(t => !string.IsNullOrEmpty(t.ThumbnailUrl))?.ThumbnailUrl;
        if (thumbUrl != null)
            _ = LoadThumbnailAsync(thumbUrl, imageLoader);
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
        // Optimistic update — immediate UI feedback
        IsPausable    = false;
        IsResumable   = true;
        IsCancellable = true;
        StatusText    = "⏸ Paused";
        Job.Status    = JobStatus.Paused;
        await _coordinator.PauseJobAsync(Job.Id);
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
        await _coordinator.StartJobAsync(Job.Id);
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

    private async Task LoadThumbnailAsync(string url, PixivImageLoader loader)
    {
        try
        {
            var bytes = await loader.FetchBytesAsync(url);
            if (bytes is null && url.Contains("_master1200"))
                bytes = await loader.FetchBytesAsync(url.Replace("_master1200", "_square1200"));
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
    /// Returns the artist-level folder for a given output path.
    /// When <paramref name="downloadRoot"/> is known, walks up until the path is a
    /// direct child of DownloadRoot (handles R-18 and per-artwork subfolders).
    /// Otherwise walks up until an existing directory is found.
    /// </summary>
    private static string ResolveArtistRootFolder(string outputFolder, string? downloadRoot = null)
    {
        try
        {
            var current = Path.GetFullPath(outputFolder);

            if (!string.IsNullOrWhiteSpace(downloadRoot))
            {
                var root = Path.GetFullPath(downloadRoot);
                // Walk up until current's parent IS the download root (i.e. current is the artist folder)
                while (!string.IsNullOrEmpty(current))
                {
                    var parent = Path.GetDirectoryName(current);
                    if (parent == null || parent == current) break;
                    // Stop when parent equals DownloadRoot — current is the artist folder
                    if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                        return Directory.Exists(current) ? current : root;
                    current = parent;
                }
                // Fallback: return DownloadRoot if we overshot
                if (Directory.Exists(root)) return root;
            }

            // No DownloadRoot known — walk up until we find an existing directory
            current = Path.GetFullPath(outputFolder);
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
        ProgressPercent   = progress.PercentComplete;
        StatusText        = progress.Status switch
        {
            JobStatus.Running => "▶ Running",
            JobStatus.Cancelled => "🚫 Cancelling…",
            JobStatus.Paused => "⏸ Paused",
            _ => progress.Status.ToString()
        };
        IsCancellable     = progress.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;
        IsPausable        = progress.Status == JobStatus.Running;
        IsResumable       = progress.Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;
        CompletedCount    = progress.CompletedTargets;
        TotalCount        = progress.TotalTargets;
        if (progress.CurrentTargetName != null)
            CurrentTargetName = progress.CurrentTargetName;
        ProgressText = progress.TotalTargets > 0
            ? $"{progress.CompletedTargets} / {progress.TotalTargets}"
            : "Running…";

        var t = Job.Targets.FirstOrDefault();
        if (t != null && !string.IsNullOrEmpty(t.UserName))
            CurrentArtist = t.UserName;

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
                    _ = LoadCurrentThumbnailAsync(progress.CurrentThumbnailUrl);
            }
        }
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

    private void UpdateStatus()
    {
        StatusText = Job.Status switch
        {
            JobStatus.Queued  => "⏳ Queued",
            JobStatus.Pending => "⏳ Starting…",
            JobStatus.Running => "▶ Running",
            JobStatus.Paused => "⏸ Paused",
            JobStatus.Completed => "✅ Completed",
            JobStatus.Failed => "❌ Failed",
            JobStatus.Cancelled => "🚫 Cancelled",
            _ => Job.Status.ToString()
        };

        IsCancellable = Job.Status is JobStatus.Running or JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;
        IsPausable    = Job.Status == JobStatus.Running;
        IsResumable   = Job.Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Paused;

        if (Job.Status == JobStatus.Completed ||
            Job.Status == JobStatus.Failed)
        {
            var success = Job.CompletedItems;
            var failed = Job.FailedItems;
            ResultSummary = $"{success} succeeded, {failed} failed";
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
