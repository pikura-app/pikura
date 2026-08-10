using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace Pikura.Avalonia.Converters;

/// <summary>
/// Converts an <c>avares://</c> asset URI string into a Bitmap for display.
/// </summary>
public class AssetImageConverter : IValueConverter
{
    public static AssetImageConverter Instance { get; } = new();

    private static readonly ConcurrentDictionary<string, Bitmap> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string uriString || string.IsNullOrEmpty(uriString))
            return null;

        if (_cache.TryGetValue(uriString, out var cached))
            return cached;

        if (!uriString.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var assetStream = AssetLoader.Open(new Uri(uriString));
            var memoryStream = new MemoryStream();
            assetStream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            var bitmap = new Bitmap(memoryStream);
            _cache[uriString] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura", "image_debug.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(debugPath)!);
            File.AppendAllText(debugPath, $"[{DateTime.Now}] Failed to load {uriString}: {ex.Message}\n{ex.StackTrace}\n\n");
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
