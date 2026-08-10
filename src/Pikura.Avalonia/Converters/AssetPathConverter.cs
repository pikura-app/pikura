using Avalonia.Data.Converters;
using Avalonia.Platform;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Converts an <c>avares://</c> asset URI into a temporary file path so controls
/// (e.g. <see cref="Controls.AnimatedImage"/>) can read it from disk.
/// </summary>
public class AssetPathConverter : IValueConverter
{
    public static AssetPathConverter Instance { get; } = new();

    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pikura",
        "FeatureCache");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uriString || string.IsNullOrEmpty(uriString))
            return null;

        if (!uriString.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return uriString;

        if (_cache.TryGetValue(uriString, out var cached) && File.Exists(cached))
            return cached;

        try
        {
            Directory.CreateDirectory(_cacheDir);

            var uri = new Uri(uriString);
            var fileName = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrEmpty(fileName))
                return null;

            var tempPath = Path.Combine(_cacheDir, fileName);
            using var assetStream = AssetLoader.Open(uri);
            using var fileStream = File.Create(tempPath);
            assetStream.CopyTo(fileStream);

            _cache[uriString] = tempPath;
            return tempPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AssetPathConverter failed for {uriString}: {ex.Message}");
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
