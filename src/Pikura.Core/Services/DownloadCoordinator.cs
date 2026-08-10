using Microsoft.Extensions.Logging;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Settings;
using Pikura.Core.Utilities;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Pikura.Core.Services;

/// <summary>
/// Progress update for a download job.
/// </summary>
public sealed record JobProgress(
    Guid JobId,
    JobStatus Status,
    int CompletedTargets,
    int TotalTargets,
    double PercentComplete,
    string? CurrentTargetName,
    string? Message,
    // Per-file detail (null when not a file-level update)
    string? CurrentArtworkId = null,
    string? CurrentThumbnailUrl = null,
    int CurrentPageIndex = 0,
    int CurrentPageTotal = 0,
    long CurrentBytesSoFar = 0,
    long? CurrentTotalBytes = null,
    double SpeedMbps = 0,
    int? EtaSeconds = null,
    long ArtworkBytesCompleted = 0);

/// <summary>
/// Coordinates batch download operations.
/// Manages download queue, job execution, and progress reporting.
/// </summary>
public sealed class DownloadCoordinator : IDisposable
{
    private readonly PixivClient _client;
    private readonly PixivDownloadService _downloadService;
    private readonly SettingsService _settingsService;
    private readonly DownloadJobRepository _jobRepository;
    private readonly ILogger<DownloadCoordinator> _logger;
    private readonly AccountService? _accountService;

    // Active job tracking
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();

    // Progress reporting
    private readonly ConcurrentDictionary<Guid, List<IProgress<JobProgress>>> _progressListeners = new();

    // Rate tracking for speed/ETA (timestamp + bytes-so-far snapshot)
    private readonly ConcurrentDictionary<Guid, (DateTime Time, long Bytes)> _rateSnapshots = new();

    /// <summary>
    /// Event raised when a job starts running.
    /// </summary>
    public event EventHandler<JobCompletedEventArgs>? JobStarted;

    /// <summary>
    /// Event raised when a job completes (successfully or with failures).
    /// </summary>
    public event EventHandler<JobCompletedEventArgs>? JobCompleted;

    /// <summary>
    /// Event raised when a new job is created (queued but not yet started).
    /// </summary>
    public event EventHandler<JobCompletedEventArgs>? JobCreated;

    /// <summary>
    /// Raises <see cref="JobStarted"/> for a job that was created externally (e.g. gallery/viewer single-download).
    /// </summary>
    public void NotifyJobStarted(DownloadJob job) => JobStarted?.Invoke(this, new JobCompletedEventArgs(job));

    /// <summary>
    /// Raises <see cref="JobCompleted"/> for a job that was saved externally (e.g. gallery single-download).
    /// </summary>
    public void NotifyJobSaved(DownloadJob job) => JobCompleted?.Invoke(this, new JobCompletedEventArgs(job));

    /// <summary>
    /// Registers an externally-managed CancellationTokenSource for a job so that
    /// PauseJobAsync and CancelJobAsync can reach it via _activeJobs.
    /// </summary>
    public void RegisterExternalJob(Guid jobId, CancellationTokenSource cts)
        => _activeJobs.TryAdd(jobId, cts);

    /// <summary>
    /// Removes an externally-registered job from the active tracking dictionary.
    /// Call this when the external download loop has finished.
    /// </summary>
    public void UnregisterExternalJob(Guid jobId)
    {
        _activeJobs.TryRemove(jobId, out _);
        _runningTasks.TryRemove(jobId, out _);
    }

    /// <summary>
    /// Updates a job's status to Running in the DB and fires JobStarted.
    /// Use with RegisterExternalJob when the caller owns the execution loop.
    /// </summary>
    public async Task NotifyJobRunningAsync(DownloadJob job, CancellationToken ct = default)
    {
        await _jobRepository.UpdateJobStatusAsync(job.Id, JobStatus.Running, null, ct);
        job.Status = JobStatus.Running;
        JobStarted?.Invoke(this, new JobCompletedEventArgs(job));
    }

    public DownloadCoordinator(
        PixivClient client,
        PixivDownloadService downloadService,
        SettingsService settingsService,
        DownloadJobRepository jobRepository,
        FanboxClient fanboxClient,
        ILogger<DownloadCoordinator> logger,
        ImageResizeService? resizeService = null,
        AccountService? accountService = null)
    {
        _client = client;
        _downloadService = downloadService;
        _settingsService = settingsService;
        _jobRepository = jobRepository;
        _fanboxClient = fanboxClient;
        _logger = logger;
        _resizeService = resizeService;
        _accountService = accountService;
    }

    private readonly FanboxClient _fanboxClient;
    private readonly ImageResizeService? _resizeService;

    #region Job Management

    /// <summary>
    /// Creates and optionally starts a new download job.
    /// </summary>
    public async Task<DownloadJob> CreateJobAsync(
        DownloadJobType type,
        string name,
        List<DownloadTarget> targets,
        SettingsOverride? settingsOverride = null,
        bool startImmediately = false,
        CancellationToken ct = default,
        JobStatus? initialStatusOverride = null)
    {
        // Always create as Pending (or an explicit override such as Paused).
        // StartJobAsync is the single authority that promotes a job to Running:
        // it enforces the concurrent-job/SafeMode slot limit and actually launches
        // ExecuteJobAsync. Pre-setting Status=Running here would make StartJobAsync
        // reject the job (it only starts Pending/Paused), leaving a "zombie" job that
        // shows Running but never downloads and can't be paused.
        var initialStatus = initialStatusOverride ?? JobStatus.Pending;

        // Append new jobs to the end of the active queue: take the current max SortOrder
        // among active (running/paused/pending) jobs and add one. This keeps the queue
        // order stable and ensures a freshly-created job doesn't jump ahead of existing
        // ones (the active list is sorted by SortOrder, then CreatedAt).
        var existingOrders = await _jobRepository.GetPendingJobSortOrdersAsync(ct);
        var nextSortOrder = existingOrders.Count > 0 ? existingOrders.Max(o => o.SortOrder) + 1 : 0;

        var job = new DownloadJob
        {
            Name = name,
            Type = type,
            Targets = targets,
            Settings = settingsOverride ?? new SettingsOverride { UseGlobalSettings = true },
            Status = initialStatus,
            StartedAt = null,
            CreatedAt = DateTime.UtcNow,
            SortOrder = nextSortOrder
        };

        // Save to database
        await _jobRepository.SaveJobAsync(job, ct);
        _logger.LogInformation("Created download job {JobId} ({Name}) with {TargetCount} targets, status={Status}",
            job.Id, job.Name, job.Targets.Count, initialStatus);

        JobCreated?.Invoke(this, new JobCompletedEventArgs(job));

        // Launch when requested. StartJobAsync promotes Pending/Paused → Running when a
        // slot is free, otherwise the job stays Pending and auto-starts later.
        if (startImmediately && initialStatus is JobStatus.Pending or JobStatus.Paused)
        {
            await StartJobAsync(job.Id, ct);
        }

        return job;
    }

    /// <summary>
    /// Starts a pending job.
    /// </summary>
    public async Task<bool> StartJobAsync(Guid jobId, CancellationToken ct = default, bool forceStart = false)
    {
        var job = await _jobRepository.GetJobAsync(jobId, ct);
        if (job == null)
        {
            _logger.LogWarning("Cannot start job {JobId}: not found", jobId);
            return false;
        }

        if (job.Status != JobStatus.Pending && job.Status != JobStatus.Paused)
        {
            _logger.LogWarning("Cannot start job {JobId}: status is {Status}", jobId, job.Status);
            return false;
        }

        // Enforce MaxConcurrentJobs limit (bypassed when user explicitly presses ▶)
        if (!forceStart)
        {
            var maxJobs = _settingsService.Current.MaxConcurrentJobs;
            if (_settingsService.Current.SafeMode)
                maxJobs = 1; // SafeMode enforces sequential jobs

            if (maxJobs > 0 && _activeJobs.Count >= maxJobs)
            {
                _logger.LogInformation("Job {JobId} remains Pending: concurrent job limit ({Max}) reached", jobId, maxJobs);
                return false;
            }
        }

        // Create cancellation token for this job
        var cts = new CancellationTokenSource();
        if (!_activeJobs.TryAdd(jobId, cts))
        {
            _logger.LogWarning("Job {JobId} is already running", jobId);
            return false;
        }

        // Update status
        await _jobRepository.UpdateJobStatusAsync(jobId, JobStatus.Running, null, ct);
        job.Status = JobStatus.Running;

        // Notify listeners that job is starting
        JobStarted?.Invoke(this, new JobCompletedEventArgs(job));

        // Start the job task
        var task = ExecuteJobAsync(job, cts.Token);
        _runningTasks.TryAdd(jobId, task);

        // Clean up when done
        _ = task.ContinueWith(async t =>
        {
            _activeJobs.TryRemove(jobId, out _);
            _runningTasks.TryRemove(jobId, out _);

            // If already set to Paused (by PauseJobAsync), don't overwrite it.
            // Don't start the next job either — pausing doesn't free a slot.
            var currentJob = await _jobRepository.GetJobAsync(jobId);
            if (currentJob?.Status == JobStatus.Paused)
            {
                JobCompleted?.Invoke(this, new JobCompletedEventArgs(currentJob));
                return;
            }

            // Update final status — check for partially-failed multi-target jobs
            JobStatus finalStatus;
            string? error = null;
            if (t.IsFaulted)
            {
                finalStatus = JobStatus.Failed;
                error = t.Exception?.InnerException?.Message;
            }
            else if (t.IsCanceled)
            {
                finalStatus = JobStatus.Cancelled;
            }
            else
            {
                // Even if the task completed normally, some targets may have failed
                var refreshedJob = await _jobRepository.GetJobAsync(jobId);
                var anyFailed = refreshedJob?.Targets.Any(t2 => t2.Status == TargetStatus.Failed) ?? false;
                finalStatus = anyFailed ? JobStatus.Failed : JobStatus.Completed;
                if (anyFailed)
                {
                    var failedTargets = refreshedJob!.Targets.Where(t2 => t2.Status == TargetStatus.Failed).ToList();
                    error = failedTargets.Count == 1
                        ? $"1 target failed: {failedTargets[0].ErrorMessage}"
                        : $"{failedTargets.Count} targets failed";
                }
            }
            await _jobRepository.UpdateJobStatusAsync(jobId, finalStatus, error);

            // Fire completion event
            var completedJob = await _jobRepository.GetJobAsync(jobId);
            if (completedJob != null)
            {
                JobCompleted?.Invoke(this, new JobCompletedEventArgs(completedJob));
            }

            _logger.LogInformation("Job {JobId} completed with status {Status}", jobId, finalStatus);

            // Auto-start next pending job if a slot is now free
            _ = TryStartNextPendingJobAsync();
        }, TaskContinuationOptions.ExecuteSynchronously);

        _logger.LogInformation("Started download job {JobId}", jobId);
        return true;
    }

    /// <summary>
    /// Cancels a running job.
    /// </summary>
    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            // Immediately notify subscribers so the UI hides the progress panel
            ReportProgress(jobId, new JobProgress(jobId, JobStatus.Cancelled, 0, 0, 0, null, "Cancelled"));
            _logger.LogInformation("Cancelled job {JobId}", jobId);
            return true;
        }

        // Job is not actively running (e.g. Paused or Pending) but still
        // exists in the database. Cancel it directly and notify the UI so it moves
        // out of the active list into Cancelled.
        var job = await _jobRepository.GetJobAsync(jobId);
        if (job != null && job.Status is JobStatus.Running or JobStatus.Paused or JobStatus.Pending)
        {
            await _jobRepository.UpdateJobStatusAsync(jobId, JobStatus.Cancelled);
            ReportProgress(jobId, new JobProgress(jobId, JobStatus.Cancelled, 0, 0, 0, null, "Cancelled"));
            _logger.LogInformation("Cancelled {Status} job {JobId}", job.Status, jobId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all jobs with optional filtering.
    /// </summary>
    public Task<List<DownloadJob>> GetJobsAsync(JobStatus? status = null, int? limit = null, CancellationToken ct = default)
        => _jobRepository.GetJobsAsync(status, limit, ct);

    public enum ReorderAction { MoveUp, MoveDown, MoveToTop, MoveToBottom }

    /// <summary>
    /// Pauses a running job. The job's completed targets are preserved so it can resume.
    /// </summary>
    public async Task<bool> PauseJobAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_activeJobs.TryGetValue(jobId, out var cts))
        {
            _logger.LogWarning("Cannot pause job {JobId}: not running", jobId);
            return false;
        }
        // Set Paused in DB before cancelling so the ContinueWith guard skips overwriting it.
        await _jobRepository.UpdateJobStatusAsync(jobId, JobStatus.Paused, null, ct);
        await cts.CancelAsync();
        // ContinueWith will detect Paused status and fire JobCompleted with the correct state.
        _logger.LogInformation("Paused job {JobId}", jobId);
        return true;
    }

    /// <summary>
    /// Pauses all currently running jobs as part of a graceful application shutdown so
    /// their progress is preserved and they can be resumed on next launch. Best-effort:
    /// startup recovery re-pauses any orphans that slip through.
    /// </summary>
    public async Task PauseAllRunningForShutdownAsync()
    {
        foreach (var (jobId, cts) in _activeJobs.ToArray())
        {
            try
            {
                await _jobRepository.UpdateJobStatusAsync(jobId, JobStatus.Paused);
                await cts.CancelAsync();
                _logger.LogInformation("Paused job {JobId} for shutdown", jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pause job {JobId} during shutdown", jobId);
            }
        }
    }

    /// <summary>
    /// Reorders a pending or paused job relative to others in the queue.
    /// </summary>
    public async Task ReorderJobAsync(Guid jobId, ReorderAction action, CancellationToken ct = default)
    {
        var orders = await _jobRepository.GetPendingJobSortOrdersAsync(ct);
        var idx = orders.FindIndex(x => x.Id == jobId);
        if (idx < 0) return;

        switch (action)
        {
            case ReorderAction.MoveToTop:
            {
                var minOrder = orders.Count > 0 ? orders[0].SortOrder - 1 : 0;
                await _jobRepository.UpdateSortOrderAsync(jobId, minOrder, ct);
                break;
            }
            case ReorderAction.MoveToBottom:
            {
                var maxOrder = orders.Count > 0 ? orders[^1].SortOrder + 1 : 0;
                await _jobRepository.UpdateSortOrderAsync(jobId, maxOrder, ct);
                break;
            }
            case ReorderAction.MoveUp when idx > 0:
            {
                var above = orders[idx - 1];
                var current = orders[idx];
                await _jobRepository.UpdateSortOrderAsync(current.Id, above.SortOrder, ct);
                await _jobRepository.UpdateSortOrderAsync(above.Id, current.SortOrder, ct);
                break;
            }
            case ReorderAction.MoveDown when idx < orders.Count - 1:
            {
                var below = orders[idx + 1];
                var current = orders[idx];
                await _jobRepository.UpdateSortOrderAsync(current.Id, below.SortOrder, ct);
                await _jobRepository.UpdateSortOrderAsync(below.Id, current.SortOrder, ct);
                break;
            }
        }
    }

    /// <summary>
    /// Sets an explicit sort order for a job (used by drag-and-drop reordering).
    /// </summary>
    public Task SetJobSortOrderAsync(Guid jobId, int sortOrder, CancellationToken ct = default)
        => _jobRepository.UpdateSortOrderAsync(jobId, sortOrder, ct);

    /// <summary>
    /// Deletes a job and all its targets.
    /// </summary>
    public async Task<bool> DeleteJobAsync(Guid jobId, CancellationToken ct = default)
    {
        // Cancel if running
        await CancelJobAsync(jobId);

        // Wait for task to complete
        if (_runningTasks.TryGetValue(jobId, out var task))
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch { /* ignore timeout */ }
        }

        await _jobRepository.DeleteJobAsync(jobId, ct);
        _logger.LogInformation("Deleted job {JobId}", jobId);
        return true;
    }

    #endregion

    /// <summary>
    /// Called once on app startup. Converts any jobs left in Running state from a
    /// previous session to Paused (preserving progress), then auto-starts Pending
    /// jobs up to the concurrent-job limit.
    /// </summary>
    public async Task StartupRecoveryAsync(CancellationToken ct = default)
    {
        await _jobRepository.RecoverInterruptedJobsAsync(ct);
        await TryStartNextPendingJobAsync();
    }

    #region Queuing

    /// <summary>
    /// If there is a free concurrent-job slot, starts the oldest Pending job from the database.
    /// </summary>
    private async Task TryStartNextPendingJobAsync()
    {
        var maxJobs = _settingsService.Current.MaxConcurrentJobs;
        if (maxJobs > 0 && _activeJobs.Count >= maxJobs) return;

        var pending = await _jobRepository.GetJobsAsync(status: JobStatus.Pending);
        var next = pending
            .Where(j => !_activeJobs.ContainsKey(j.Id))
            .OrderBy(j => j.SortOrder)
            .ThenBy(j => j.CreatedAt)
            .FirstOrDefault();

        if (next != null)
        {
            _logger.LogInformation("Auto-starting next pending job {JobId}", next.Id);
            await StartJobAsync(next.Id);
        }
    }

    private readonly ConcurrentDictionary<Guid, (DownloadTarget, ImageEditPreset)> _presetQueue = [];

    /// <summary>
    /// Queues a single download with a resize preset for processing after download.
    /// </summary>
    public void QueueDownloadWithPreset(DownloadTarget target, ImageEditPreset preset)
    {
        var jobId = Guid.NewGuid();
        _presetQueue[jobId] = (target, preset);

        // Queue for immediate download and post-process
        _ = Task.Run(async () => await DownloadAndProcessAsync(target, preset));
    }

    private async Task DownloadAndProcessAsync(DownloadTarget target, ImageEditPreset preset)
    {
        try
        {
            // Get base download directory
            var baseDir = Path.Combine(_settingsService.Current.DownloadRoot, "Processed");
            Directory.CreateDirectory(baseDir);

            var fileName = $"{target.TargetId}_p{target.PageIndex}.png";
            var outputPath = Path.Combine(baseDir, fileName);

            // If custom folder is specified, use that
            if (!string.IsNullOrEmpty(preset.CustomOutputFolder))
            {
                Directory.CreateDirectory(preset.CustomOutputFolder);
                outputPath = Path.Combine(preset.CustomOutputFolder, fileName);
            }

            // Download image first to temp location
            var tempPath = Path.Combine(Path.GetTempPath(), $"pikura_{Guid.NewGuid()}.tmp");
            try
            {
                var imageUrl = target.OverrideUrl ?? target.OriginalUrl;
                if (string.IsNullOrEmpty(imageUrl))
                {
                    _logger.LogWarning("No image URL available for target {TargetId}", target.TargetId);
                    return;
                }

                // Route through PixivDownloadService so SafeMode 429/503 backoff +
                // Retry-After honoring apply to the preset queue as well — previously
                // a raw HttpClient bypassed every rate-limit protection.
                using var cts = new CancellationTokenSource();
                await _downloadService.DownloadGenericFileAsync(
                    imageUrl, tempPath,
                    "https://www.pixiv.net/",
                    cts.Token);

                // Process with preset (overwrite original file, then move to final destination)
                if (_resizeService != null)
                {
                    var processedPath = await _resizeService.ProcessAsync(tempPath, preset, cts.Token);
                    if (processedPath != null && processedPath != tempPath)
                    {
                        // If output differs from tempPath, move it to the final destination
                        File.Move(processedPath, outputPath, overwrite: true);
                    }
                }

                _logger.LogInformation("Processed and saved: {OutputPath}", outputPath);
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and process artwork {TargetId}", target.TargetId);
        }
    }

    #endregion

    #region Progress Reporting

    /// <summary>
    /// Subscribes to progress updates for a job.
    /// </summary>
    public void SubscribeToProgress(Guid jobId, IProgress<JobProgress> progress)
    {
        var listeners = _progressListeners.GetOrAdd(jobId, _ => new List<IProgress<JobProgress>>());
        lock (listeners)
        {
            listeners.Add(progress);
        }
    }

    /// <summary>
    /// Unsubscribes from progress updates.
    /// </summary>
    public void UnsubscribeFromProgress(Guid jobId, IProgress<JobProgress> progress)
    {
        if (_progressListeners.TryGetValue(jobId, out var listeners))
        {
            lock (listeners)
            {
                listeners.Remove(progress);
            }
        }
    }

    public void ReportJobProgress(Guid jobId, JobProgress progress) => ReportProgress(jobId, progress);

    private void ReportProgress(Guid jobId, JobProgress progress)
    {
        // Compute speed & ETA from byte deltas
        double speedMbps = 0;
        int? etaSec = null;
        if (progress.CurrentBytesSoFar > 0)
        {
            var now = DateTime.UtcNow;
            if (_rateSnapshots.TryGetValue(jobId, out var prev))
            {
                var elapsedSec = (now - prev.Time).TotalSeconds;
                var bytesDelta = progress.CurrentBytesSoFar - prev.Bytes;
                if (elapsedSec > 0.1 && bytesDelta > 0)
                {
                    speedMbps = bytesDelta / elapsedSec / (1024.0 * 1024.0);
                    if (progress.CurrentTotalBytes > 0)
                    {
                        var remain = progress.CurrentTotalBytes.Value - progress.CurrentBytesSoFar;
                        etaSec = (int)(remain / (bytesDelta / elapsedSec));
                    }
                }
            }
            _rateSnapshots[jobId] = (now, progress.CurrentBytesSoFar);
        }
        else if (progress.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)
        {
            _rateSnapshots.TryRemove(jobId, out _);
        }

        var augmented = progress with { SpeedMbps = speedMbps, EtaSeconds = etaSec };

        if (_progressListeners.TryGetValue(jobId, out var listeners))
        {
            List<IProgress<JobProgress>> snapshot;
            lock (listeners)
            {
                snapshot = listeners.ToList();
            }

            foreach (var listener in snapshot)
            {
                try
                {
                    listener.Report(augmented);
                }
                catch { /* ignore listener errors */ }
            }
        }
    }

    #endregion

    #region Job Execution

    private async Task ExecuteJobAsync(DownloadJob job, CancellationToken ct)
    {
        var completedCount = job.Targets.Count(t => t.Status == TargetStatus.Completed);
        var totalCount = job.Targets.Count;

        // Emit initial progress immediately so the UI shows correct counts on resume
        ReportProgress(job.Id, new JobProgress(
            job.Id,
            JobStatus.Running,
            completedCount,
            totalCount,
            totalCount > 0 ? completedCount * 100.0 / totalCount : 0,
            null,
            null));

        // Get effective settings for this job — start from global, then apply per-account overrides
        var effectiveSettings = job.Settings.UseGlobalSettings
            ? SettingsOverride.FromGlobalSettings(_settingsService.Current)
            : job.Settings;

        // Apply per-account download root/folder/filename overrides when enabled
        var acctProfile = _accountService?.ActiveProfile;
        if (acctProfile?.Settings is { UseAccountSettings: true } acctSettings)
        {
            if (!string.IsNullOrWhiteSpace(acctSettings.DownloadRoot))
            { effectiveSettings.DownloadRoot = acctSettings.DownloadRoot; effectiveSettings.UseGlobalSettings = false; }
            if (!string.IsNullOrWhiteSpace(acctSettings.FolderTemplate))
                effectiveSettings.FolderTemplate = acctSettings.FolderTemplate;
            if (!string.IsNullOrWhiteSpace(acctSettings.FilenameTemplate))
                effectiveSettings.FilenameTemplate = acctSettings.FilenameTemplate;
            if (acctSettings.MaxConcurrentDownloads.HasValue)
                effectiveSettings.MaxConcurrentDownloads = acctSettings.MaxConcurrentDownloads;
            if (acctSettings.FilterAiGenerated.HasValue)
                effectiveSettings.FilterAiGenerated = acctSettings.FilterAiGenerated;
            if (acctSettings.SkipR18.HasValue)
                effectiveSettings.SkipR18 = acctSettings.SkipR18;
            if (acctSettings.SkipR18G.HasValue)
                effectiveSettings.SkipR18G = acctSettings.SkipR18G;
            if (acctSettings.SeparateR18Folder.HasValue)
                effectiveSettings.SeparateR18Folder = acctSettings.SeparateR18Folder;
            if (acctSettings.AllowRedownload.HasValue)
                effectiveSettings.AllowRedownload = acctSettings.AllowRedownload;
        }

        // Process each target
        var targetsToRun = job.Targets.Where(t =>
            t.Status != TargetStatus.Completed &&
            t.Status != TargetStatus.Skipped).ToList();
        var processedAny = false;

        // Job-wide artwork totals for accurate multi-artist progress.
        // Seed offset from already-completed targets; total grows as each artist's count is discovered.
        var jobArtworkOffset = job.Targets
            .Where(t => t.Status == TargetStatus.Completed)
            .Sum(t => t.FoundItems > 0 ? t.FoundItems : t.DownloadedItems);
        var jobArtworkTotal = jobArtworkOffset; // grows via onArtistTotalKnown callbacks

        foreach (var target in targetsToRun)
        {
            ct.ThrowIfCancellationRequested();

            // Inter-target pacing under SafeMode. Applies between every pair of
            // targets in a multi-target job (e.g. 20 ranking artworks selected at
            // once, or a FANBOX schedule with multiple posts). Skipped for the
            // first target — there's nothing to space out before it. Artist
            // targets still get their own finer-grained per-artwork pacing inside
            // DownloadArtistAsync; the two layers compose because this delay
            // fires *before* the artist target starts processing.
            if (_settingsService.Current.SafeMode && processedAny)
            {
                var jittered = 2.0 + (Random.Shared.NextDouble() * 2.0); // 2.0–4.0s
                await Task.Delay(TimeSpan.FromSeconds(jittered), ct);
            }
            processedAny = true;

            // Update target status
            await _jobRepository.UpdateTargetStatusAsync(target.Id, TargetStatus.Running);

            ReportProgress(job.Id, new JobProgress(
                job.Id,
                JobStatus.Running,
                completedCount,
                totalCount,
                completedCount * 100.0 / totalCount,
                target.Name,
                $"Processing {target.Name}..."
            ));

            // Prefer the user-facing "Retry count" setting (RetryCount); fall back to the
            // legacy MaxRetryAttempts, then a default of 3. AutoRetry gates whether any
            // retries happen at all.
            // Read these live from global settings so changes apply without restarting the job.
            var liveSettings = _settingsService.Current;
            var maxRetries = (effectiveSettings.AutoRetryFailedDownloads ?? liveSettings.AutoRetryFailedDownloads)
                ? (effectiveSettings.RetryCount ?? effectiveSettings.MaxRetryAttempts ?? liveSettings.RetryCount)
                : 0;
            var retryDelay = TimeSpan.FromSeconds(effectiveSettings.RetryDelaySeconds ?? liveSettings.RetryDelaySeconds);
            var attempt = 0;
            var success = false;

            while (attempt <= maxRetries && !success)
            {
                try
                {
                    // Get target-specific settings if available
                    var targetSettings = target.HasCustomSettings
                        ? target.CustomSettings!.ApplyTo(effectiveSettings)
                        : effectiveSettings;

                    // Execute based on target type
                    int found, downloaded;
                    long bytes = 0;
                    if (target.Type == TargetType.Artist)
                    {
                        (found, downloaded, bytes) = await DownloadArtistAsync(
                            job.Id, target, targetSettings, ct,
                            jobArtworkOffset: jobArtworkOffset,
                            jobArtworkTotal: jobArtworkTotal,
                            onArtistTotalKnown: artistTotal =>
                            {
                                // Update job-wide total now that we know the real count.
                                // jobArtworkTotal includes a 0-placeholder for this target;
                                // replace it with the real value.
                                jobArtworkTotal += artistTotal;
                            },
                            onOutputFolder: folder =>
                            {
                                if (string.IsNullOrWhiteSpace(job.OutputFolder))
                                {
                                    job.OutputFolder = folder;
                                    _ = _jobRepository.SaveJobAsync(job, ct);
                                }
                            },
                            onFirstThumbnail: url =>
                            {
                                // Always update to the first artwork thumbnail — these are
                                // disk-cached by PixivImageLoader and reliably render on restart,
                                // unlike profile image URLs which can 403 after a session reset.
                                target.ThumbnailUrl = url;
                                _ = _jobRepository.SaveJobAsync(job, ct);
                            });
                        // Advance the offset by this artist's confirmed total for the next target.
                        jobArtworkOffset += found;
                    }
                    else
                    {
                        (found, downloaded) = target.Type switch
                        {
                            TargetType.Artwork => await DownloadArtworkAsync(target, targetSettings, ct,
                                onOutputFolder: folder =>
                                {
                                    if (string.IsNullOrWhiteSpace(job.OutputFolder))
                                    {
                                        job.OutputFolder = folder;
                                        _ = _jobRepository.SaveJobAsync(job, ct);
                                    }
                                }),
                            TargetType.Post    => await DownloadFanboxPostAsync(target, targetSettings, ct),
                            _ => throw new NotSupportedException($"Target type {target.Type} not supported")
                        };
                    }

                    // Guard against silent success: if the target had items to fetch but
                    // every one failed (e.g. disposed semaphore, rate-limit, network), do
                    // NOT mark it Completed. Throw so it routes through the retry/Failed
                    // path and surfaces to the user instead of reporting "succeeded, 0 files".
                    if (found > 0 && downloaded == 0)
                    {
                        throw new InvalidOperationException(
                            $"All {found} item(s) for target {target.TargetId} failed to download (0 succeeded).");
                    }

                    await _jobRepository.UpdateTargetStatusAsync(
                        target.Id,
                        TargetStatus.Completed,
                        found,
                        downloaded);

                    completedCount++;
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    // If the job was paused (not user-cancelled), leave the target in its
                    // current in-progress state (Running/Pending) so it is re-queued on resume.
                    // Only mark Cancelled when the user explicitly cancelled the job.
                    var jobStatus = (await _jobRepository.GetJobAsync(job.Id))?.Status;
                    if (jobStatus != JobStatus.Paused)
                        await _jobRepository.UpdateTargetStatusAsync(target.Id, TargetStatus.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;

                    if (attempt > maxRetries)
                    {
                        _logger.LogError(ex, "Failed to process target {TargetId} in job {JobId} after {Attempts} attempts",
                            target.TargetId, job.Id, attempt);

                        await _jobRepository.UpdateTargetStatusAsync(
                            target.Id,
                            TargetStatus.Failed,
                            errorMessage: ex.Message);

                        // Continue with next target (don't fail entire job for one error)
                    }
                    else
                    {
                        // Under SafeMode jitter the retry delay ±25% so a flapping
                        // network doesn't produce a metronome-perfect retry cadence
                        // that's trivially fingerprintable as a bot.
                        var thisDelay = retryDelay;
                        if (_settingsService.Current.SafeMode)
                        {
                            var factor = 0.75 + (Random.Shared.NextDouble() * 0.5);
                            thisDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * factor);
                        }
                        _logger.LogWarning(ex, "Attempt {Attempt} failed for target {TargetId}, retrying in {Delay:F1}s...",
                            attempt, target.TargetId, thisDelay.TotalSeconds);

                        await Task.Delay(thisDelay, ct);
                    }
                }
            }
        }

        ReportProgress(job.Id, new JobProgress(
            job.Id,
            JobStatus.Completed,
            completedCount,
            totalCount,
            100,
            null,
            "Job completed"
        ));
    }

    private async Task<(int Found, int Downloaded, long Bytes)> DownloadArtistAsync(
        Guid jobId,
        DownloadTarget target,
        SettingsOverride settings,
        CancellationToken ct,
        int jobArtworkOffset = 0,
        int jobArtworkTotal = 0,
        Action<int>? onArtistTotalKnown = null,
        Action<string>? onOutputFolder = null,
        Action<string>? onFirstThumbnail = null)
    {
        // Get all artwork IDs for this artist
        var profile = await _client.GetUserProfileAllAsync(target.TargetId, ct);
        var allArtworkIds = profile.AllArtworkIds();

        if (allArtworkIds.Count == 0)
            return (0, 0, 0);

        // Cap to the N most-recent artworks when requested (IDs are already newest-first)
        if (settings.MaxArtworksPerArtist is > 0)
            allArtworkIds = allArtworkIds.Take(settings.MaxArtworksPerArtist.Value).ToList();

        // Pre-parse per-target tag/date filters (case-insensitive substring match for tags)
        var includeTagSet = ParseTagSet(settings.IncludeTags);
        var excludeTagSet = ParseTagSet(settings.ExcludeTagsFilter);
        var dateFromUtc = settings.DateFrom?.Date;
        var dateToUtc = settings.DateTo?.Date.AddDays(1).AddTicks(-1); // include the entire 'To' day

        // Fetch metadata in batches of 48 (newest-first). When a DateFrom cutoff is active,
        // stop early once every artwork in a batch predates the cutoff — no need to scan
        // the entire gallery for date-limited schedules.
        const int batchSize = 48;
        var allMetadata = new Dictionary<string, ArtworkPreview>();
        for (int i = 0; i < allArtworkIds.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = allArtworkIds.Skip(i).Take(batchSize).ToList();
            var batchMeta = await _client.GetArtworksMetadataAsync(target.TargetId, batch, ct);
            foreach (var kv in batchMeta)
                allMetadata[kv.Key] = kv.Value;

            // Early-stop: if every artwork in this batch is older than the cutoff, all
            // remaining IDs (which are even older) will also be filtered out.
            if (dateFromUtc.HasValue && batchMeta.Count > 0 &&
                batchMeta.Values.All(a => a.CreateDate.HasValue && a.CreateDate.Value.UtcDateTime < dateFromUtc.Value))
            {
                break;
            }
        }

        // Apply page range filter
        var pageRange = target.HasCustomPageRange
            ? PageRangeParser.Parse(target.PageRange)
            : PageRangeParser.Parse("0"); // All pages

        // Artworks that pass all content filters (ignoring the resume checkpoint). This is
        // the artist's FULL total for this job and must stay stable across pause/resume so
        // the UI progress doesn't "drop" when resuming (which made it look like a new job).
        var completedIds = target.CompletedArtworkIds;
        var matchedArtworks = allMetadata.Values.Where(artwork =>
        {
            if (settings.FilterAiGenerated == true && artwork.IsAiGenerated) return false;
            if (settings.SkipManga   == true && artwork.IllustType == 1) return false;
            if (settings.SkipUgoira  == true && artwork.IllustType == 2) return false;
            if (settings.SkipR18     == true && artwork.IsR18)           return false;
            if (settings.SkipR18G    == true && artwork.IsR18G)          return false;
            if (!MatchesTagFilters(artwork.Tags, includeTagSet, excludeTagSet)) return false;
            if (!MatchesDateRange(artwork.CreateDate, dateFromUtc, dateToUtc)) return false;
            if (_settingsService.Current.IsArtworkBlockedFromDownload(artwork.UserId, artwork.UserName, artwork.Title, artwork.Tags)) return false;
            return true;
        }).ToList();

        // The subset still to download this run = matched minus those already completed
        // in a previous run (resume checkpoint).
        var filteredArtworks = matchedArtworks
            .Where(a => !completedIds.Contains(a.Id))
            .ToList();

        // Stable full total + how many were already done before this run started, so the
        // reported "completed / total" continues from where it left off instead of 0/N.
        int fullTotal = matchedArtworks.Count;
        int alreadyDone = fullTotal - filteredArtworks.Count;

        // Persist FoundItems immediately so if the job is paused before completing,
        // the total artwork count is already in the DB for display on next launch.
        if (target.FoundItems != fullTotal)
        {
            target.FoundItems = fullTotal;
            _ = _jobRepository.UpdateFoundItemsAsync(target.Id, fullTotal);
        }

        // Notify caller of the confirmed total so job-wide sum can be updated.
        onArtistTotalKnown?.Invoke(fullTotal);
        // effectiveJobTotal = offset from prior targets + this artist's full total.
        // The caller's jobArtworkTotal now reflects all known targets after the callback.
        var effectiveJobTotal = jobArtworkOffset + fullTotal;

        // Download avatar + banner before artwork loop when option is enabled
        var downloadProfileImages = settings.DownloadAvatarAndBanner ?? _settingsService.Current.DownloadAvatarAndBanner;
        if (downloadProfileImages && filteredArtworks.Count > 0)
        {
            try
            {
                var sampleArtwork = filteredArtworks[0];
                var artistFolder = _downloadService.ResolveArtistFolder(sampleArtwork, settings);
                Directory.CreateDirectory(artistFolder);

                var userInfo = await _client.GetArtistFullAsync(target.TargetId, ct).ConfigureAwait(false);
                if (userInfo != null)
                {
                    // Avatar (big version preferred)
                    var avatarUrl = userInfo.ImageBigUrl ?? userInfo.ImageUrl;
                    if (!string.IsNullOrWhiteSpace(avatarUrl))
                    {
                        var avatarExt = Path.GetExtension(new Uri(avatarUrl).AbsolutePath);
                        if (string.IsNullOrWhiteSpace(avatarExt)) avatarExt = ".jpg";
                        var avatarDest = Path.Combine(artistFolder, $"avatar{avatarExt}");
                        if (!File.Exists(avatarDest))
                        {
                            await _downloadService.DownloadGenericFileAsync(avatarUrl, avatarDest, "https://www.pixiv.net/", ct).ConfigureAwait(false);
                            _logger.LogInformation("Saved avatar for {UserId} -> {Path}", target.TargetId, avatarDest);
                        }
                    }

                    // Banner/background (only present with ?full=1)
                    var bannerUrl = userInfo.Background?.Url;
                    if (!string.IsNullOrWhiteSpace(bannerUrl) && userInfo.Background?.IsPrivate != true)
                    {
                        var bannerExt = Path.GetExtension(new Uri(bannerUrl).AbsolutePath);
                        if (string.IsNullOrWhiteSpace(bannerExt)) bannerExt = ".jpg";
                        var bannerDest = Path.Combine(artistFolder, $"banner{bannerExt}");
                        if (!File.Exists(bannerDest))
                        {
                            await _downloadService.DownloadGenericFileAsync(bannerUrl, bannerDest, "https://www.pixiv.net/", ct).ConfigureAwait(false);
                            _logger.LogInformation("Saved banner for {UserId} -> {Path}", target.TargetId, bannerDest);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download avatar/banner for artist {UserId} — continuing with artworks", target.TargetId);
            }
        }

        int downloaded = 0;
        long bytesThisRun = 0;
        int artworkIndex = 0;
        bool outputFolderCaptured = false;
        bool thumbnailCaptured = false;

        // Degree of parallelism: SafeMode forces 1 via the download semaphore, but we
        // also cap the outer loop so the inter-artwork delay fires sequentially in SafeMode.
        var parallelDegree = _settingsService.Current.SafeMode
            ? 1
            : Math.Max(1, settings.MaxConcurrentDownloads ?? _settingsService.Current.MaxConcurrentDownloads);

        await Parallel.ForEachAsync(filteredArtworks,
            new ParallelOptions { MaxDegreeOfParallelism = parallelDegree, CancellationToken = ct },
            async (artwork, innerCt) =>
            {
                var idx = Interlocked.Increment(ref artworkIndex);
                var completedSoFar = alreadyDone + Volatile.Read(ref downloaded);
                // Report job-wide counts: offset by artworks from prior targets.
                var jobCompletedSoFar = jobArtworkOffset + completedSoFar;
                ReportProgress(jobId, new JobProgress(
                    jobId, JobStatus.Running,
                    jobCompletedSoFar, effectiveJobTotal,
                    effectiveJobTotal > 0 ? (jobArtworkOffset + alreadyDone + idx) * 100.0 / effectiveJobTotal : 0,
                    artwork.Title,
                    $"Downloading {alreadyDone + idx}/{fullTotal}: {artwork.Title}",
                    CurrentThumbnailUrl: artwork.ThumbnailUrl));

                try
                {
                    List<int>? pageIndices = null;
                    if (!pageRange.IsAll)
                    {
                        var pages = await _client.GetArtworkPagesAsync(artwork.Id, innerCt);
                        if (pages.Count == 0)
                            throw new InvalidOperationException($"Artwork {artwork.Id} returned no pages (possibly deleted, private, or rate-limited)");
                        pageIndices = pageRange.ToZeroBasedIndices()
                            .Where(i => i < pages.Count)
                            .ToList();
                        if (pageIndices.Count == 0) return;
                    }

                    // Inter-artwork pacing (sequential in SafeMode; parallel runs skip fixed delay
                    // since the download semaphore already gates throughput).
                    // Read live so the user can adjust delay/SafeMode without restarting the job.
                    if (parallelDegree == 1)
                    {
                        var liveGlobal = _settingsService.Current;
                        var delaySec = settings.DownloadDelaySeconds ?? liveGlobal.DownloadDelaySeconds;
                        if (liveGlobal.SafeMode)
                        {
                            var jittered = 2.0 + (Random.Shared.NextDouble() * 2.0);
                            if (delaySec < jittered) delaySec = (int)Math.Ceiling(jittered);
                        }
                        if (delaySec > 0)
                            await Task.Delay(TimeSpan.FromSeconds(delaySec), innerCt);
                    }

                    var savedFiles = await _downloadService.DownloadArtworkPagesAsync(artwork, pageIndices, null, innerCt, settings);
                    var dl = Interlocked.Increment(ref downloaded);
                    var artworkBytes = savedFiles.Sum(f => { try { return new System.IO.FileInfo(f).Length; } catch { return 0L; } });
                    Interlocked.Add(ref bytesThisRun, artworkBytes);

                    if (artworkBytes > 0)
                    {
                        _ = _jobRepository.AddBytesAsync(target.Id, artworkBytes);
                        var completedNow = alreadyDone + dl;
                        var jobCompletedNow = jobArtworkOffset + completedNow;
                        ReportProgress(jobId, new JobProgress(
                            jobId, JobStatus.Running,
                            jobCompletedNow, effectiveJobTotal,
                            effectiveJobTotal > 0 ? jobCompletedNow * 100.0 / effectiveJobTotal : 0,
                            artwork.Title, null,
                            ArtworkBytesCompleted: artworkBytes));
                    }

                    lock (target.CompletedArtworkIds)
                    {
                        if (!target.CompletedArtworkIds.Contains(artwork.Id))
                        {
                            target.CompletedArtworkIds.Add(artwork.Id);
                            _ = _jobRepository.AppendCompletedArtworkIdAsync(target.Id, artwork.Id, innerCt);
                        }
                    }

                    if (savedFiles.Count > 0 && !Volatile.Read(ref outputFolderCaptured))
                    {
                        Volatile.Write(ref outputFolderCaptured, true);
                        onOutputFolder?.Invoke(Path.GetDirectoryName(savedFiles[0])!);
                    }
                    if (!string.IsNullOrEmpty(artwork.ThumbnailUrl) && !Volatile.Read(ref thumbnailCaptured))
                    {
                        Volatile.Write(ref thumbnailCaptured, true);
                        onFirstThumbnail?.Invoke(artwork.ThumbnailUrl);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download artwork {ArtworkId} from artist {ArtistId}",
                        artwork.Id, target.TargetId);
                }
            });

        // Report the FULL totals (stable across pause/resume) and the CUMULATIVE number
        // downloaded (prior runs + this run), so the persisted target counts — and the
        // completed-job artwork summary — reflect the whole job, not just the last run.
        // The silent-success guard still triggers correctly: fullTotal>0 with cumulative==0
        // means nothing was ever downloaded.
        return (fullTotal, alreadyDone + downloaded, bytesThisRun);
    }

    private static HashSet<string> ParseTagSet(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesTagFilters(IReadOnlyList<string> tags, HashSet<string> include, HashSet<string> exclude)
    {
        // Exclude wins
        if (exclude.Count > 0 && tags.Any(t => exclude.Any(ex => t.Contains(ex, StringComparison.OrdinalIgnoreCase))))
            return false;
        // Include = at least one tag must match (substring, case-insensitive)
        if (include.Count > 0 && !tags.Any(t => include.Any(inc => t.Contains(inc, StringComparison.OrdinalIgnoreCase))))
            return false;
        return true;
    }

    private static bool MatchesDateRange(DateTimeOffset? createDate, DateTime? from, DateTime? to)
    {
        if (from == null && to == null) return true;
        if (createDate == null) return true; // unknown date → don't filter out
        var d = createDate.Value.UtcDateTime;
        if (from.HasValue && d < from.Value) return false;
        if (to.HasValue && d > to.Value) return false;
        return true;
    }

    private async Task<(int Found, int Downloaded)> DownloadArtworkAsync(
        DownloadTarget target,
        SettingsOverride settings,
        CancellationToken ct,
        Action<string>? onOutputFolder = null)
    {
        var detail = await _client.GetArtworkDetailAsync(target.TargetId, ct);
        if (detail == null)
            return (0, 0);

        // Apply content filters
        if (settings.FilterAiGenerated == true && detail.AiType is 1 or 2) return (1, 0);
        if (settings.SkipR18          == true && detail.XRestrict >= 1)    return (1, 0);
        if (settings.SkipR18G         == true && detail.XRestrict == 2)    return (1, 0);

        var detailTags = detail.Tags?.Tags.Select(t => t.Tag ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>();
        if (_settingsService.Current.IsArtworkBlockedFromDownload(detail.UserId, detail.UserName, detail.IllustTitle, detailTags))
            return (1, 0);

        var pages = await _client.GetArtworkPagesAsync(target.TargetId, ct);
        if (pages.Count == 0)
        {
            throw new InvalidOperationException($"Artwork {target.TargetId} returned no pages (possibly deleted, private, or rate-limited)");
        }

        // Apply page range
        var pageRange = target.HasCustomPageRange
            ? PageRangeParser.Parse(target.PageRange)
            : PageRangeParser.Parse("0");

        List<int>? pageIndices = null;
        if (!pageRange.IsAll)
        {
            pageIndices = pageRange.ToZeroBasedIndices()
                .Where(i => i < pages.Count)
                .ToList();
        }

        // Create ArtworkPreview from detail response
        var artworkPreview = new ArtworkPreview
        {
            Id = detail.IllustId ?? target.TargetId,
            Title = detail.IllustTitle ?? "",
            UserId = detail.UserId ?? "",
            UserName = detail.UserName ?? "",
            ThumbnailUrl = detail.ThumbnailUrl ?? "",
            PageCount = detail.PageCount,
            Width = detail.Width,
            Height = detail.Height,
            AiType = detail.AiType,
            Tags = detail.Tags?.Tags.Select(t => t.Tag ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>()
        };

        var savedFiles = await _downloadService.DownloadArtworkPagesAsync(artworkPreview, pageIndices, null, ct, settings);
        if (savedFiles.Count > 0)
            onOutputFolder?.Invoke(Path.GetDirectoryName(savedFiles[0])!);

        return (1, 1);
    }

    private async Task<(int Found, int Downloaded)> DownloadFanboxPostAsync(
        DownloadTarget target,
        SettingsOverride settings,
        CancellationToken ct)
    {
        try
        {
            var post = await _fanboxClient.GetPostAsync(target.TargetId, ct);
            if (post == null)
            {
                _logger.LogWarning("FANBOX post {PostId} not found", target.TargetId);
                return (0, 0);
            }

            var s = _settingsService.Current;
            var downloadRoot = !string.IsNullOrWhiteSpace(settings.DownloadRoot) && !settings.UseGlobalSettings
                ? settings.DownloadRoot
                : s.DownloadRoot;
            
            // Create artist folder
            var artistName = post.User?.Name ?? post.CreatorId;
            var artistId = post.UserId ?? post.CreatorId;
            var artistFolder = Path.Combine(downloadRoot, $"FANBOX {artistName} ({artistId})");
            Directory.CreateDirectory(artistFolder);

            // Download cover image
            if (!string.IsNullOrEmpty(post.CoverImageUrl))
            {
                try
                {
                    var coverFilename = ApplyTemplate(s.FilenameFanboxCover, post, post.User);
                    var coverPath = Path.Combine(artistFolder, SanitizeFilename(coverFilename));
                    await DownloadFileFromUrlAsync(post.CoverImageUrl, coverPath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download FANBOX cover for post {PostId}", post.Id);
                }
            }

            // Download content images
            var downloadedCount = 0;
            foreach (var image in post.Images)
            {
                try
                {
                    var imageFilename = ApplyTemplate(s.FilenameFanboxContent, post, post.User, image.Extension);
                    var imagePath = Path.Combine(artistFolder, SanitizeFilename(imageFilename));
                    await DownloadFileFromUrlAsync(image.OriginalUrl, imagePath, ct);
                    downloadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download FANBOX image {ImageId} for post {PostId}", image.Id, post.Id);
                }
            }

            // Write metadata/info file
            if (s.WriteFanboxHtml || s.WriteImageInfo)
            {
                var infoFilename = ApplyTemplate(s.FilenameFanboxInfo, post, post.User, "txt");
                var infoPath = Path.Combine(artistFolder, SanitizeFilename(infoFilename));
                
                var info = new StringBuilder();
                info.AppendLine($"Title: {post.Title}");
                info.AppendLine($"Post ID: {post.Id}");
                info.AppendLine($"Creator: {artistName} ({artistId})");
                info.AppendLine($"Published: {post.PublishedDatetime:yyyy-MM-dd HH:mm:ss}");
                info.AppendLine($"Fee Required: {post.FeeRequired} JPY");
                info.AppendLine($"Adult Content: {post.HasAdultContent}");
                info.AppendLine($"Image Count: {post.Images.Count}");
                
                if (!string.IsNullOrEmpty(post.Body?.Text))
                {
                    info.AppendLine($"\nBody:\n{post.Body.Text}");
                }

                await File.WriteAllTextAsync(infoPath, info.ToString(), ct);
            }

            return (1, downloadedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download FANBOX post {PostId}", target.TargetId);
            return (0, 0);
        }
    }

    private const string FanboxReferer = "https://www.fanbox.cc/";

    // Routes FANBOX downloads through PixivDownloadService.DownloadGenericFileAsync
    // so the SafeMode 429/503 backoff + Retry-After protections apply uniformly
    // (the previous raw HttpClient call bypassed all of that).
    private Task DownloadFileFromUrlAsync(string url, string filePath, CancellationToken ct)
        => _downloadService.DownloadGenericFileAsync(url, filePath, FanboxReferer, ct);

    private string ApplyTemplate(string template, FanboxPost post, FanboxUser? user, string? extension = null)
    {
        var result = template
            .Replace("%artist%", user?.Name ?? post.CreatorId)
            .Replace("%member_id%", user?.UserId ?? post.CreatorId)
            .Replace("%creator_id%", post.CreatorId)
            .Replace("%image_id%", post.Id)
            .Replace("%title%", post.Title)
            .Replace("%urlFilename%", post.Id)
            .Replace("%image_ext%", extension ?? "jpg");

        return result;
    }

    private string SanitizeFilename(string filename)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", filename.Split(invalidChars));
    }

    #endregion

    public void Dispose()
    {
        // Cancel all active jobs
        foreach (var cts in _activeJobs.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _activeJobs.Clear();
        _runningTasks.Clear();
        _progressListeners.Clear();
    }
}

/// <summary>
/// Event arguments for job completion events.
/// </summary>
public class JobCompletedEventArgs : EventArgs
{
    public DownloadJob Job { get; }

    public JobCompletedEventArgs(DownloadJob job)
    {
        Job = job;
    }
}
