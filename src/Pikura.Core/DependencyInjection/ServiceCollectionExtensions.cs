using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pikura.Core.Http;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Core engine services (settings, http, client, downloader) as singletons.</summary>
    public static IServiceCollection AddPixivCore(this IServiceCollection services)
    {
        // Explicit factory so MS DI doesn't try to resolve the optional `string?` parameter.
        services.TryAddSingleton(_ => new SettingsService());
        services.TryAddSingleton<DeviceCapabilityService>();
        services.TryAddSingleton<PixivHttpClientFactory>();
        services.TryAddSingleton<PixivClient>();
        services.TryAddSingleton<ImageResizeService>();
        services.TryAddSingleton<FfmpegService>();
        services.TryAddSingleton<UgoiraService>();
        services.TryAddSingleton<PixivDownloadService>();
        services.TryAddSingleton<PixivImageLoader>();
        return services;
    }
}
