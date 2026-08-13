using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace Pikura.Avalonia.Controls;

/// <summary>
/// An <see cref="Image"/> that plays an animated WebP / GIF / APNG file. Frames
/// are decoded once via <see cref="SKCodec"/> and cycled by a single
/// <see cref="DispatcherTimer"/>. Designed for short ugoira animations
/// (typically &lt;200 frames) — large GIFs will use proportional memory.
/// </summary>
public sealed class AnimatedImage : Image
{
    /// <summary>Path to the animated image file. Setting it to null/empty stops playback.</summary>
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<AnimatedImage, string?>(nameof(SourcePath));

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>Whether playback is currently looping.</summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AnimatedImage, bool>(nameof(IsPlaying), defaultValue: true);

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>Raised every time playback completes a full loop back to the first frame — used
    /// by callers (e.g. the feature-highlights carousel) that want to wait for a GIF to finish
    /// playing before moving on, rather than advancing on a fixed timer. Never raised for
    /// single-frame (static) sources.</summary>
    public event EventHandler? AnimationCompleted;

    private Bitmap[]? _frames;
    private int[]? _frameDurationsMs;
    private int _frameIndex;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _loadCts;
    private bool _ownsFrames;
    private readonly object _lock = new();

    private static readonly ConcurrentDictionary<string, (Bitmap[] Frames, int[] Delays)> _frameCache = new();

    // Only assets extracted for the feature-highlights splash are kept in the shared
    // cache — regular ugoira playback owns (and disposes) its frames per instance so
    // memory doesn't grow unboundedly while browsing.
    private static readonly string _cacheableDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pikura",
        "FeatureCache");

    private static bool ShouldCache(string path) =>
        path.StartsWith(_cacheableDir, StringComparison.OrdinalIgnoreCase);

    static AnimatedImage()
    {
        SourcePathProperty.Changed.AddClassHandler<AnimatedImage>(async (c, e) =>
        {
            // Cancel any in-flight load before starting new one
            c._loadCts?.Cancel();
            c._loadCts?.Dispose();
            c._loadCts = null;

            if (string.IsNullOrEmpty(e.NewValue?.ToString()))
            {
                c.Dispose();
                return;
            }

            var cts = new CancellationTokenSource();
            c._loadCts = cts;
            await c.ReloadAsync(cts.Token);
        });
        IsPlayingProperty.Changed.AddClassHandler<AnimatedImage>((c, e) =>
        {
            if (e.NewValue is true) c.StartTimer();
            else c.StopTimer();
        });
    }

    public AnimatedImage()
    {
        DetachedFromVisualTree += (_, _) => Dispose();
    }

    /// <summary>Stops playback and disposes pre-decoded frames this instance owns.</summary>
    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        lock (_lock)
        {
            StopTimer();
            // Clear source first to prevent rendering disposed bitmaps
            Source = null;
            if (_frames != null)
            {
                if (_ownsFrames)
                {
                    foreach (var f in _frames)
                    {
                        try { f.Dispose(); } catch { /* best-effort */ }
                    }
                }
                _frames = null;
                _frameDurationsMs = null;
                _frameIndex = 0;
                _ownsFrames = false;
            }
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        // Clean up previous frames synchronously
        lock (_lock)
        {
            StopTimer();
            Source = null;
            if (_frames != null)
            {
                if (_ownsFrames)
                {
                    foreach (var f in _frames)
                    {
                        try { f.Dispose(); } catch { }
                    }
                }
                _frames = null;
                _frameDurationsMs = null;
                _frameIndex = 0;
                _ownsFrames = false;
            }
        }

        if (ct.IsCancellationRequested) return;

        var path = SourcePath;
        if (string.IsNullOrEmpty(path)) return;

        // Use cached decoded frames if available (already on UI thread)
        if (_frameCache.TryGetValue(path, out var cached))
        {
            lock (_lock)
            {
                if (ct.IsCancellationRequested || SourcePath != path) return;

                _frames = cached.Frames;
                _frameDurationsMs = cached.Delays;
                _frameIndex = 0;
                _ownsFrames = false;
                Source = _frames[0];
                if (IsPlaying && _frames.Length > 1) StartTimer();
            }
            return;
        }

        SKBitmap[]? decodedSkBitmaps = null;
        int[]? decodedDelays = null;

        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    using var data = LoadSourceData(path);
                    if (data == null) return (null, null);
                    return DecodeAllFrames(data, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AnimatedImage failed to decode {path}: {ex.Message}");
                    return (null, null);
                }
            }, ct);

            decodedSkBitmaps = result.Frames;
            decodedDelays = result.DelaysMs;

            if (ct.IsCancellationRequested || decodedSkBitmaps == null || decodedSkBitmaps.Length == 0)
            {
                DisposeSkBitmaps(decodedSkBitmaps);
                return;
            }

            // Verify SourcePath hasn't changed while we were decoding
            if (SourcePath != path)
            {
                DisposeSkBitmaps(decodedSkBitmaps);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_lock)
                {
                    // Final check inside lock - bail if cancelled or path changed
                    if (ct.IsCancellationRequested || SourcePath != path)
                    {
                        DisposeSkBitmaps(decodedSkBitmaps);
                        return;
                    }

                    var bitmaps = new Bitmap[decodedSkBitmaps.Length];
                    for (int i = 0; i < bitmaps.Length; i++)
                    {
                        bitmaps[i] = decodedSkBitmaps[i] is null ? null! : SkBitmapToAvalonia(decodedSkBitmaps[i]);
                    }
                    DisposeSkBitmaps(decodedSkBitmaps);

                    var cacheable = ShouldCache(path);
                    if (cacheable) _frameCache[path] = (bitmaps, decodedDelays!);
                    _frames = bitmaps;
                    _frameDurationsMs = decodedDelays;
                    _frameIndex = 0;
                    _ownsFrames = !cacheable;
                    Source = _frames[0];
                    if (IsPlaying && _frames.Length > 1) StartTimer();
                }
            });
        }
        catch (OperationCanceledException)
        {
            DisposeSkBitmaps(decodedSkBitmaps);
        }
        catch (Exception ex)
        {
            DisposeSkBitmaps(decodedSkBitmaps);
            System.Diagnostics.Debug.WriteLine($"AnimatedImage failed to load {path}: {ex.Message}");
        }
    }

    private static void DisposeFrames(Bitmap[]? frames)
    {
        if (frames == null) return;
        foreach (var f in frames)
        {
            try { f.Dispose(); } catch { }
        }
    }

    private static void DisposeSkBitmaps(SKBitmap[]? frames)
    {
        if (frames == null) return;
        foreach (var f in frames)
        {
            try { f?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Decodes all frames of the given file into the shared cache so a later
    /// <see cref="SourcePath"/> assignment displays instantly. No-op if already cached.
    /// </summary>
    public static Task PreloadAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || _frameCache.ContainsKey(path))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                if (_frameCache.ContainsKey(path)) return;

                using var data = LoadSourceData(path);
                if (data == null) return;

                var (skFrames, delays) = DecodeAllFrames(data, CancellationToken.None);
                if (skFrames == null || skFrames.Length == 0 || delays == null) return;

                var bitmaps = new Bitmap[skFrames.Length];
                for (int i = 0; i < bitmaps.Length; i++)
                {
                    bitmaps[i] = skFrames[i] is null ? null! : SkBitmapToAvalonia(skFrames[i]);
                }
                DisposeSkBitmaps(skFrames);

                if (!_frameCache.TryAdd(path, (bitmaps, delays)))
                    DisposeFrames(bitmaps);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AnimatedImage preload failed for {path}: {ex.Message}");
            }
        });
    }

    /// <summary>Total playback duration of one full loop of a preloaded/cached animated source,
    /// in milliseconds — the sum of every frame's display delay. Returns null if <paramref
    /// name="path"/> hasn't been decoded/cached yet (e.g. via <see cref="PreloadAsync"/>), or has
    /// only a single frame (static image). Used by callers that want to time something to a
    /// GIF's actual length rather than a fixed interval.</summary>
    public static int? GetTotalDurationMs(string path)
    {
        if (!_frameCache.TryGetValue(path, out var cached) || cached.Delays.Length <= 1)
            return null;

        var total = 0;
        foreach (var d in cached.Delays) total += Math.Max(20, d);
        return total;
    }

    /// <summary>Clears the shared decoded-frame cache and releases its bitmaps.</summary>
    public static void ClearCache()
    {
        foreach (var (_, cached) in _frameCache)
        {
            DisposeFrames(cached.Frames);
        }
        _frameCache.Clear();
    }

    private void StartTimer()
    {
        if (_frames == null || _frames.Length <= 1 || _frameDurationsMs == null) return;
        StopTimer();
        var first = Math.Max(20, _frameDurationsMs[_frameIndex]);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(first), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool completedLoop;
        lock (_lock)
        {
            if (_frames == null || _frameDurationsMs == null || _frames.Length == 0)
            {
                StopTimer();
                return;
            }
            _frameIndex = (_frameIndex + 1) % _frames.Length;
            completedLoop = _frameIndex == 0;
            var frame = _frames[_frameIndex];
            if (frame != null)
                Source = frame;

            // Some animated formats use variable per-frame delays — recompute the
            // interval each tick so timing stays accurate.
            if (_timer != null)
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frameDurationsMs[_frameIndex]));
        }

        if (completedLoop) AnimationCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Decodes every frame of an animated image into Avalonia <see cref="Bitmap"/>s
    /// using <see cref="SKCodec"/>. Returns the per-frame display delay in ms
    /// (defaults to 80 ms when the codec doesn't supply one).
    /// </summary>
    private static (SKBitmap[]? Frames, int[]? DelaysMs) DecodeAllFrames(SKData data, CancellationToken ct)
    {
        using var codec = SKCodec.Create(data);
        if (codec == null) return (Array.Empty<SKBitmap>(), Array.Empty<int>());

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frameInfos = codec.FrameInfo;
        var count = Math.Max(1, frameInfos?.Length ?? 1);

        var frames = new SKBitmap[count];
        var delays = new int[count];

        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var bmp = new SKBitmap(info);
            var opts = new SKCodecOptions(i);
            var result = codec.GetPixels(info, bmp.GetPixels(), bmp.RowBytes, opts);
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                bmp.Dispose();
                continue;
            }

            frames[i] = bmp;
            delays[i] = frameInfos != null && i < frameInfos.Length && frameInfos[i].Duration > 0
                ? frameInfos[i].Duration
                : 80;
        }
        return (frames, delays);
    }

    private static unsafe Bitmap SkBitmapToAvalonia(SKBitmap source)
    {
        var size = new PixelSize(source.Width, source.Height);
        var dpi = new global::Avalonia.Vector(96, 96);
        var writeable = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = writeable.Lock();
        int rowBytes = source.Width * 4;
        byte* src = (byte*)source.GetPixels();
        byte* dst = (byte*)fb.Address;
        for (int y = 0; y < source.Height; y++)
        {
            Buffer.MemoryCopy(src + y * source.RowBytes, dst + y * fb.RowBytes, rowBytes, rowBytes);
        }
        return writeable;
    }

    private static SKData? LoadSourceData(string path)
    {
        try
        {
            if (path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = AssetLoader.Open(new Uri(path));
                return SKData.Create(stream);
            }

            using var fs = File.OpenRead(path);
            return SKData.Create(fs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AnimatedImage failed to read {path}: {ex.Message}");
            return null;
        }
    }
}
