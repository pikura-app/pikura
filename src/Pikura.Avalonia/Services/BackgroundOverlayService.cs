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
    private int _currentIndex;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private double _imageOpacity = 0.25;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _darkness;
    [ObservableProperty] private int _cycleInterval = 30;
    [ObservableProperty] private int _selectedCycleModeIndex;
    [ObservableProperty] private Bitmap? _currentImage;

    /// <summary>Image URLs or local file paths used for the overlay (max 5).</summary>
    public ObservableCollection<string> ImagePaths { get; } = new();

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
        _settingsService.Changed += (_, _) => ReloadFromSettings();
        ReloadFromSettings();
    }

    partial void OnIsEnabledChanged(bool value) { if (!_isReloading) Persist(); }
    partial void OnImageOpacityChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnBrightnessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnDarknessChanged(double value) { if (!_isReloading) Persist(); }
    partial void OnCycleIntervalChanged(int value) { if (!_isReloading) Persist(); }
    partial void OnSelectedCycleModeIndexChanged(int value) { if (!_isReloading) Persist(); }

    /// <summary>Adds an image to the overlay list. If the list exceeds 5, the oldest is removed.</summary>
    public void AddImage(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return;

        var url = pathOrUrl.Trim();
        var existing = ImagePaths.FirstOrDefault(p => p.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            ImagePaths.Remove(existing);

        ImagePaths.Add(url);
        if (ImagePaths.Count > 5)
            ImagePaths.RemoveAt(0);

        _currentIndex = Math.Max(0, ImagePaths.Count - 1);
        Persist();
    }

    /// <summary>Removes the image at the specified index.</summary>
    public void RemoveImage(int index)
    {
        if (index < 0 || index >= ImagePaths.Count) return;
        ImagePaths.RemoveAt(index);
        if (_currentIndex >= ImagePaths.Count)
            _currentIndex = Math.Max(0, ImagePaths.Count - 1);
        Persist();
    }

    /// <summary>Clears all overlay images.</summary>
    public void ClearImages()
    {
        ImagePaths.Clear();
        _currentIndex = 0;
        CurrentImage = null;
        Persist();
    }

    private void Persist()
    {
        if (_isReloading) return;
        _settingsService.Update(s =>
        {
            s.BackgroundOverlayEnabled = IsEnabled;
            s.BackgroundOverlayImageOpacity = ImageOpacity;
            s.BackgroundOverlayBrightness = Brightness;
            s.BackgroundOverlayDarkness = Darkness;
            s.BackgroundOverlayCycleInterval = CycleInterval;
            s.BackgroundOverlayCycleMode = SelectedCycleModeIndex;
            s.BackgroundOverlayImagePaths = ImagePaths.ToList();
        });
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
            CycleInterval = Math.Max(1, s.BackgroundOverlayCycleInterval);
            SelectedCycleModeIndex = Clamp(s.BackgroundOverlayCycleMode, 0, CycleModeOptions.Length - 1);

            ImagePaths.Clear();
            foreach (var path in s.BackgroundOverlayImagePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
                ImagePaths.Add(path);

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
                bytes = await _imageLoader.FetchBytesAsync(url, ct).ConfigureAwait(false);
            }

            if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                try
                {
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

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
