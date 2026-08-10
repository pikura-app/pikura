using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Observable wrapper around <see cref="OverlayImageEntry"/> for display in the Settings UI.
/// Holds a thumbnail bitmap and exposes the per-image path for identification.
/// </summary>
public sealed partial class OverlayImageItem : ObservableObject
{
    public string Path { get; }
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
        Path = path;
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
/// and cycles through up to five selected images on a timer.
/// </summary>
public sealed partial class BackgroundOverlayService : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PixivImageLoader _imageLoader;
    private readonly ILogger<BackgroundOverlayService> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private CancellationTokenSource? _loadCts;
    private bool _isReloading;
    private bool _isPersisting;
    private int _currentIndex;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private double _imageOpacity = 0.25;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _darkness;
    [ObservableProperty] private bool _useGlobalOverrides;
    [ObservableProperty] private int _cycleInterval = 30;
    [ObservableProperty] private int _selectedCycleModeIndex;
    [ObservableProperty] private Bitmap? _currentImage;

    /// <summary>Image URLs or local file paths used for the overlay (max 5).</summary>
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

    public BackgroundOverlayService(SettingsService settingsService, PixivImageLoader imageLoader, ILogger<BackgroundOverlayService> logger)
    {
        _settingsService = settingsService;
        _imageLoader = imageLoader;
        _logger = logger;
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => Advance();
        _settingsService.Changed += (_, _) =>
        {
            // Skip reload when we ourselves just wrote to settings — avoids
            // a Persist → Changed → ReloadFromSettings → flicker loop.
            if (_isPersisting) return;
            ReloadFromSettings();
        };
        ReloadFromSettings();
    }

    partial void OnIsEnabledChanged(bool value) { if (!_isReloading) Persist(); }
    partial void OnImageOpacityChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnBrightnessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnDarknessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnUseGlobalOverridesChanged(bool value) { if (!_isReloading) { Persist(); ApplyPerImageSettings(); } }
    partial void OnCycleIntervalChanged(int value) { if (!_isReloading) Persist(); }
    partial void OnSelectedCycleModeIndexChanged(int value) { if (!_isReloading) Persist(); }

    /// <summary>Adds an image to the overlay list with its per-image entry. If the list exceeds 5, the oldest is removed.</summary>
    public void AddImage(string? pathOrUrl, OverlayImageEntry? entry = null)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return;

        var url = pathOrUrl.Trim();
        var existing = ImagePaths.FirstOrDefault(p => p.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var idx = ImagePaths.IndexOf(existing);
            ImagePaths.RemoveAt(idx);
            if (idx < ImageItems.Count) ImageItems.RemoveAt(idx);
        }

        entry ??= new OverlayImageEntry { Path = url };
        entry.Path = url;

        ImagePaths.Add(url);
        var item = new OverlayImageItem(url, entry);
        ImageItems.Add(item);
        _ = LoadThumbnailAsync(item);

        if (ImagePaths.Count > 5)
        {
            ImagePaths.RemoveAt(0);
            if (ImageItems.Count > 5) ImageItems.RemoveAt(0);
        }

        _currentIndex = Math.Max(0, ImagePaths.Count - 1);
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
        UpdateTimer();
        _ = LoadCurrentImageAsync();
    }

    private void ReloadFromSettings()
    {
        _isReloading = true;
        try
        {
            var s = _settingsService.Current;
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
            var entries = s.BackgroundOverlayImageEntries ?? new();
            var paths = s.BackgroundOverlayImagePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i];
                var entry = entries.FirstOrDefault(e => e.Path == p)
                            ?? new OverlayImageEntry { Path = p };
                ImagePaths.Add(p);
                var itm = new OverlayImageItem(p, entry);
                ImageItems.Add(itm);
                _ = LoadThumbnailAsync(itm);
            }

            if (_currentIndex >= ImagePaths.Count)
                _currentIndex = Math.Max(0, ImagePaths.Count - 1);

            UpdateTimer();
            _ = LoadCurrentImageAsync();
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

        var url = ImagePaths[_currentIndex];
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
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
                // Try to load the original/full-resolution image first, then fall back
                // to the source URL (typically a 540px master thumbnail).
                var originalUrl = PixivImageLoader.ConvertUrlForThumbnailSize(url, ThumbnailSize.Original);
                if (!string.Equals(originalUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    try { bytes = await _imageLoader.FetchBytesAsync(originalUrl, ct).ConfigureAwait(false); }
                    catch { bytes = null; }
                }
                if (bytes is null || bytes.Length == 0)
                    bytes = await _imageLoader.FetchBytesAsync(url, ct).ConfigureAwait(false);
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
            _logger.LogDebug(ex, "Failed to load overlay image {Url}", url);
        }
    }

    /// <summary>Applies per-image settings (pan, opacity, brightness, darkness, zoom) for the currently displayed image.
    /// When UseGlobalOverrides is true, the global slider values are used instead of per-image ones.
    /// Sets backing fields directly to avoid triggering OnPropertyChanged → Persist loops.</summary>
#pragma warning disable MVVMTK0034 // Direct field access is intentional to bypass OnXxxChanged → Persist() loops
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
                    // Create a smaller decode for the thumbnail (80px height)
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

    /// <summary>Fetches raw image bytes for a path or URL — used by the preview window.</summary>
    public async Task<byte[]?> FetchImageBytesAsync(string pathOrUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var local = uri.LocalPath;
            if (File.Exists(local))
                return await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
        }
        else if (File.Exists(pathOrUrl))
        {
            return await File.ReadAllBytesAsync(pathOrUrl, ct).ConfigureAwait(false);
        }
        else if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await _imageLoader.FetchBytesAsync(pathOrUrl, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
