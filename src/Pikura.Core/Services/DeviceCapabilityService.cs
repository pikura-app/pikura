using Microsoft.Extensions.Logging;

namespace Pikura.Core.Services;

/// <summary>Coarse capability bucket for the host machine.</summary>
public enum DeviceTier
{
    /// <summary>Few cores and/or little RAM — trim concurrency and cache sizes aggressively.</summary>
    Low,
    /// <summary>Typical modern laptop/desktop.</summary>
    Medium,
    /// <summary>Plenty of cores and RAM — safe to use the previous hardcoded "assume beefy desktop" limits.</summary>
    High
}

/// <summary>
/// Detects a rough CPU/RAM budget for the current machine at startup and derives a handful of
/// concurrency/cache-size knobs from it. Several places in the app (image loader concurrency,
/// decoded-bitmap cache size, followed-artist page fetch parallelism, etc.) previously used
/// hardcoded constants tuned for a "typical desktop", which could over-commit threads, sockets,
/// and memory on lower-end machines (older laptops, budget devices, VMs with few cores). This
/// service lets those call sites scale down automatically instead of needing separate manual
/// settings for every low-end user.
/// </summary>
public sealed class DeviceCapabilityService
{
    public int ProcessorCount { get; }
    public long AvailableMemoryBytes { get; }
    public DeviceTier Tier { get; }

    /// <summary>Max concurrent network fetches for <see cref="PixivImageLoader"/>.</summary>
    public int MaxImageFetchConcurrency { get; }
    /// <summary>Max decoded SKBitmap entries kept in <see cref="PixivImageLoader"/>'s memory cache.</summary>
    public int MaxBitmapCacheEntries { get; }
    /// <summary>Max raw-byte entries kept in <see cref="PixivImageLoader"/>'s in-flight/recent cache.</summary>
    public int MaxByteCacheEntries { get; }
    /// <summary>Max concurrent "followed artists" pagination requests issued at once.</summary>
    public int MaxParallelPageFetches { get; }

    public DeviceCapabilityService(ILogger<DeviceCapabilityService>? logger = null)
    {
        ProcessorCount = Environment.ProcessorCount;
        AvailableMemoryBytes = GetAvailableMemoryBytes();

        var lowMemory  = AvailableMemoryBytes > 0 && AvailableMemoryBytes < 4L * 1024 * 1024 * 1024;
        var highMemory = AvailableMemoryBytes >= 16L * 1024 * 1024 * 1024;

        if (ProcessorCount <= 4 || lowMemory)
            Tier = DeviceTier.Low;
        else if (ProcessorCount >= 8 && highMemory)
            Tier = DeviceTier.High;
        else
            Tier = DeviceTier.Medium;

        (MaxImageFetchConcurrency, MaxBitmapCacheEntries, MaxByteCacheEntries, MaxParallelPageFetches) = Tier switch
        {
            DeviceTier.Low  => (12, 64,  256,  4),
            DeviceTier.High => (48, 256, 1024, 16),
            _               => (28, 160, 640,  8),
        };

        logger?.LogInformation(
            "[DeviceCapability] Tier={Tier} Cores={Cores} AvailableMemory={MemoryMB}MB -> ImageFetchConcurrency={Fetch} BitmapCache={Bitmap} ByteCache={Bytes} ParallelPages={Pages}",
            Tier, ProcessorCount, AvailableMemoryBytes / (1024 * 1024),
            MaxImageFetchConcurrency, MaxBitmapCacheEntries, MaxByteCacheEntries, MaxParallelPageFetches);
    }

    /// <summary>
    /// Best-effort available-memory estimate. <see cref="GC.GetGCMemoryInfo"/> is cross-platform
    /// and container/cgroup-aware (no native P/Invoke needed), and reflects the memory budget
    /// the runtime itself is planning around, which is what we care about for cache sizing.
    /// </summary>
    private static long GetAvailableMemoryBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0) return info.TotalAvailableMemoryBytes;
        }
        catch { /* best-effort — callers treat 0 as "unknown" */ }
        return 0;
    }
}
