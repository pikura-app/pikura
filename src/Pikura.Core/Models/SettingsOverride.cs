namespace Pikura.Core.Models;

/// <summary>Controls how far back a scheduled download looks for new artworks.</summary>
public enum ScheduleDateLimitMode
{
    /// <summary>No date limit — download all artworks.</summary>
    None = 0,
    /// <summary>Only artworks posted since this schedule last ran.</summary>
    SinceLastRun = 1,
    /// <summary>Only artworks posted within the last 24 hours.</summary>
    Last1Day = 2,
    /// <summary>Only artworks posted within the last 7 days.</summary>
    Last7Days = 3,
    /// <summary>Only artworks posted within the last 30 days.</summary>
    Last30Days = 4,
}

/// <summary>
/// A subset of AppSettings that can be overridden per-job or per-target.
/// When UseGlobalSettings is true, all other properties are ignored.
/// </summary>
public sealed class SettingsOverride
{
    /// <summary>
    /// When true, uses global AppSettings instead of these overrides.
    /// This is the default behavior.
    /// </summary>
    public bool UseGlobalSettings { get; set; } = true;

    #region Location

    /// <summary>Custom download root folder (null = use global DownloadRoot).</summary>
    public string? DownloadRoot { get; set; }

    #endregion

    #region Per-Target Filtering

    /// <summary>Comma-separated tags that an artwork must have at least one of (null/empty = no filter).</summary>
    public string? IncludeTags { get; set; }

    /// <summary>Comma-separated tags that exclude an artwork if it has any of them (null/empty = no filter).</summary>
    public string? ExcludeTagsFilter { get; set; }

    /// <summary>Only download artworks created on or after this date (null = no lower bound).</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>Only download artworks created on or before this date (null = no upper bound).</summary>
    public DateTime? DateTo { get; set; }

    #endregion

    #region Naming Templates

    /// <summary>Folder template (null = use global).</summary>
    public string? FolderTemplate { get; set; }

    /// <summary>Filename template for single-page artworks.</summary>
    public string? FilenameTemplate { get; set; }

    /// <summary>Filename template for multi-page artworks.</summary>
    public string? FilenameMangaFormat { get; set; }

    /// <summary>Filename template for info text files.</summary>
    public string? FilenameInfoFormat { get; set; }

    /// <summary>Date format string.</summary>
    public string? DateFormat { get; set; }

    /// <summary>Tags separator character.</summary>
    public string? TagsSeparator { get; set; }

    #endregion

    #region Download Behavior

    /// <summary>Create subfolder for each submission.</summary>
    public bool? CreateSubfolderPerSubmission { get; set; }

    /// <summary>Separate R-18 into subfolder.</summary>
    public bool? SeparateR18Folder { get; set; }

    /// <summary>Overwrite mode: 0=skip, 1=overwrite, 2=backup.</summary>
    public int? OverwriteMode { get; set; }

    /// <summary>Backup existing file before overwrite.</summary>
    public bool? BackupOldFile { get; set; }

    /// <summary>Also save the artist's avatar and banner to their folder.</summary>
    public bool? DownloadAvatarAndBanner { get; set; }

    #endregion

    #region Limits & Filtering

    /// <summary>Maximum concurrent downloads.</summary>
    public int? MaxConcurrentDownloads { get; set; }

    /// <summary>Minimum file size in KB.</summary>
    public int? MinFileSizeKB { get; set; }

    /// <summary>Maximum file size in KB.</summary>
    public int? MaxFileSizeKB { get; set; }

    /// <summary>Download timeout in seconds.</summary>
    public int? DownloadTimeout { get; set; }

    /// <summary>Retry count for failed downloads.</summary>
    public int? RetryCount { get; set; }

    /// <summary>Auto-retry failed downloads.</summary>
    public bool? AutoRetryFailedDownloads { get; set; }

    /// <summary>Maximum retry attempts for failed downloads.</summary>
    public int? MaxRetryAttempts { get; set; }

    /// <summary>Delay between retries in seconds.</summary>
    public int? RetryDelaySeconds { get; set; }

    /// <summary>Delay between downloads in seconds (rate limiting).</summary>
    public int? DownloadDelaySeconds { get; set; }

    /// <summary>Filter AI-generated content.</summary>
    public bool? FilterAiGenerated { get; set; }

    /// <summary>Skip manga-type artworks (IllustType == 1).</summary>
    public bool? SkipManga { get; set; }

    /// <summary>Skip ugoira-type artworks (IllustType == 2).</summary>
    public bool? SkipUgoira { get; set; }

    /// <summary>Skip R-18 / R-18G artworks (XRestrict >= 1).</summary>
    public bool? SkipR18 { get; set; }

    /// <summary>Skip R-18G artworks (XRestrict == 2).</summary>
    public bool? SkipR18G { get; set; }

    /// <summary>Controls how far back a scheduled download looks for new artworks.</summary>
    public ScheduleDateLimitMode DateLimitMode { get; set; } = ScheduleDateLimitMode.None;

    /// <summary>Cap the number of artworks fetched per artist (0 or null = no limit).</summary>
    public int? MaxArtworksPerArtist { get; set; }

    /// <summary>When true, re-download files even if they already exist on disk.</summary>
    public bool? AllowRedownload { get; set; }

    #endregion

    #region Metadata Export

    /// <summary>Write JSON metadata.</summary>
    public bool? WriteImageJSON { get; set; }

    /// <summary>Write info text file.</summary>
    public bool? WriteImageInfo { get; set; }

    /// <summary>Write raw Pixiv API response.</summary>
    public bool? WriteRawJSON { get; set; }

    /// <summary>Include series metadata.</summary>
    public bool? IncludeSeriesJSON { get; set; }

    /// <summary>Write XMP metadata.</summary>
    public bool? WriteImageXMP { get; set; }

    /// <summary>Verify downloaded images.</summary>
    public bool? VerifyImage { get; set; }

    /// <summary>Image resize/edit preset for post-processing.</summary>
    public ImageEditPreset? ImagePreset { get; set; }

    /// <summary>Custom output folder for this download (null = use global).</summary>
    public string? CustomOutputFolder { get; set; }

    /// <summary>Custom filename template (null = use global).</summary>
    public string? FileNameTemplate { get; set; }

    #endregion

    /// <summary>
    /// Applies this override to base settings, returning the effective settings.
    /// </summary>
    public SettingsOverride ApplyTo(SettingsOverride? baseSettings)
    {
        if (baseSettings == null || baseSettings.UseGlobalSettings)
            return this;

        return new SettingsOverride
        {
            UseGlobalSettings = false,
            DownloadRoot = DownloadRoot ?? baseSettings.DownloadRoot,
            IncludeTags = IncludeTags ?? baseSettings.IncludeTags,
            ExcludeTagsFilter = ExcludeTagsFilter ?? baseSettings.ExcludeTagsFilter,
            DateFrom = DateFrom ?? baseSettings.DateFrom,
            DateTo = DateTo ?? baseSettings.DateTo,
            FolderTemplate = FolderTemplate ?? baseSettings.FolderTemplate,
            FilenameTemplate = FilenameTemplate ?? baseSettings.FilenameTemplate,
            FilenameMangaFormat = FilenameMangaFormat ?? baseSettings.FilenameMangaFormat,
            FilenameInfoFormat = FilenameInfoFormat ?? baseSettings.FilenameInfoFormat,
            DateFormat = DateFormat ?? baseSettings.DateFormat,
            TagsSeparator = TagsSeparator ?? baseSettings.TagsSeparator,
            CreateSubfolderPerSubmission = CreateSubfolderPerSubmission ?? baseSettings.CreateSubfolderPerSubmission,
            SeparateR18Folder = SeparateR18Folder ?? baseSettings.SeparateR18Folder,
            OverwriteMode = OverwriteMode ?? baseSettings.OverwriteMode,
            BackupOldFile = BackupOldFile ?? baseSettings.BackupOldFile,
            DownloadAvatarAndBanner = DownloadAvatarAndBanner ?? baseSettings.DownloadAvatarAndBanner,
            MaxConcurrentDownloads = MaxConcurrentDownloads ?? baseSettings.MaxConcurrentDownloads,
            MinFileSizeKB = MinFileSizeKB ?? baseSettings.MinFileSizeKB,
            MaxFileSizeKB = MaxFileSizeKB ?? baseSettings.MaxFileSizeKB,
            DownloadTimeout = DownloadTimeout ?? baseSettings.DownloadTimeout,
            RetryCount = RetryCount ?? baseSettings.RetryCount,
            AutoRetryFailedDownloads = AutoRetryFailedDownloads ?? baseSettings.AutoRetryFailedDownloads,
            MaxRetryAttempts = MaxRetryAttempts ?? baseSettings.MaxRetryAttempts,
            RetryDelaySeconds = RetryDelaySeconds ?? baseSettings.RetryDelaySeconds,
            DownloadDelaySeconds = DownloadDelaySeconds ?? baseSettings.DownloadDelaySeconds,
            FilterAiGenerated = FilterAiGenerated ?? baseSettings.FilterAiGenerated,
            SkipManga = SkipManga ?? baseSettings.SkipManga,
            SkipUgoira = SkipUgoira ?? baseSettings.SkipUgoira,
            SkipR18 = SkipR18 ?? baseSettings.SkipR18,
            SkipR18G = SkipR18G ?? baseSettings.SkipR18G,
            DateLimitMode = DateLimitMode != ScheduleDateLimitMode.None ? DateLimitMode : baseSettings.DateLimitMode,
            MaxArtworksPerArtist = MaxArtworksPerArtist ?? baseSettings.MaxArtworksPerArtist,
            WriteImageJSON = WriteImageJSON ?? baseSettings.WriteImageJSON,
            WriteImageInfo = WriteImageInfo ?? baseSettings.WriteImageInfo,
            WriteRawJSON = WriteRawJSON ?? baseSettings.WriteRawJSON,
            IncludeSeriesJSON = IncludeSeriesJSON ?? baseSettings.IncludeSeriesJSON,
            WriteImageXMP = WriteImageXMP ?? baseSettings.WriteImageXMP,
            VerifyImage = VerifyImage ?? baseSettings.VerifyImage,
            ImagePreset = ImagePreset ?? baseSettings.ImagePreset,
            CustomOutputFolder = CustomOutputFolder ?? baseSettings.CustomOutputFolder,
            FileNameTemplate = FileNameTemplate ?? baseSettings.FileNameTemplate,
        };
    }

    /// <summary>
    /// Creates a SettingsOverride from global AppSettings for use as defaults.
    /// </summary>
    public static SettingsOverride FromGlobalSettings(Core.Settings.AppSettings global)
    {
        return new SettingsOverride
        {
            UseGlobalSettings = false, // Override with explicit values
            DownloadRoot = global.DownloadRoot,
            IncludeTags = null,
            ExcludeTagsFilter = null,
            DateFrom = null,
            DateTo = null,
            FolderTemplate = global.FolderTemplate,
            FilenameTemplate = global.FilenameTemplate,
            FilenameMangaFormat = global.FilenameMangaFormat,
            FilenameInfoFormat = global.FilenameInfoFormat,
            DateFormat = global.DateFormat,
            TagsSeparator = global.TagsSeparator,
            CreateSubfolderPerSubmission = global.CreateSubfolderPerSubmission,
            SeparateR18Folder = global.SeparateR18Folder,
            OverwriteMode = global.OverwriteMode,
            BackupOldFile = global.BackupOldFile,
            DownloadAvatarAndBanner = global.DownloadAvatarAndBanner,
            MaxConcurrentDownloads = global.MaxConcurrentDownloads,
            MinFileSizeKB = global.MinFileSizeKB,
            MaxFileSizeKB = global.MaxFileSizeKB,
            DownloadTimeout = global.DownloadTimeout,
            RetryCount = global.RetryCount,
            AutoRetryFailedDownloads = global.AutoRetryFailedDownloads,
            MaxRetryAttempts = global.MaxRetryAttempts,
            RetryDelaySeconds = global.RetryDelaySeconds,
            DownloadDelaySeconds = global.DownloadDelaySeconds,
            FilterAiGenerated = global.FilterAiGenerated,
            WriteImageJSON = global.WriteImageJSON,
            WriteImageInfo = global.WriteImageInfo,
            WriteRawJSON = global.WriteRawJSON,
            IncludeSeriesJSON = global.IncludeSeriesJSON,
            WriteImageXMP = global.WriteImageXMP,
            VerifyImage = global.VerifyImage,
            ImagePreset = global.ImagePreset,
            CustomOutputFolder = null,
            FileNameTemplate = null,
        };
    }
}
