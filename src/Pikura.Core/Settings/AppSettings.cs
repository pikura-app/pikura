using System;
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

    /// <summary>
    /// Cloudflare's <c>cf_clearance</c> cookie. Unlike <see cref="PhpSessId"/> this isn't
    /// obtained during normal login — some Pixiv subdomains (confirmed: <c>embed.pixiv.net</c>,
    /// used for Collection collage thumbnails) enforce Cloudflare bot-management that rejects
    /// plain HttpClient requests without it, even with a valid PHPSESSID. Obtained by solving
    /// the Cloudflare challenge in a headless browser — see
    /// <c>Pikura.Avalonia.Services.CloudflareSessionService</c>.
    /// </summary>
    public string CfClearance { get; set; } = string.Empty;

    /// <summary>When <see cref="CfClearance"/> was last (re)obtained — used to decide when it's
    /// stale enough to refresh (Cloudflare clearance tokens are typically valid for hours, not
    /// indefinitely).</summary>
    public DateTime? CfClearanceObtainedAt { get; set; }

    /// <summary>Pixiv user id of the logged-in account (resolved after a successful session check).</summary>
    public string? UserId { get; set; }

    /// <summary>Display name resolved from the logged-in account, for UI only.</summary>
    public string? UserName { get; set; }

    /// <summary>Whether the logged-in account has a Pixiv Premium subscription (resolved after a successful session check).</summary>
    public bool IsPremium { get; set; }

    /// <summary>Artwork IDs the user has liked through Pikura, persisted so the UI remembers across restarts.</summary>
    [JsonPropertyName("pixivLikedArtworkIds")]
    public List<string> PixivLikedArtworkIds { get; set; } = new();

    /// <summary>Past searches from the Search tab (query + the filters active at the time),
    /// newest first — mirrors Pixiv's own search-history dropdown.</summary>
    public List<SearchHistoryEntry> SearchHistory { get; set; } = new();

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

    /// <summary>
    /// When true, opening artworks anywhere in the app is not recorded to the local
    /// "Viewed" history and does not affect recent-search suggestions — like a browser's
    /// incognito mode. Existing history entries are left untouched; this only suppresses
    /// new writes while enabled.
    /// </summary>
    public bool IncognitoModeEnabled { get; set; } = false;

    /// <summary>Top-level layout for the Viewed/History tab: "Default", "Grouped", or "List". Persists across restarts.</summary>
    public string HistoryViewMode { get; set; } = "Default";

    /// <summary>When true, viewed history entries older than the retention window are deleted at app start/restart.</summary>
    public bool AutoClearViewedHistoryEnabled { get; set; } = false;

    /// <summary>When true, viewed history entries older than the retention window are deleted periodically while the app is running.</summary>
    public bool AutoClearViewedHistoryWhileRunning { get; set; } = false;

    /// <summary>Retention amount for auto-clearing viewed history (interpreted with <see cref="AutoClearViewedHistoryUnit"/>).</summary>
    public int AutoClearViewedHistoryValue { get; set; } = 1;

    /// <summary>Retention unit for auto-clearing viewed history: Hours, Days, Weeks, Months, or Years.</summary>
    public string AutoClearViewedHistoryUnit { get; set; } = "Months";

    /// <summary>
    /// Computes the UTC cutoff for viewed-history retention; entries viewed before this
    /// instant should be deleted. Returns null when the window is misconfigured. Callers
    /// are responsible for checking the on-start / while-running enable flags.
    /// </summary>
    public DateTime? GetViewedHistoryRetentionCutoffUtc(DateTime nowUtc)
    {
        if (AutoClearViewedHistoryValue <= 0) return null;
        return AutoClearViewedHistoryUnit switch
        {
            "Hours"  => nowUtc.AddHours(-AutoClearViewedHistoryValue),
            "Days"   => nowUtc.AddDays(-AutoClearViewedHistoryValue),
            "Weeks"  => nowUtc.AddDays(-7 * AutoClearViewedHistoryValue),
            "Months" => nowUtc.AddMonths(-AutoClearViewedHistoryValue),
            "Years"  => nowUtc.AddYears(-AutoClearViewedHistoryValue),
            _        => null,
        };
    }

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

    #region Blocklist

    /// <summary>Unified blocklist entries. Replaces legacy BlacklistTags, BlacklistTitles, BlacklistMembers and ExcludedTags.</summary>
    public List<BlocklistEntry> BlocklistEntries { get; set; } = new();

    // Legacy properties are retained only for one-time migration into <see cref="BlocklistEntries"/>.
    public List<string> BlacklistTags { get; set; } = new();
    public bool UseBlacklistTagsRegex { get; set; } = false;
    public List<string> BlacklistTitles { get; set; } = new();
    public bool UseBlacklistTitlesRegex { get; set; } = false;
    public List<string> BlacklistMembers { get; set; } = new();
    public bool BlockDownloadsFromBlocklistedMembers { get; set; } = true;
    public bool BlockDownloadsWithBlocklistedTags { get; set; } = true;
    public bool HideBlocklistedArtistsInGallery { get; set; } = false;

    /// <summary>
    /// One-time migration from legacy blacklists into <see cref="BlocklistEntries"/>.
    /// Safe to call repeatedly: it only runs when <see cref="BlocklistEntries"/> is empty.
    /// </summary>
    public void MigrateLegacyBlocklists()
    {
        if (BlocklistEntries.Count > 0) return;

        foreach (var tag in ExcludedTags)
            if (!string.IsNullOrWhiteSpace(tag))
                BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Tag, Value = tag, Scope = BlocklistScope.AllTabs, BlockDownload = false });

        foreach (var tag in BlacklistTags)
            if (!string.IsNullOrWhiteSpace(tag))
                BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Tag, Value = tag, Scope = BlocklistScope.AllTabs, UseRegex = UseBlacklistTagsRegex, BlockDownload = BlockDownloadsWithBlocklistedTags });

        foreach (var title in BlacklistTitles)
            if (!string.IsNullOrWhiteSpace(title))
                BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Title, Value = title, Scope = BlocklistScope.AllTabs, UseRegex = UseBlacklistTitlesRegex, BlockDownload = true });

        foreach (var member in BlacklistMembers)
            if (!string.IsNullOrWhiteSpace(member))
                BlocklistEntries.Add(new BlocklistEntry { Type = BlocklistType.Artist, Value = member, Scope = BlocklistScope.AllTabs, BlockDownload = BlockDownloadsFromBlocklistedMembers });

        // Clear legacy lists so they are not re-migrated and no longer clutter the saved JSON.
        ExcludedTags.Clear();
        BlacklistTags.Clear();
        BlacklistTitles.Clear();
        BlacklistMembers.Clear();
    }

    /// <summary>Returns true if the given artist matches any artist blocklist entry.</summary>
    public bool IsArtistBlocklisted(string? userId, string? userName)
    {
        foreach (var entry in BlocklistEntries)
        {
            if (entry.Type != BlocklistType.Artist) continue;
            if (IsArtistMatch(entry, userId, userName)) return true;
        }
        return false;
    }

    /// <summary>Legacy alias for <see cref="IsArtistBlocklisted"/>.</summary>
    public bool IsMemberBlocklisted(string? userId, string? userName) => IsArtistBlocklisted(userId, userName);

    /// <summary>Returns true if the artwork should be hidden from the given source tab.</summary>
    public bool IsArtworkHidden(string source, string? userId, string? userName, string? title, IReadOnlyList<string>? tags)
    {
        foreach (var entry in BlocklistEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;
            if (!ScopeMatches(entry.Scope, source)) continue;

            bool matches = entry.Type switch
            {
                BlocklistType.Tag => IsTagMatch(entry, tags),
                BlocklistType.Artist => IsArtistMatch(entry, userId, userName),
                BlocklistType.Title => IsTitleMatch(entry, title),
                _ => false,
            };

            if (matches) return true;
        }
        return false;
    }

    /// <summary>Returns true if the artwork should be blocked from download.</summary>
    public bool IsArtworkBlockedFromDownload(string? userId, string? userName, string? title, IReadOnlyList<string>? tags)
    {
        foreach (var entry in BlocklistEntries)
        {
            if (!entry.BlockDownload) continue;
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;

            bool matches = entry.Type switch
            {
                BlocklistType.Tag => IsTagMatch(entry, tags),
                BlocklistType.Artist => IsArtistMatch(entry, userId, userName),
                BlocklistType.Title => IsTitleMatch(entry, title),
                _ => false,
            };

            if (matches) return true;
        }
        return false;
    }

    private static bool ScopeMatches(BlocklistScope scope, string source)
    {
        if (scope == BlocklistScope.AllTabs) return true;
        var sourceLower = source.ToLowerInvariant();
        return scope.ToString().ToLowerInvariant() == sourceLower;
    }

    private static bool IsTagMatch(BlocklistEntry entry, IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return false;
        if (entry.UseRegex)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(entry.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return tags.Any(t => regex.IsMatch(t));
            }
            catch { return false; }
        }
        return tags.Any(t => t.Contains(entry.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsArtistMatch(BlocklistEntry entry, string? userId, string? userName)
    {
        if (!string.IsNullOrEmpty(userId) && string.Equals(entry.Value, userId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(userName) && string.Equals(entry.Value, userName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsTitleMatch(BlocklistEntry entry, string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        if (entry.UseRegex)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(entry.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return regex.IsMatch(title);
            }
            catch { return false; }
        }
        return title.Contains(entry.Value, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>App theme: "Light", "Dark", "System", "Scheduled".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>When <see cref="Theme"/> is "Scheduled", the local time of day to switch to dark mode.</summary>
    public TimeSpan ThemeScheduleDarkStart { get; set; } = TimeSpan.FromHours(20);

    /// <summary>When <see cref="Theme"/> is "Scheduled", the local time of day to switch back to light mode.</summary>
    public TimeSpan ThemeScheduleDarkEnd { get; set; } = TimeSpan.FromHours(6);

    #region Background Artwork Overlay

    /// <summary>When true, the selected artwork images are shown as a full-window overlay.</summary>
    public bool BackgroundOverlayEnabled { get; set; } = false;

    /// <summary>Up to 5 image URLs or local file paths used for the background overlay.</summary>
    public List<string> BackgroundOverlayImagePaths { get; set; } = new();

    /// <summary>Per-image overlay settings (opacity, brightness, darkness, pan). Indices match <see cref="BackgroundOverlayImagePaths"/>.</summary>
    public List<OverlayImageEntry> BackgroundOverlayImageEntries { get; set; } = new();

    /// <summary>Opacity of the overlay image (0 = invisible, 1 = fully opaque).</summary>
    public double BackgroundOverlayImageOpacity { get; set; } = 0.25;

    /// <summary>White overlay opacity used to brighten the image (0 - 1).</summary>
    public double BackgroundOverlayBrightness { get; set; } = 0.0;

    /// <summary>Black overlay opacity used to darken the image (0 - 1).</summary>
    public double BackgroundOverlayDarkness { get; set; } = 0.0;

    /// <summary>When true, the global opacity/lighten/darken values override per-image settings.</summary>
    public bool BackgroundOverlayUseGlobalOverrides { get; set; } = false;

    /// <summary>How long to wait before cycling to the next overlay image.</summary>
    public int BackgroundOverlayCycleInterval { get; set; } = 30;

    /// <summary>Overlay cycling mode index: 0=Sequential seconds, 1=Sequential minutes, 2=Random seconds, 3=Random minutes.</summary>
    public int BackgroundOverlayCycleMode { get; set; } = 0;

    #endregion

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

    /// <summary>Whether the Liked/Bookmarked/Local-favorite corner badges are visible on cards
    /// (Gallery, Discover, and Bookmarks all share this one setting).</summary>
    public bool ShowBadges { get; set; } = true;

    /// <summary>Whether the artwork viewer's like/bookmark/view stat counts are visible.</summary>
    public bool ShowPixivStats { get; set; } = true;

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

    /// <summary>Which tab/view opens automatically on startup (matches the nav button labels).</summary>
    public string StartupTab { get; set; } = "Gallery";

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

    /// <summary>R-18 toggle state in Collections browse (persisted) — Collections summaries
    /// carry Pixiv's own <c>xRestrict</c> flag, so unlike artworks this can filter without an
    /// extra request per tile.</summary>
    public bool CollectionsShowR18 { get; set; } = false;

    /// <summary>"Grid" or "List" for a Collection's own artwork grid (mirrors Bookmarks).</summary>
    public string CollectionsViewMode { get; set; } = "Grid";

    /// <summary>"Fixed" or "Natural" height for a Collection's own artwork cards.</summary>
    public string CollectionsCardHeightMode { get; set; } = "Fixed";

    /// <summary>Whether tag chips are visible on a Collection's artwork cards.</summary>
    public bool CollectionsShowTags { get; set; } = true;

    /// <summary>Whether the info strip is visible on a Collection's artwork cards.</summary>
    public bool CollectionsShowInfo { get; set; } = true;

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

    /// <summary>When true, Pixivision browses page-by-page (Prev/Next/jump-to-page) instead of
    /// autoloading while scrolling. Not persisted before — always silently reset to autoload on
    /// every restart regardless of what the user had picked.</summary>
    public bool PixivisionUsePagination { get; set; } = true;

    /// <summary>When true, History browses page-by-page instead of autoloading while scrolling
    /// (Default/flat mode only — Grouped/List mode always shows one date at a time).</summary>
    public bool HistoryUsePagination { get; set; } = false;

    /// <summary>Items per page in History's Default (flat) view when HistoryUsePagination is on.</summary>
    public int HistoryItemsPerPage { get; set; } = 50;

    /// <summary>When true, use pagination in the Search view instead of autoload-on-scroll.</summary>
    public bool SearchUsePagination { get; set; } = false;

    /// <summary>Items per page in Search view.</summary>
    public int SearchItemsPerPage { get; set; } = 60;

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

    #region UI

    /// <summary>When true, show the introductory splash screen on startup.</summary>
    public bool ShowSplashScreen { get; set; } = true;

    /// <summary>When true, show the feature highlights / onboarding popup for new installs or major updates.</summary>
    public bool ShowFeatureHighlights { get; set; } = true;

    /// <summary>The app version that last displayed the feature highlights. Empty for a fresh install.</summary>
    public string? LastOnboardingVersionShown { get; set; }

    #endregion

    #region Viewer tab persistence

    /// <summary>Open inline-viewer tabs (artwork + collage tabs), saved on close and restored on next launch.</summary>
    public List<PersistedViewerTab> PersistedViewerTabs { get; set; } = [];

    /// <summary>Index into <see cref="PersistedViewerTabs"/> of the tab that was selected when the app closed.</summary>
    public int PersistedSelectedTabIndex { get; set; } = -1;

    #endregion

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(PhpSessId);
}

/// <summary>
/// A single inline-viewer tab's persisted state — enough to rebuild the tab (re-fetching the
/// artwork card(s) from Pixiv) the next time the app launches.
/// </summary>
public sealed class PersistedViewerTab
{
    /// <summary>The section that originally opened this tab (informational only).</summary>
    public string Source { get; set; } = "Gallery";

    /// <summary>Tab header text shown in the tab strip.</summary>
    public string? Header { get; set; }

    /// <summary>True if this is the collage tab.</summary>
    public bool IsCollage { get; set; }

    /// <summary>Artwork ID shown by a regular (non-collage) tab.</summary>
    public string? ArtworkId { get; set; }

    /// <summary>Artwork IDs contained in the collage tab, in order.</summary>
    public List<string> CollageArtworkIds { get; set; } = [];
}

/// <summary>
/// Per-image overlay settings persisted alongside <see cref="AppSettings.BackgroundOverlayImagePaths"/>.
/// </summary>
public sealed class OverlayImageEntry
{
    /// <summary>Image URL or local file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Artwork title (for display in settings).</summary>
    public string? Title { get; set; }

    /// <summary>Artist display name.</summary>
    public string? UserName { get; set; }

    /// <summary>Artist Pixiv user ID.</summary>
    public string? UserId { get; set; }

    /// <summary>Artwork (illust) ID on Pixiv.</summary>
    public string? IllustId { get; set; }

    /// <summary>Image opacity (0 = invisible, 1 = fully opaque).</summary>
    public double Opacity { get; set; } = 0.25;

    /// <summary>White overlay brightness (0 - 1).</summary>
    public double Brightness { get; set; } = 0.0;

    /// <summary>Black overlay darkness (0 - 1).</summary>
    public double Darkness { get; set; } = 0.0;

    /// <summary>Horizontal pan offset as a fraction (-1 to 1). 0 = centered.</summary>
    public double PanX { get; set; } = 0.0;

    /// <summary>Vertical pan offset as a fraction (-1 to 1). 0 = centered.</summary>
    public double PanY { get; set; } = 0.0;

    /// <summary>Zoom scale (1.0 = 100%, 0.5 = 50%, 2.0 = 200%).</summary>
    public double Zoom { get; set; } = 1.0;
}

/// <summary>One entry in the Search tab's search history — the query plus enough of the active
/// filter state to both display a summary (e.g. "Illustrations · Hide AI-generated work") and
/// exactly re-run the search later, mirroring Pixiv's own search-history dropdown.</summary>
public sealed class SearchHistoryEntry
{
    public string Query { get; set; } = string.Empty;
    public string Category { get; set; } = "illustrations"; // "illustrations" | "manga" | "novels" | "users"
    public string SortOrder { get; set; } = "date_d";
    public string SearchMode { get; set; } = "safe";
    public string IncludeAnyKeywords { get; set; } = string.Empty;
    public string ExcludeKeywords { get; set; } = string.Empty;
    /// <summary>Precomputed short summary shown under the query in the dropdown, e.g.
    /// "Illustrations and Manga" or "Novels · Group by series".</summary>
    public string FilterSummary { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; } = DateTime.Now;
}
