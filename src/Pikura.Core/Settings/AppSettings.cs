using System.Text.Json.Serialization;
using Pikura.Core.Models;

namespace Pikura.Core.Settings;

/// <summary>R-18 visibility mode.</summary>
public enum R18Mode
{
    /// <summary>R-18 / R-18G content is hidden entirely.</summary>
    Off,
    /// <summary>R-18 content is shown mixed with other content.</summary>
    Show,
    /// <summary>Only R-18 / R-18G content is shown.</summary>
    Only,
}

/// <summary>Which R-18 type to include when filtering.</summary>
public enum R18TypeFilter
{
    /// <summary>Both R-18 and R-18G are included.</summary>
    Both,
    /// <summary>Only R-18 is included (no R-18G).</summary>
    R18,
    /// <summary>Only R-18G is included (no R-18).</summary>
    R18G,
}

/// <summary>
/// User-editable, persisted application settings. Stored as JSON under
/// <c>%APPDATA%\PixivUtil\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Pixiv <c>PHPSESSID</c> cookie value used to authenticate every request.</summary>
    public string PhpSessId { get; set; } = string.Empty;

    /// <summary>Pixiv App API refresh token for OAuth authentication (used for bookmark operations).</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Pixiv user id of the logged-in account (resolved after a successful session check).</summary>
    public string? UserId { get; set; }

    /// <summary>Display name resolved from the logged-in account, for UI only.</summary>
    public string? UserName { get; set; }

    /// <summary>Absolute path to the download root folder.</summary>
    public string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PixivDownloads");

    /// <summary>Maximum number of artworks downloaded in parallel.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Maximum number of download jobs that may run simultaneously (0 = unlimited).</summary>
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>When true, multi-page submissions are saved into their own subfolder
    /// (e.g., "Title (123456)/" instead of "Title (123456)-page1.jpg").</summary>
    public bool CreateFolderForManga { get; set; } = true;

    /// <summary>
    /// When true, multi-page submissions are saved into their own subfolder
    /// (e.g. <c>{artist}/{artworkId}_{title}/{artworkId}_pN.ext</c>) instead of
    /// being dumped flat next to single-page artworks.
    /// </summary>
    public bool CreateSubfolderPerSubmission { get; set; } = false;

    /// <summary>
    /// When true, R-18 / R-18G artworks are placed under an extra <c>R-18</c>
    /// folder within the artist directory.
    /// </summary>
    public bool SeparateR18Folder { get; set; } = false;

    #region Filtering

    /// <summary>R-18 visibility mode.</summary>
    public R18Mode R18Mode { get; set; } = R18Mode.Show;

    /// <summary>Which R-18 type to include when filtering.</summary>
    public R18TypeFilter R18Type { get; set; } = R18TypeFilter.Both;

    /// <summary>When true, AI-generated images (aiType==2) are excluded.</summary>
    public bool FilterAiGenerated { get; set; } = false;

    /// <summary>When true, R-18 and R-18G content is blurred in gallery until clicked.</summary>
    public bool BlurR18Content { get; set; } = false;

    /// <summary>Blur intensity/radius (0-50). Higher = more blur.</summary>
    public int BlurIntensity { get; set; } = 15;

    /// <summary>R-18 toggle state in Gallery (persisted).</summary>
    public bool GalleryShowR18 { get; set; } = false;

    /// <summary>R-18 toggle state in Rankings (persisted).</summary>
    public bool RankingsShowR18 { get; set; } = false;

    /// <summary>Maximum pages to fetch per artist (0 = all).</summary>
    public int MaxPagesPerArtist { get; set; } = 0;

    /// <summary>
    /// Tags that cause an artwork to be hidden from galleries and rankings.
    /// Comparison is case-insensitive. Match is substring-based so "R-18" also
    /// matches "R-18G", and matching a Japanese tag matches any artwork whose
    /// tag list contains it verbatim.
    /// </summary>
    public List<string> ExcludedTags { get; set; } = new();

    #endregion

    #region Naming

    /// <summary>Folder path template, e.g. <c>%artist% (%member_id%)\%R-18%</c>.</summary>
    public string FolderTemplate { get; set; } = "%artist% (%member_id%)";

    /// <summary>Filename template, e.g. <c>%image_id%_p%page_index%_%title%</c>.</summary>
    public string FilenameTemplate { get; set; } = "%image_id%_p%page_index%";

    /// <summary>Date format string for %date% and %works_date% tokens (default yyyy-MM-dd).</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>Separate filename template for manga/multi-page artworks.</summary>
    public string FilenameMangaFormat { get; set; } = "%artist% (%member_id%)\\%image_id% - %title%\\%page_number%";

    /// <summary>Filename template for metadata/info text files.</summary>
    public string FilenameInfoFormat { get; set; } = "%artist% (%member_id%)\\%image_id% - %title%.txt";

    /// <summary>Tags separator character for %tags% token (default: comma).</summary>
    public string TagsSeparator { get; set; } = ", ";

    #endregion

    #region Metadata Export

    /// <summary>When true, writes artwork metadata as JSON file alongside image.</summary>
    public bool WriteImageJSON { get; set; } = false;

    /// <summary>When true, writes human-readable info text file.</summary>
    public bool WriteImageInfo { get; set; } = false;

    /// <summary>When true, writes raw Pixiv API response as JSON.</summary>
    public bool WriteRawJSON { get; set; } = false;

    /// <summary>When true, includes manga series metadata in JSON exports.</summary>
    public bool IncludeSeriesJSON { get; set; } = false;

    /// <summary>When true, embeds XMP metadata into downloaded images.</summary>
    public bool WriteImageXMP { get; set; } = false;

    /// <summary>When true, writes XMP for each page of multi-page works.</summary>
    public bool WriteImageXMPPerImage { get; set; } = false;

    /// <summary>When true, verifies downloaded image integrity (checksum/size).</summary>
    public bool VerifyImage { get; set; } = false;

    /// <summary>When true, preserves Pixiv's last modified timestamp on files.</summary>
    public bool SetLastModified { get; set; } = true;

    /// <summary>When true, uses local timezone instead of UTC for timestamps.</summary>
    public bool UseLocalTimezone { get; set; } = false;

    #endregion

    #region Download Control

    /// <summary>When true, also saves the artist's avatar and banner/background image to their folder.</summary>
    public bool DownloadAvatarAndBanner { get; set; } = false;

    /// <summary>Overwrite behavior: 0=skip, 1=overwrite, 2=backup old file.</summary>
    public int OverwriteMode { get; set; } = 0; // 0=skip, 1=overwrite, 2=backup

    /// <summary>When true, backs up existing file before overwriting (requires OverwriteMode=1).</summary>
    public bool BackupOldFile { get; set; } = false;

    /// <summary>Minimum file size in KB to download (0 = no minimum).</summary>
    public int MinFileSizeKB { get; set; } = 0;

    /// <summary>Maximum file size in KB to download (0 = no maximum).</summary>
    public int MaxFileSizeKB { get; set; } = 0;

    /// <summary>Download timeout in seconds.</summary>
    public int DownloadTimeout { get; set; } = 60;

    /// <summary>Number of retry attempts for failed downloads.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Auto-retry failed downloads.</summary>
    public bool AutoRetryFailedDownloads { get; set; } = true;

    /// <summary>Maximum retry attempts for failed downloads.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay between retry attempts in seconds.</summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>Delay between downloads in seconds (rate limiting).</summary>
    public int DownloadDelaySeconds { get; set; } = 0;

    /// <summary>Buffer size in KB for download operations.</summary>
    public int DownloadBufferKB { get; set; } = 512;

    /// <summary>
    /// Anti-suspension safe mode. When enabled, overrides parts of the download
    /// pipeline to reduce the risk of Pixiv flagging the account for excessive
    /// access (24-72h "unauthorized access attempts" suspensions). Specifically:
    /// <list type="bullet">
    ///   <item>Forces concurrency to 1.</item>
    ///   <item>Enforces a minimum 2-second + 0-2s jittered delay between artworks
    ///   even if <see cref="DownloadDelaySeconds"/> is 0.</item>
    ///   <item>Honors the <c>Retry-After</c> header on HTTP 429 / 503, otherwise
    ///   uses exponential backoff (5s → 10s → 20s → 60s) with ±25% jitter.
    ///   These rate-limit waits don't consume the user's
    ///   <see cref="MaxRetryAttempts"/> budget.</item>
    /// </list>
    /// Default false for new installs; existing installs preserve their setting.
    /// </summary>
    public bool SafeMode { get; set; } = false;

    #endregion

    #region Blacklist Filtering

    /// <summary>Tags that prevent download (exact match or substring).</summary>
    public List<string> BlacklistTags { get; set; } = new();

    /// <summary>When true, uses regex matching for blacklist tags.</summary>
    public bool UseBlacklistTagsRegex { get; set; } = false;

    /// <summary>Title patterns that prevent download.</summary>
    public List<string> BlacklistTitles { get; set; } = new();

    /// <summary>When true, uses regex matching for blacklist titles.</summary>
    public bool UseBlacklistTitlesRegex { get; set; } = false;

    /// <summary>Member IDs that are blacklisted from download.</summary>
    public List<string> BlacklistMembers { get; set; } = new();

    #endregion

    #region Network / Proxy

    /// <summary>When true, uses proxy for all connections.</summary>
    public bool UseProxy { get; set; } = false;

    /// <summary>Proxy server address (e.g., http://127.0.0.1:8080).</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>When true, verifies SSL certificates.</summary>
    public bool EnableSSLVerification { get; set; } = true;

    /// <summary>When true, respects robots.txt.</summary>
    public bool UseRobots { get; set; } = true;

    #endregion

    #region Ugoira (Animated Images)

    /// <summary>When true, converts ugoira to WebM format.</summary>
    public bool CreateUgoiraWebm { get; set; } = false;

    /// <summary>When true, converts ugoira to GIF format.</summary>
    public bool CreateUgoiraGif { get; set; } = false;

    /// <summary>When true, converts ugoira to WebP format.</summary>
    public bool CreateUgoiraWebp { get; set; } = false;

    /// <summary>When true, converts ugoira to APNG format.</summary>
    public bool CreateUgoiraApng { get; set; } = false;

    /// <summary>When true, converts ugoira to MP4 (h264 + yuv420p).</summary>
    public bool CreateUgoiraMp4 { get; set; } = true;

    /// <summary>When true, keeps original ugoira ZIP after conversion.</summary>
    public bool KeepUgoiraZip { get; set; } = true;

    /// <summary>When true, saves individual frames as separate PNG images in a subfolder.</summary>
    public bool SaveUgoiraFrames { get; set; } = false;

    /// <summary>When true, only saves frames without encoding animation (no MP4/GIF/WebM).</summary>
    public bool UgoiraFramesOnly { get; set; } = false;

    /// <summary>FFmpeg codec for WebM conversion (default: libvpx-vp9).</summary>
    public string FFmpegCodec { get; set; } = "libvpx-vp9";

    /// <summary>FFmpeg quality CRF value (lower = better quality, 15-35).</summary>
    public int FFmpegCRF { get; set; } = 15;

    /// <summary>Absolute path to the ffmpeg executable. Empty = auto-detect (PATH or app-managed install).</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Reported ffmpeg version (e.g. "n6.1") after a successful detect/install. Empty when unknown.</summary>
    public string FfmpegInstalledVersion { get; set; } = string.Empty;

    #endregion

    #region FANBOX

    /// <summary>Filename template for FANBOX cover images.</summary>
    public string FilenameFanboxCover { get; set; } = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%";

    /// <summary>Filename template for FANBOX content images.</summary>
    public string FilenameFanboxContent { get; set; } = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%";

    /// <summary>Filename template for FANBOX info/metadata.</summary>
    public string FilenameFanboxInfo { get; set; } = "FANBOX %artist% (%member_id%)\\%urlFilename% - %title%.txt";

    /// <summary>When true, downloads FANBOX cover even for restricted posts.</summary>
    public bool DownloadFanboxCoverWhenRestricted { get; set; } = true;

    /// <summary>When true, generates HTML for FANBOX article posts.</summary>
    public bool WriteFanboxHtml { get; set; } = false;

    #endregion

    #region Database auto-add

    /// <summary>When true, member is saved to database on download.</summary>
    public bool AutoAddMember { get; set; } = true;

    /// <summary>When true, image tags are saved to database on download.</summary>
    public bool AutoAddTags { get; set; } = true;

    /// <summary>When true, image caption is saved to database on download.</summary>
    public bool AutoAddCaption { get; set; } = false;

    #endregion

    /// <summary>User-Agent header sent on every Pixiv request.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>UI locale passed to Pixiv ajax endpoints.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>Display language for the Pikura UI: "English", "日本語", "中文", "한국어".</summary>
    public string AppLanguage { get; set; } = "English";

    /// <summary>App theme: "Default", "Light", "Dark".</summary>
    public string Theme { get; set; } = "Default";

    #region Gallery UI state

    /// <summary>"Grid" or "List".</summary>
    public string GalleryViewMode { get; set; } = "Grid";

    /// <summary>"Fixed" or "Natural".</summary>
    public string CardHeightMode { get; set; } = "Fixed";

    /// <summary>Card width in pixels (120-300).</summary>
    public int CardSize { get; set; } = 180;

    /// <summary>Sort mode index matching the ComboBox order.</summary>
    public int SortModeIndex { get; set; } = 0;

    /// <summary>Whether tag chips are visible on cards.</summary>
    public bool ShowTags { get; set; } = true;

    /// <summary>Whether the info strip (title + tags) is visible on cards.</summary>
    public bool ShowInfo { get; set; } = true;

    /// <summary>Whether the side preview panel is visible.</summary>
    public bool ShowPreview { get; set; } = false;

    /// <summary>Last width of the browse/preview side panel in pixels (0 = use default).</summary>
    public double BrowsePanelWidth { get; set; } = 380;

    #endregion

    #region Rankings UI state

    /// <summary>"Grid" or "List" for rankings view.</summary>
    public string RankingsViewMode { get; set; } = "Grid";

    /// <summary>"Fixed" or "Natural" height for rankings cards.</summary>
    public string RankingsCardHeightMode { get; set; } = "Fixed";

    /// <summary>Card width in pixels for rankings (120-300).</summary>
    public int RankingsCardSize { get; set; } = 180;

    /// <summary>Whether tag chips are visible on ranking cards.</summary>
    public bool RankingsShowTags { get; set; } = true;

    /// <summary>Whether the info strip is visible on ranking cards.</summary>
    public bool RankingsShowInfo { get; set; } = true;

    /// <summary>Whether the side preview panel is visible in rankings.</summary>
    public bool RankingsShowPreview { get; set; } = false;

    #endregion

    #region Startup Behavior

    /// <summary>When true, app starts automatically with Windows.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Startup window state: "Normal", "Maximized", "Minimized", "SystemTray".</summary>
    public string StartupWindowState { get; set; } = "Normal";

    /// <summary>When true, app minimizes to system tray instead of taskbar.</summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>When true, closing the window minimizes to tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = false;

    /// <summary>When true, app starts hidden in system tray (no window shown).</summary>
    public bool StartMinimizedToTray { get; set; } = false;

    /// <summary>When true, the app stays running in the background (tray) when closed so scheduled downloads can still execute.</summary>
    public bool KeepSchedulesRunningInBackground { get; set; } = false;

    /// <summary>When true, show a tray notification when a scheduled download completes.</summary>
    public bool ShowScheduleNotifications { get; set; } = true;

    /// <summary>When true, show a tray notification when any download job completes (individual, batch, or scheduled).</summary>
    public bool NotifyOnDownloadComplete { get; set; } = false;

    /// <summary>When true, show a tray notification when a download job starts.</summary>
    public bool NotifyOnDownloadStarted { get; set; } = false;

    /// <summary>When true, show a tray notification when a download job fails.</summary>
    public bool NotifyOnDownloadFailed { get; set; } = true;

    /// <summary>When true, show a tray notification when a download job is paused or resumed.</summary>
    public bool NotifyOnDownloadPaused { get; set; } = true;

    #endregion

    #region Discover UI state

    /// <summary>"Fixed" or "Natural" height for Discover cards.</summary>
    public string DiscoverCardHeightMode { get; set; } = "Fixed";

    /// <summary>Card width in pixels for Discover (120-300).</summary>
    public int DiscoverCardSize { get; set; } = 180;

    /// <summary>"Grid" or "List" for Discover view.</summary>
    public string DiscoverViewMode { get; set; } = "Grid";

    /// <summary>Whether tag chips are visible on Discover cards.</summary>
    public bool DiscoverShowTags { get; set; } = true;

    /// <summary>Whether the info strip is visible on Discover cards.</summary>
    public bool DiscoverShowInfo { get; set; } = true;

    /// <summary>Whether the side preview panel is visible in Discover.</summary>
    public bool DiscoverShowPreview { get; set; } = false;

    /// <summary>R-18 toggle state in Discover (persisted).</summary>
    public bool DiscoverShowR18 { get; set; } = true;

    /// <summary>When true, use pagination in Discover view.</summary>
    public bool DiscoverUsePagination { get; set; } = false;

    /// <summary>Items per page in Discover view.</summary>
    public int DiscoverItemsPerPage { get; set; } = 50;

    #endregion

    #region Bookmarks UI state

    /// <summary>"Fixed" or "Natural" height for Bookmarks cards.</summary>
    public string BookmarksCardHeightMode { get; set; } = "Fixed";

    /// <summary>Card width in pixels for Bookmarks (120-300).</summary>
    public int BookmarksCardSize { get; set; } = 180;

    /// <summary>"Grid" or "List" for Bookmarks view.</summary>
    public string BookmarksViewMode { get; set; } = "Grid";

    /// <summary>Whether tag chips are visible on Bookmarks cards.</summary>
    public bool BookmarksShowTags { get; set; } = true;

    /// <summary>Whether the info strip is visible on Bookmarks cards.</summary>
    public bool BookmarksShowInfo { get; set; } = true;

    /// <summary>R-18 toggle state in Bookmarks (persisted).</summary>
    public bool BookmarksShowR18 { get; set; } = false;

    #endregion

    #region Gallery Viewer Keyboard Navigation

    /// <summary>When true, arrow keys navigate between artworks in the gallery while the inline viewer is open.</summary>
    public bool GalleryKeyboardNavEnabled { get; set; } = true;

    /// <summary>When true, arrow keys navigate between artworks while in the fullscreen viewer window.</summary>
    public bool FullscreenKeyboardNavEnabled { get; set; } = true;

    #endregion

    #region Gallery/Rankings Pagination

    /// <summary>When true, use pagination in Gallery view.</summary>
    public bool GalleryUsePagination { get; set; } = false;

    /// <summary>Items per page in Gallery view.</summary>
    public int GalleryItemsPerPage { get; set; } = 50;

    /// <summary>When true, use pagination in Rankings view.</summary>
    public bool RankingsUsePagination { get; set; } = false;

    /// <summary>Items per page in Rankings view.</summary>
    public int RankingsItemsPerPage { get; set; } = 50;

    #endregion

    #region Accessibility

    /// <summary>Font size scaling factor (0.5 to 2.0, default 1.0).</summary>
    public double FontSizeScale { get; set; } = 1.0;

    /// <summary>When true, uses high contrast mode for better visibility.</summary>
    public bool UseHighContrast { get; set; } = false;

    /// <summary>When true, uses larger fonts throughout the application.</summary>
    public bool UseLargeFonts { get; set; } = false;

    /// <summary>When true, increases UI element spacing for better accessibility.</summary>
    public bool IncreaseSpacing { get; set; } = false;

    /// <summary>When true, reduces animations for motion sensitivity.</summary>
    public bool ReduceMotion { get; set; } = false;

    #endregion

    #region Hoshi AI Model Settings

    /// <summary>Custom text model name for Hoshi AI (empty = use default).</summary>
    public string HoshiTextModel { get; set; } = string.Empty;
    
    /// <summary>Custom vision model name for Hoshi AI (empty = use default).</summary>
    public string HoshiVisionModel { get; set; } = string.Empty;
    
    /// <summary>When true, use custom models if specified; otherwise use defaults.</summary>
    public bool UseCustomHoshiModels { get; set; } = false;
    
    /// <summary>When true, Hoshi AI is enabled by default on startup.</summary>
    public bool HoshiEnabled { get; set; } = false;

    /// <summary>When true, use the local ONNX anime tagger for image tagging.</summary>
    public bool HoshiUseAnimeTagger { get; set; } = false;

    /// <summary>Key of the active ONNX anime tagger model (matches KnownAnimeTaggerModels).</summary>
    public string HoshiAnimeTaggerModel { get; set; } = "wd-swinv2-tagger-v3";

    /// <summary>Confidence threshold for anime tagger predictions.</summary>
    public double HoshiAnimeTaggerThreshold { get; set; } = 0.35;

    /// <summary>Maximum number of tags returned by the anime tagger.</summary>
    public int HoshiAnimeTaggerMaxTags { get; set; } = 50;

    /// <summary>When true, automatically tag downloaded images with the anime tagger.</summary>
    public bool HoshiAnimeTaggerAutoTagDownloads { get; set; } = false;

    #endregion

    #region Diagnostics

    /// <summary>When true, verbose (Debug-level) logging is written to the log file.</summary>
    public bool VerboseLogging { get; set; } = false;

    #endregion

    #region Updates

    /// <summary>When to check for updates: "Startup", "Daily", "Weekly", "Never".</summary>
    public string UpdateCheckFrequency { get; set; } = "Startup";

    /// <summary>When true, automatically download the update in the background.</summary>
    public bool AutoDownloadUpdates { get; set; } = false;

    /// <summary>When true, show a banner/notification when an update is available.</summary>
    public bool NotifyOnUpdate { get; set; } = true;

    /// <summary>Release channel: "Stable" or "PreRelease".</summary>
    public string UpdateChannel { get; set; } = "Stable";

    /// <summary>UTC timestamp of the last update check.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>The version that was running last time the app launched — used to show changelog on first run after update.</summary>
    public string? LastSeenVersion { get; set; }

    #endregion

    #region Image Processing

    /// <summary>Active resize preset for post-download processing (None = no processing).</summary>
    public DevicePreset ActiveResizePreset { get; set; } = DevicePreset.None;

    /// <summary>Resize mode: Fit, Fill, or Stretch.</summary>
    public ResizeMode ResizeMode { get; set; } = ResizeMode.Fit;

    /// <summary>Output format for resized images.</summary>
    public ResizeOutputFormat ResizeOutputFormat { get; set; } = ResizeOutputFormat.KeepOriginal;

    /// <summary>JPEG quality for resized images (1-100).</summary>
    public int ResizeJpegQuality { get; set; } = 90;

    /// <summary>Custom resize width in pixels (when using Custom preset).</summary>
    public int ResizeCustomWidth { get; set; } = 1920;

    /// <summary>Custom resize height in pixels (when using Custom preset).</summary>
    public int ResizeCustomHeight { get; set; } = 1080;

    /// <summary>When true, maintain aspect ratio during custom resize.</summary>
    public bool ResizeMaintainAspect { get; set; } = true;

    /// <summary>When true, enable image processing on all downloads.</summary>
    public bool EnableImageProcessing { get; set; } = false;

    /// <summary>Custom output folder for processed images (empty = same as original).</summary>
    public string? ImageProcessingOutputFolder { get; set; }

    /// <summary>Default image resize/edit preset for post-download processing (null = no processing).</summary>
    public ImageEditPreset? ImagePreset { get; set; }

    #endregion

    #region Window geometry

    /// <summary>Last saved window width (0 = use default).</summary>
    public double WindowWidth { get; set; } = 0;

    /// <summary>Last saved window height (0 = use default).</summary>
    public double WindowHeight { get; set; } = 0;

    /// <summary>Last saved window X position (-1 = use default/center).</summary>
    public double WindowX { get; set; } = -1;

    /// <summary>Last saved window Y position (-1 = use default/center).</summary>
    public double WindowY { get; set; } = -1;

    /// <summary>Last saved window state (0=Normal, 1=Minimized, 2=Maximized).</summary>
    public int WindowState { get; set; } = 0;

    #endregion

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(PhpSessId);
}
