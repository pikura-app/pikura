using System.Text.Json.Serialization;

namespace Pikura.Core.Models;

/// <summary>Type of download job.</summary>
public enum DownloadJobType
{
    Artist,      // Download by artist ID(s)
    ImageId,     // Batch download by artwork ID(s)
    BookmarkArtist, // Download from bookmarked artists
    BookmarkImage,  // Download from bookmarked images
    ListFile,    // Download from list.txt or tags.txt
    Search,      // Download by search criteria
    Fanbox,      // Download from FANBOX
}

/// <summary>Status of a download job.</summary>
public enum JobStatus
{
    Pending   = 0,  // Ready to run, waiting for concurrent slot
    Running   = 1,  // Actively executing
    Paused    = 2,  // User-paused, can be resumed
    Completed = 3,  // Finished successfully
    Failed    = 4,  // Finished with errors
    Cancelled = 5,  // Cancelled by user
}

/// <summary>Type of download target.</summary>
public enum TargetType
{
    Artist,      // User/artist profile
    Artwork,     // Single artwork/image
    Post,        // FANBOX post
    Tag,         // Search tag
    Group,       // Pixiv group
}

/// <summary>Status of an individual download target.</summary>
public enum TargetStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>
/// A batch download job containing one or more targets.
/// Persisted to database for queue management and recovery.
/// </summary>
public sealed class DownloadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-defined name for this job.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Type of download operation.</summary>
    public DownloadJobType Type { get; set; }

    /// <summary>Current status of the job.</summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>Individual download targets (artists, images, etc.).</summary>
    public List<DownloadTarget> Targets { get; set; } = new();

    /// <summary>Settings override for this job. If UseGlobalSettings=true, uses AppSettings.</summary>
    public SettingsOverride Settings { get; set; } = new();

    #region Progress Tracking

    public int TotalItems => Targets.Count;
    public int CompletedItems => Targets.Count(t => t.Status == TargetStatus.Completed);
    public int FailedItems => Targets.Count(t => t.Status == TargetStatus.Failed);
    public int SkippedItems => Targets.Count(t => t.Status == TargetStatus.Skipped);

    /// <summary>Overall progress percentage (0-100).</summary>
    public double ProgressPercent => TotalItems > 0
        ? (CompletedItems + FailedItems + SkippedItems) * 100.0 / TotalItems
        : 0;

    #endregion

    #region Timestamps

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastRetriedAt { get; set; }

    #endregion

    #region Error Handling

    /// <summary>Error message if the entire job failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts for failed items.</summary>
    public int RetryCount { get; set; }

    /// <summary>Local folder where downloaded files were saved. Null for coordinator jobs (folder is per-target).</summary>
    public string? OutputFolder { get; set; }

    /// <summary>Display order for pending/paused/running jobs (lower = first).</summary>
    public int SortOrder { get; set; }

    #endregion
}

/// <summary>
/// An individual download target within a job.
/// Represents one artist, artwork, post, etc. to download.
/// </summary>
public sealed class DownloadTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Foreign key to parent job.</summary>
    [JsonIgnore]
    public Guid JobId { get; set; }

    /// <summary>Target ID (artist ID, image ID, post ID, etc.).</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Display name (artist name, image title, etc.).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Thumbnail URL for display in history.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>Artist/user display name.</summary>
    public string? UserName { get; set; }

    /// <summary>Artist/user ID.</summary>
    public string? UserId { get; set; }

    /// <summary>Type of target.</summary>
    public TargetType Type { get; set; }

    #region Image Download Properties

    /// <summary>Original image URL for downloading.</summary>
    public string? OriginalUrl { get; set; }

    /// <summary>Override URL for downloading (takes priority over OriginalUrl).</summary>
    public string? OverrideUrl { get; set; }

    /// <summary>Page index for multi-page artworks.</summary>
    public int PageIndex { get; set; }

    /// <summary>Total page count for multi-page artworks.</summary>
    public int PageCount { get; set; }

    #endregion

    #region Page Range Configuration

    /// <summary>
    /// Page range string (e.g., "0", "2", "1-5", "2,4,6-10").
    /// Null means "use job default".
    /// </summary>
    public string? PageRange { get; set; }

    /// <summary>Whether this target has a custom page range.</summary>
    public bool HasCustomPageRange => !string.IsNullOrEmpty(PageRange);

    #endregion

    #region Per-Target Settings Override

    /// <summary>
    /// Optional custom settings for this specific target.
    /// Takes highest priority if present.
    /// </summary>
    public SettingsOverride? CustomSettings { get; set; }

    /// <summary>Whether this target has custom settings.</summary>
    public bool HasCustomSettings => CustomSettings != null && !CustomSettings.UseGlobalSettings;

    #endregion

    #region Status Tracking

    public TargetStatus Status { get; set; } = TargetStatus.Pending;

    /// <summary>Error message if target failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of items (artworks) found for this target.</summary>
    public int FoundItems { get; set; }

    /// <summary>Number of items successfully downloaded.</summary>
    public int DownloadedItems { get; set; }

    /// <summary>Total bytes successfully downloaded for this target.</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>Timestamp when this target was processed.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Artwork IDs already successfully downloaded for this artist target.
    /// Used to skip already-finished artworks when resuming after pause.
    /// </summary>
    public List<string> CompletedArtworkIds { get; set; } = new();

    #endregion

    #region Navigation

    [JsonIgnore]
    public DownloadJob? Job { get; set; }

    #endregion

    #region Constructors

    /// <summary>Default constructor for serialization.</summary>
    public DownloadTarget() { }

    /// <summary>Constructor for artwork downloads with preset processing.</summary>
    public DownloadTarget(
        string workId,
        string title,
        string? userName,
        string? userId,
        string? originalUrl,
        int pageIndex = 0,
        int pageCount = 1)
    {
        TargetId = workId;
        Name = title;
        UserName = userName;
        UserId = userId;
        OriginalUrl = originalUrl;
        PageIndex = pageIndex;
        PageCount = pageCount;
        Type = TargetType.Artwork;
    }

    #endregion
}
