using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Dialogs;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Observable wrapper around <see cref="OverlayImageEntry"/> for display in the Settings UI.
/// Holds a thumbnail bitmap and exposes the per-image path for identification.
/// </summary>
public sealed partial class OverlayImageItem : ObservableObject
{
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private Bitmap? _thumbnail;
    public OverlayImageEntry Entry { get; }

    /// <summary>Display-friendly title, e.g. "ArtworkTitle by ArtistName (12345)".</summary>
    public string DisplayTitle
    {
        get
        {
            var title = !string.IsNullOrWhiteSpace(Entry.Title)
                ? Entry.Title
                : System.IO.Path.GetFileNameWithoutExtension(Path);
            if (!string.IsNullOrWhiteSpace(Entry.UserName))
            {
                var artist = !string.IsNullOrWhiteSpace(Entry.UserId)
                    ? $"{Entry.UserName} ({Entry.UserId})"
                    : Entry.UserName;
                return $"{title} by {artist}";
            }
            return title;
        }
    }

    public OverlayImageItem(string path, OverlayImageEntry entry)
    {
        _path = path;
        Entry = entry;
    }
}

/// <summary>
/// Overlay cycling mode. The integer values are persisted in <see cref="AppSettings.BackgroundOverlayCycleMode"/>.
/// </summary>
public enum BackgroundOverlayCycleMode
{
    /// <summary>Cycle to the next image every N seconds.</summary>
    SequentialSeconds = 0,
    /// <summary>Cycle to the next image every N minutes.</summary>
    SequentialMinutes = 1,
    /// <summary>Pick a random image every N seconds.</summary>
    RandomSeconds = 2,
    /// <summary>Pick a random image every N minutes.</summary>
    RandomMinutes = 3,
}

/// <summary>
/// Manages the artwork background overlay. Loads images, applies transparency / brightness / darkness,
/// and cycles through up to <see cref="MaxImageItems"/> selected images on a timer.
/// </summary>
public sealed partial class BackgroundOverlayService : ObservableObject
{
    /// <summary>Maximum number of background overlay images the user can have in rotation.</summary>
    public const int MaxImageItems = 10;

    /// <summary>Binding-friendly wrapper for the constant limit.</summary>
    public int MaxImageItemsValue => MaxImageItems;

    private readonly SettingsService _settingsService;
    private readonly PixivImageLoader _imageLoader;
    private readonly PixivClient _pixivClient;
    private readonly ILogger<BackgroundOverlayService> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private CancellationTokenSource? _loadCts;
    private bool _isReloading;
    private bool _isPersisting;
    private bool _isShowingPreview;
    private int _currentIndex;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private double _imageOpacity = 0.25;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _darkness;
    [ObservableProperty] private bool _useGlobalOverrides;
    [ObservableProperty] private int _cycleInterval = 30;
    [ObservableProperty] private int _selectedCycleModeIndex;
    [ObservableProperty] private Bitmap? _currentImage;

    /// <summary>Image URLs or local file paths used for the overlay (max <see cref="MaxImageItems"/>).</summary>
    public ObservableCollection<string> ImagePaths { get; } = new();

    /// <summary>Observable items for the Settings UI — each carries a thumbnail + per-image entry.</summary>
    public ObservableCollection<OverlayImageItem> ImageItems { get; } = new();

    /// <summary>Horizontal alignment of the current overlay image (-1 to 1).</summary>
    [ObservableProperty] private double _panX;
    /// <summary>Vertical alignment of the current overlay image (-1 to 1).</summary>
    [ObservableProperty] private double _panY;
    /// <summary>Zoom level of the current overlay image.</summary>
    [ObservableProperty] private double _zoom = 1.0;

    /// <summary>Friendly labels for the cycle mode dropdown.</summary>
    public string[] CycleModeOptions { get; } = new[]
    {
        "In order - seconds",
        "In order - minutes",
        "Random - seconds",
        "Random - minutes",
    };

    public BackgroundOverlayCycleMode CycleMode => (BackgroundOverlayCycleMode)SelectedCycleModeIndex;

    public BackgroundOverlayService(SettingsService settingsService, PixivImageLoader imageLoader, PixivClient pixivClient, ILogger<BackgroundOverlayService> logger)
    {
        _settingsService = settingsService;
        _imageLoader = imageLoader;
        _pixivClient = pixivClient;
        _logger = logger;
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => Advance();
        _settingsService.Changed += (_, _) =>
        {
            // Skip reload when we ourselves just wrote to settings — avoids
            // a Persist -> Changed -> ReloadFromSettings -> flicker loop.
            if (_isPersisting) return;
            // Changed fires on every settings write; reloading re-resolves every overlay image
            // (possibly over the network), so only reload when overlay settings changed.
            if (OverlaySignature() == _overlaySignature) return;
            _ = ReloadFromSettingsAsync();
        };
        _ = ReloadFromSettingsAsync();
    }

    private string? _overlaySignature;

    private string OverlaySignature()
    {
        var s = _settingsService.Current;
        return string.Join("|", s.BackgroundOverlayEnabled, s.BackgroundOverlayImageOpacity, s.BackgroundOverlayBrightness,
            s.BackgroundOverlayDarkness, s.BackgroundOverlayUseGlobalOverrides, s.BackgroundOverlayCycleInterval,
            s.BackgroundOverlayCycleMode, string.Join(";", s.BackgroundOverlayImagePaths ?? []),
            string.Join(";", (s.BackgroundOverlayImageEntries ?? []).Select(e => e.Path)));
    }

    partial void OnIsEnabledChanged(bool value) { if (!_isReloading) Persist(); }
    partial void OnImageOpacityChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnBrightnessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnDarknessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnUseGlobalOverridesChanged(bool value) { if (!_isReloading) { Persist(); ApplyPerImageSettings(); } }
    partial void OnCycleIntervalChanged(int value) { if (!_isReloading) Persist(); }
    partial void OnSelectedCycleModeIndexChanged(int value) { if (!_isReloading) Persist(); }

    /// <summary>
    /// Adds an image to the overlay list with its per-image entry. If the list exceeds <see cref="MaxImageItems"/>, the oldest is removed.
    /// This is the synchronous, fire-and-forget entry point for callers that do not want to await.
    /// </summary>
    public void AddImage(string? pathOrUrl, OverlayImageEntry? entry = null)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return;
        _ = AddImageAsync(pathOrUrl, entry);
    }

    /// <summary>
    /// Adds an image to the overlay list after showing the preview/adjustment dialog.
    /// If the user cancels, nothing is added.
    /// </summary>
    public async Task AddImageWithPreviewAsync(string? pathOrUrl, Window? owner, OverlayImageEntry? entry = null)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return;

        if (owner is null)
        {
            AddImage(pathOrUrl, entry);
            return;
        }

        try
        {
            var resolved = await ResolveToOriginalAsync(pathOrUrl.Trim(), entry?.IllustId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved)) return;

            var bytes = await FetchImageBytesAsync(resolved, entry?.IllustId).ConfigureAwait(false);

            var seedEntry = entry ?? new OverlayImageEntry { Path = resolved };
            seedEntry.Path = resolved;

            OverlayImageEntry? result = null;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var preview = new BackgroundPreviewWindow(resolved, bytes, seedEntry);
                await preview.ShowDialog(owner);
                result = preview.Result;
            }).ConfigureAwait(false);

            if (result is null) return;

            result.Path = resolved;
            await Dispatcher.UIThread.InvokeAsync(() => AddImageCore(resolved, result));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to add overlay image with preview from {Path}", pathOrUrl);
        }
    }

    /// <summary>Outcome of a bulk add, so the UI can report what actually happened.</summary>
    /// <param name="Added">Entries that resolved and were added.</param>
    /// <param name="Failed">Entries that couldn't be resolved to an image.</param>
    /// <param name="Skipped">Entries dropped because the list is already at <see cref="MaxImageItems"/>.</param>
    public readonly record struct OverlayAddResult(int Added, int Failed, int Skipped);

    /// <summary>
    /// Adds any number of overlay images from one blob of text. Entries may be artwork IDs,
    /// pixiv.net links, direct image URLs or local file paths, freely mixed, separated by new
    /// lines, commas or semicolons. A <c>.txt</c>/<c>.csv</c>/<c>.list</c> path is read as a list
    /// of further entries rather than treated as an image.
    /// </summary>
    public async Task<OverlayAddResult> AddManyAsync(string? raw, Window? owner)
    {
        var entries = ParseEntries(raw);
        if (entries.Count == 0) return default;

        // A single entry keeps the familiar preview/adjust dialog. For a batch, putting ten modal
        // dialogs in the user's way would be worse than useless, so those are added directly.
        if (entries.Count == 1)
        {
            var before = ImagePaths.Count;
            await AddImageWithPreviewAsync(entries[0], owner).ConfigureAwait(false);
            return ImagePaths.Count > before
                ? new OverlayAddResult(1, 0, 0)
                : new OverlayAddResult(0, 1, 0);
        }

        int added = 0, failed = 0, skipped = 0;
        foreach (var token in entries)
        {
            // Stop at the cap rather than letting AddImageCore evict the images already in
            // rotation — silently rotating out what the user just added would be baffling.
            if (ImagePaths.Count >= MaxImageItems) { skipped++; continue; }

            try
            {
                if (await TryAddResolvedAsync(token).ConfigureAwait(false)) added++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogDebug(ex, "Bulk overlay add failed for entry {Entry}", token);
            }
        }

        return new OverlayAddResult(added, failed, skipped);
    }

    private async Task<bool> TryAddResolvedAsync(string token)
    {
        var resolved = await ResolveToOriginalAsync(token.Trim(), null).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolved)) return false;

        var newEntry = new OverlayImageEntry { Path = resolved };
        await Dispatcher.UIThread.InvokeAsync(() => AddImageCore(resolved, newEntry));
        return true;
    }

    /// <summary>
    /// Splits a blob of user input into individual overlay entries, expanding any list files and
    /// removing duplicates while preserving order.
    /// </summary>
    public static IReadOnlyList<string> ParseEntries(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var token in SplitTokens(raw))
        {
            if (IsListFile(token))
            {
                try
                {
                    // One level deep only — a list referencing further lists isn't worth the
                    // cycle-detection it would need.
                    foreach (var line in File.ReadAllLines(token))
                        foreach (var inner in SplitTokens(line))
                            if (!IsListFile(inner) && seen.Add(inner))
                                result.Add(inner);
                    continue;
                }
                catch
                {
                    // Unreadable — fall through and let it be treated as an ordinary entry.
                }
            }

            if (seen.Add(token)) result.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Separators: new lines, commas and semicolons. Commas are accepted because that's how people
    /// naturally paste a list; the trade-off is that a URL or filename containing a literal comma
    /// would be split, which is rare enough to be worth it.
    /// <para>
    /// Blank lines and <c>#</c>/<c>//</c> comments are ignored, since hand-written list files
    /// routinely contain both and reporting them as failed entries would be noise.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SplitTokens(string raw) =>
        raw.Split(['\n', '\r', ',', ';'],
                  StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(t => t.Length > 0
                       && !t.StartsWith('#')
                       && !t.StartsWith("//", StringComparison.Ordinal));

    private static bool IsListFile(string token)
    {
        if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(token);
        if (ext is not (".txt" or ".csv" or ".list")) return false;
        try { return File.Exists(token); }
        catch { return false; }
    }

    private async Task AddImageAsync(string? pathOrUrl, OverlayImageEntry? entry)
    {
        try
        {
            var resolved = await ResolveToOriginalAsync(pathOrUrl?.Trim(), entry?.IllustId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved)) return;

            var newEntry = entry ?? new OverlayImageEntry { Path = resolved };
            newEntry.Path = resolved;

            if (Dispatcher.UIThread.CheckAccess())
                AddImageCore(resolved, newEntry);
            else
                await Dispatcher.UIThread.InvokeAsync(() => AddImageCore(resolved, newEntry));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to add overlay image from {Path}", pathOrUrl);
        }
    }

    private void AddImageCore(string url, OverlayImageEntry entry)
    {
        var existing = ImagePaths.FirstOrDefault(p => p.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var idx = ImagePaths.IndexOf(existing);
            ImagePaths.RemoveAt(idx);
            if (idx < ImageItems.Count) ImageItems.RemoveAt(idx);
        }

        ImagePaths.Add(url);
        var item = new OverlayImageItem(url, entry);
        ImageItems.Add(item);
        _ = LoadThumbnailAsync(item);

        if (ImagePaths.Count > MaxImageItems)
        {
            ImagePaths.RemoveAt(0);
            if (ImageItems.Count > MaxImageItems) ImageItems.RemoveAt(0);
        }

        _currentIndex = Math.Max(0, ImagePaths.Count - 1);
        if (!_isReloading)
            Persist();
    }

    /// <summary>Removes the image at the specified index.</summary>
    public void RemoveImage(int index)
    {
        if (index < 0 || index >= ImagePaths.Count) return;
        ImagePaths.RemoveAt(index);
        if (index < ImageItems.Count)
        {
            var old = ImageItems[index].Thumbnail;
            ImageItems.RemoveAt(index);
            old?.Dispose();
        }
        if (_currentIndex >= ImagePaths.Count)
            _currentIndex = Math.Max(0, ImagePaths.Count - 1);
        Persist();
    }

    /// <summary>Clears all overlay images.</summary>
    public void ClearImages()
    {
        ImagePaths.Clear();
        foreach (var item in ImageItems) item.Thumbnail?.Dispose();
        ImageItems.Clear();
        _currentIndex = 0;
        CurrentImage = null;
        Persist();
    }

    /// <summary>Persists only the per-image entries (called after editing a single image's settings).</summary>
    public void PersistEntries()
    {
        if (_isReloading) return;
        _isPersisting = true;
        try
        {
            _settingsService.Update(s =>
            {
                s.BackgroundOverlayImageEntries = ImageItems.Select(i => i.Entry).ToList();
            });
        }
        finally { _isPersisting = false; }
        _ = LoadCurrentImageAsync();
    }

    /// <summary>
    /// The single path all "Use as Background" view call sites should use.
    /// Resolves the card's thumbnail to the true Pixiv Original URL, opens the preview
    /// window immediately, and lets the preview load the full-resolution bytes in the
    /// background. Adds the image to the overlay if the user clicks Apply.
    /// </summary>
    public async Task UseAsBackgroundFromCardAsync(ArtworkCardViewModel card, string? preferredUrl = null, Window? window = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (string.IsNullOrWhiteSpace(card.ThumbnailUrl))
            return;

        if (_isShowingPreview)
            return;

        _isShowingPreview = true;
        try
        {
            string? url = !string.IsNullOrWhiteSpace(preferredUrl)
                ? preferredUrl
                : await ResolveToOriginalAsync(card.ThumbnailUrl, card.Id, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(url))
                url = card.ThumbnailUrl;

            var seedEntry = new OverlayImageEntry
            {
                Path = url,
                Title = card.Title,
                UserName = card.UserName,
                UserId = card.UserId,
                IllustId = card.Id,
            };

            OverlayImageEntry? result = null;
            if (window is null)
            {
                result = seedEntry;
            }
            else if (Dispatcher.UIThread.CheckAccess())
            {
                var preview = new BackgroundPreviewWindow(url, null, seedEntry);
                await preview.ShowDialog(window);
                result = preview.Result;
            }
            else
            {
                BackgroundPreviewWindow? preview = null;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    preview = new BackgroundPreviewWindow(url, null, seedEntry);
                    await preview.ShowDialog(window);
                }).ConfigureAwait(false);
                result = preview?.Result;
            }

            if (result is null) return;

            result.Title = card.Title;
            result.UserName = card.UserName;
            result.UserId = card.UserId;
            result.IllustId = card.Id;

            AddImage(url, result);
        }
        finally
        {
            _isShowingPreview = false;
        }
    }

    /// <summary>
    /// Fetches raw image bytes for a path or URL.
    /// If an <paramref name="illustId"/> is provided and the URL looks like a Pixiv thumbnail,
    /// it is resolved to the true Original first; if the Original 404s, the original input URL is used as a fallback.
    /// Local files bypass resolution.
    /// </summary>
    public async Task<byte[]?> FetchImageBytesAsync(string? pathOrUrl, string? illustId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var local = uri.LocalPath;
            if (File.Exists(local))
                return await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            return null;
        }

        if (File.Exists(pathOrUrl))
            return await File.ReadAllBytesAsync(pathOrUrl, ct).ConfigureAwait(false);

        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveToOriginalAsync(pathOrUrl, illustId, ct).ConfigureAwait(false);
            var url = string.IsNullOrWhiteSpace(resolved) ? pathOrUrl : resolved;

            if (!string.Equals(url, pathOrUrl, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var originalBytes = await _imageLoader.FetchBytesAsync(url, ct).ConfigureAwait(false);
                    if (originalBytes is { Length: > 0 }) return originalBytes;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Resolved original URL {ResolvedUrl} failed for {Url}; falling back", url, pathOrUrl);
                }
            }

            try
            {
                return await _imageLoader.FetchBytesAsync(pathOrUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch overlay image bytes from {Url}", pathOrUrl);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a Pixiv thumbnail/master URL to the true Original URL.
    /// Local file paths and non-Pixiv URLs are returned unchanged.
    /// </summary>
    private async Task<string?> ResolveToOriginalAsync(string? url, string? illustId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            return url;

        if (File.Exists(url))
            return url;

        // A bare artwork ID ("40594") or an artwork page link rather than a direct image URL.
        // Resolve it to that artwork's Original image. Without this an ID fell straight through
        // the http checks below, came back unchanged, and then failed to fetch as neither a local
        // file nor a URL — so nothing was ever added.
        if (TryExtractArtworkId(url) is { } artworkId)
        {
            var fromId = await ResolveArtworkIdToOriginalAsync(artworkId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromId)) return fromId;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        // Already a Pixiv original (or an unrecognised non-Pixiv URL) — skip the API call.
        if (LooksLikeOriginalUrl(url))
            return url;

        if (!string.IsNullOrWhiteSpace(illustId))
        {
            try
            {
                var pages = await _pixivClient.GetArtworkPagesAsync(illustId, ct).ConfigureAwait(false);
                if (pages.Count > 0 && !string.IsNullOrWhiteSpace(pages[0].Urls.Original))
                    return pages[0].Urls.Original;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve artwork pages for illust {IllustId}", illustId);
            }
        }

        var converted = PixivImageLoader.ConvertUrlForThumbnailSize(url, ThumbnailSize.Original);
        return string.IsNullOrEmpty(converted) ? url : converted;
    }

    /// <summary>
    /// Recognises a Pixiv artwork reference that isn't a direct image URL, so the background
    /// overlay can be set from an artwork ID the user already has to hand. Accepts:
    /// a bare numeric ID (<c>40594</c>), an artwork page link
    /// (<c>pixiv.net/artworks/40594</c>, <c>pixiv.net/en/artworks/40594</c>), and the legacy
    /// <c>member_illust.php?illust_id=40594</c> form. Returns null for anything else.
    /// </summary>
    private static string? TryExtractArtworkId(string? input)
    {
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        // Plain ID. Length-capped so a long digit run (e.g. an odd filename) isn't mistaken
        // for one — real Pixiv illust IDs are well under this.
        if (s.Length <= 12 && s.All(char.IsAsciiDigit)) return s;

        if (!s.Contains("pixiv.net", StringComparison.OrdinalIgnoreCase)) return null;

        var match = Regex.Match(s, @"/artworks/(\d+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(s, @"[?&]illust_id=(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Looks up an artwork by ID and returns its first page's Original image URL.</summary>
    private async Task<string?> ResolveArtworkIdToOriginalAsync(string artworkId, CancellationToken ct)
    {
        try
        {
            var pages = await _pixivClient.GetArtworkPagesAsync(artworkId, ct).ConfigureAwait(false);
            var first = pages.FirstOrDefault();
            if (first is null) return null;
            return first.Urls.Original ?? first.Urls.Regular ?? first.Urls.Small;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve artwork id {Id} for background overlay", artworkId);
            return null;
        }
    }

    private static bool LooksLikeOriginalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Local files and non-Pixiv URLs are out of our reach; use them as-is.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            return true;
        if (File.Exists(url))
            return true;
        if (!url.Contains("pximg.net", StringComparison.OrdinalIgnoreCase))
            return true;

        // A Pixiv URL is only definitively original when it lives under /img-original/.
        // Anything else (img-master, /c/ resizes, custom-thumb, master/square suffixes)
        // should be resolved through the artwork page so we get the true full-resolution URL.
        if (url.Contains("/img-original/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void Persist()
    {
        if (_isReloading) return;
        _isPersisting = true;
        try
        {
            _settingsService.Update(s =>
            {
                s.BackgroundOverlayEnabled = IsEnabled;
                s.BackgroundOverlayImageOpacity = ImageOpacity;
                s.BackgroundOverlayBrightness = Brightness;
                s.BackgroundOverlayDarkness = Darkness;
                s.BackgroundOverlayUseGlobalOverrides = UseGlobalOverrides;
                s.BackgroundOverlayCycleInterval = CycleInterval;
                s.BackgroundOverlayCycleMode = SelectedCycleModeIndex;
                s.BackgroundOverlayImagePaths = ImagePaths.ToList();
                s.BackgroundOverlayImageEntries = ImageItems.Select(i => i.Entry).ToList();
            });
        }
        finally { _isPersisting = false; }
        _overlaySignature = OverlaySignature();
        UpdateTimer();
        _ = LoadCurrentImageAsync();
    }

    private async Task ReloadFromSettingsAsync()
    {
        if (_isReloading) return;
        _isReloading = true;
        _overlaySignature = OverlaySignature();
        try
        {
            var s = _settingsService.Current;
            var entries = s.BackgroundOverlayImageEntries ?? new List<OverlayImageEntry>();
            var paths = (s.BackgroundOverlayImagePaths ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

            // Resolve each stored thumbnail to the true Original before populating the UI,
            // using the persisted IllustId when available.
            var resolved = new List<(string Path, OverlayImageEntry Entry)>(paths.Count);
            foreach (var p in paths)
            {
                var entry = entries.FirstOrDefault(e => e.Path == p)
                            ?? new OverlayImageEntry { Path = p };
                var r = await ResolveToOriginalAsync(p, entry.IllustId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(r)) continue;
                entry.Path = r;
                resolved.Add((r, entry));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsEnabled = s.BackgroundOverlayEnabled;
                ImageOpacity = Clamp(s.BackgroundOverlayImageOpacity, 0.0, 1.0);
                Brightness = Clamp(s.BackgroundOverlayBrightness, 0.0, 1.0);
                Darkness = Clamp(s.BackgroundOverlayDarkness, 0.0, 1.0);
                UseGlobalOverrides = s.BackgroundOverlayUseGlobalOverrides;
                CycleInterval = Math.Max(1, s.BackgroundOverlayCycleInterval);
                SelectedCycleModeIndex = Clamp(s.BackgroundOverlayCycleMode, 0, CycleModeOptions.Length - 1);

                ImagePaths.Clear();
                foreach (var item in ImageItems) item.Thumbnail?.Dispose();
                ImageItems.Clear();

                foreach (var (p, entry) in resolved)
                {
                    ImagePaths.Add(p);
                    var itm = new OverlayImageItem(p, entry);
                    ImageItems.Add(itm);
                    _ = LoadThumbnailAsync(itm);
                }

                if (_currentIndex >= ImagePaths.Count)
                    _currentIndex = Math.Max(0, ImagePaths.Count - 1);

                UpdateTimer();
                _ = LoadCurrentImageAsync();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to reload background overlay settings");
        }
        finally
        {
            _isReloading = false;
        }
    }

    private void Advance()
    {
        if (!IsEnabled || ImagePaths.Count <= 1) return;

        switch (CycleMode)
        {
            case BackgroundOverlayCycleMode.SequentialSeconds:
            case BackgroundOverlayCycleMode.SequentialMinutes:
                _currentIndex = (_currentIndex + 1) % ImagePaths.Count;
                break;
            case BackgroundOverlayCycleMode.RandomSeconds:
            case BackgroundOverlayCycleMode.RandomMinutes:
                var next = _rng.Next(ImagePaths.Count);
                _currentIndex = next == _currentIndex && ImagePaths.Count > 1
                    ? (next + 1) % ImagePaths.Count
                    : next;
                break;
        }

        _ = LoadCurrentImageAsync();
    }

    private void UpdateTimer()
    {
        _timer.Stop();
        if (!IsEnabled || ImagePaths.Count <= 1)
            return;

        var isMinutes = CycleMode is BackgroundOverlayCycleMode.SequentialMinutes or BackgroundOverlayCycleMode.RandomMinutes;
        _timer.Interval = isMinutes
            ? TimeSpan.FromMinutes(Math.Max(1, CycleInterval))
            : TimeSpan.FromSeconds(Math.Max(1, CycleInterval));
        _timer.Start();
    }

    private async Task LoadCurrentImageAsync()
    {
        if (!IsEnabled || ImagePaths.Count == 0 || _currentIndex < 0 || _currentIndex >= ImagePaths.Count)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var old = CurrentImage;
                CurrentImage = null;
                old?.Dispose();
            });
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var rawUrl = ImagePaths[_currentIndex];
        OverlayImageItem? item = null;
        string? illustId = null;
        if (_currentIndex < ImageItems.Count)
        {
            item = ImageItems[_currentIndex];
            illustId = item.Entry.IllustId;
        }

        try
        {
            var resolved = await ResolveToOriginalAsync(rawUrl, illustId, ct).ConfigureAwait(false);
            var url = string.IsNullOrWhiteSpace(resolved) ? rawUrl : resolved;

            // Keep the in-memory list and entry pointed at the original after the first resolve.
            if (item != null && !ct.IsCancellationRequested &&
                !string.Equals(url, rawUrl, StringComparison.OrdinalIgnoreCase))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_currentIndex >= ImageItems.Count || ImageItems[_currentIndex] != item) return;
                    ImagePaths[_currentIndex] = url;
                    item.Path = url;
                    item.Entry.Path = url;
                });
            }

            byte[]? bytes = null;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                var local = uri.LocalPath;
                if (File.Exists(local))
                    bytes = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            }
            else if (File.Exists(url))
            {
                bytes = await File.ReadAllBytesAsync(url, ct).ConfigureAwait(false);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(url, rawUrl, StringComparison.OrdinalIgnoreCase))
                {
                    try { bytes = await _imageLoader.FetchBytesAsync(url, ct).ConfigureAwait(false); }
                    catch { bytes = null; }
                }
                if (bytes is null || bytes.Length == 0)
                    bytes = await _imageLoader.FetchBytesAsync(rawUrl, ct).ConfigureAwait(false);
            }

            if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    // Apply per-image settings BEFORE swapping the image to avoid
                    // a brief flash where default/stale values are visible.
                    ApplyPerImageSettings();
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    var old = CurrentImage;
                    CurrentImage = bmp;
                    old?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to decode overlay image {Url}", url);
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { /* expected on rapid switch */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load overlay image {Url}", rawUrl);
        }
    }

    /// <summary>Applies per-image settings (pan, opacity, brightness, darkness, zoom) for the currently displayed image.
    /// When UseGlobalOverrides is true, the global slider values are used instead of per-image ones.
    /// Sets backing fields directly to avoid triggering OnPropertyChanged -> Persist loops.</summary>
#pragma warning disable MVVMTK0034 // Direct field access is intentional to bypass OnXxxChanged -> Persist() loops
    private void ApplyPerImageSettings()
    {
        if (_currentIndex >= 0 && _currentIndex < ImageItems.Count)
        {
            var e = ImageItems[_currentIndex].Entry;
            _panX = e.PanX;
            _panY = e.PanY;
            _zoom = Math.Max(0.1, e.Zoom);

            if (!UseGlobalOverrides)
            {
                _imageOpacity = Clamp(e.Opacity, 0.0, 1.0);
                _brightness = Clamp(e.Brightness, 0.0, 1.0);
                _darkness = Clamp(e.Darkness, 0.0, 1.0);
                OnPropertyChanged(nameof(ImageOpacity));
                OnPropertyChanged(nameof(Brightness));
                OnPropertyChanged(nameof(Darkness));
            }

            OnPropertyChanged(nameof(PanX));
            OnPropertyChanged(nameof(PanY));
            OnPropertyChanged(nameof(Zoom));
        }
        else
        {
            _panX = 0;
            _panY = 0;
            _zoom = 1.0;
            OnPropertyChanged(nameof(PanX));
            OnPropertyChanged(nameof(PanY));
            OnPropertyChanged(nameof(Zoom));
        }
    }
#pragma warning restore MVVMTK0034

    /// <summary>Loads a small thumbnail bitmap for display in the Settings panel.</summary>
    internal async Task LoadThumbnailAsync(OverlayImageItem item)
    {
        try
        {
            byte[]? bytes = null;
            var url = item.Path;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                var local = uri.LocalPath;
                if (File.Exists(local))
                    bytes = await File.ReadAllBytesAsync(local).ConfigureAwait(false);
            }
            else if (File.Exists(url))
            {
                bytes = await File.ReadAllBytesAsync(url).ConfigureAwait(false);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bytes = await _imageLoader.FetchBytesAsync(url).ConfigureAwait(false);
            }

            if (bytes is null || bytes.Length == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    item.Thumbnail = bmp;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to decode thumbnail for {Url}", url);
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load thumbnail for {Url}", item.Path);
        }
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
