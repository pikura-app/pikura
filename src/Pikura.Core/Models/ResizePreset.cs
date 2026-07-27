namespace Pikura.Core.Models;

/// <summary>
/// Built-in device presets for image resizing.
/// </summary>
public enum DevicePreset
{
    None,
    Original,
    Custom,
    // Phones
    iPhone16ProMax,
    iPhone16Pro,
    iPhone16,
    iPhoneSE,
    SamsungS25Ultra,
    SamsungS25,
    GooglePixel9Pro,
    GooglePixel9,
    // Tablets
    iPadPro13,
    iPadPro11,
    iPadAir,
    iPadMini,
    SamsungTabS10Ultra,
    // Desktops/Laptops
    Desktop1080p,
    Desktop1440p,
    Desktop4K,
    MacBookPro14,
    MacBookPro16,
    MacBookAir,
    // Social Media
    InstagramPost,
    InstagramStory,
    TwitterPost,
    TwitterHeader,
    FacebookCover,
    LinkedInBanner,
    YouTubeThumbnail,
    // Wallpapers
    Wallpaper1080p,
    Wallpaper1440p,
    Wallpaper4K,
    WallpaperUltraWide,
    WallpaperPhone
}

/// <summary>
/// Resize mode determines how the image is fitted to target dimensions.
/// </summary>
public enum ResizeMode
{
    /// <summary>Letterbox to fit within target (no cropping, maintains aspect ratio).</summary>
    Fit,
    /// <summary>Crop to fill target exactly (center anchor, maintains aspect ratio).</summary>
    Fill,
    /// <summary>Distort to fill target (does not maintain aspect ratio).</summary>
    Stretch
}

/// <summary>
/// Output format for resized images.
/// </summary>
public enum ResizeOutputFormat
{
    KeepOriginal,
    Jpeg,
    Png,
    Webp
}

/// <summary>
/// Aspect ratio presets for cropping.
/// </summary>
public enum AspectRatioPreset
{
    Free,
    Original,
    Square,
    Ratio16_9,
    Ratio9_16,
    Ratio4_3,
    Ratio3_4,
    Ratio21_9,
    Ratio2_3,
    Ratio3_2,
    Ratio5_4,
    Ratio4_5
}

/// <summary>
/// Resize settings for image processing.
/// </summary>
public class ResizeSettings
{
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public ResizeMode Mode { get; set; } = ResizeMode.Fit;
    public ResizeOutputFormat OutputFormat { get; set; } = ResizeOutputFormat.KeepOriginal;
    public int JpegQuality { get; set; } = 90;
    public bool LockAspectRatio { get; set; } = true;
    public AspectRatioPreset AspectRatio { get; set; } = AspectRatioPreset.Original;
    public bool IsPortrait { get; set; } = false;

    /// <summary>
    /// Horizontal crop offset for <see cref="ResizeMode.Fill"/>, normalized to
    /// the range [-1, 1]. 0 = centered (default), -1 = anchored to the left
    /// edge of the source after scaling, +1 = anchored to the right edge.
    /// </summary>
    public double CropOffsetX { get; set; } = 0;

    /// <summary>
    /// Vertical crop offset for <see cref="ResizeMode.Fill"/>, normalized to
    /// the range [-1, 1]. 0 = centered (default), -1 = top, +1 = bottom.
    /// </summary>
    public double CropOffsetY { get; set; } = 0;

    /// <summary>
    /// Gets dimensions for a device preset.
    /// </summary>
    public static (int width, int height) GetPresetDimensions(DevicePreset preset, bool portrait = false)
    {
        var (w, h) = preset switch
        {
            // iPhones
            DevicePreset.iPhone16ProMax => (1320, 2868),
            DevicePreset.iPhone16Pro => (1206, 2622),
            DevicePreset.iPhone16 => (1179, 2556),
            DevicePreset.iPhoneSE => (750, 1334),
            // Samsung
            DevicePreset.SamsungS25Ultra => (1440, 3120),
            DevicePreset.SamsungS25 => (1080, 2340),
            // Google Pixel
            DevicePreset.GooglePixel9Pro => (1280, 2856),
            DevicePreset.GooglePixel9 => (1080, 2424),
            // iPads
            DevicePreset.iPadPro13 => (2064, 2752),
            DevicePreset.iPadPro11 => (1668, 2388),
            DevicePreset.iPadAir => (1640, 2360),
            DevicePreset.iPadMini => (1488, 2266),
            // Samsung Tab
            DevicePreset.SamsungTabS10Ultra => (1848, 2960),
            // Desktops
            DevicePreset.Desktop1080p => (1920, 1080),
            DevicePreset.Desktop1440p => (2560, 1440),
            DevicePreset.Desktop4K => (3840, 2160),
            // MacBooks
            DevicePreset.MacBookPro14 => (3024, 1964),
            DevicePreset.MacBookPro16 => (3456, 2234),
            DevicePreset.MacBookAir => (2560, 1664),
            // Social Media
            DevicePreset.InstagramPost => (1080, 1080),
            DevicePreset.InstagramStory => (1080, 1920),
            DevicePreset.TwitterPost => (1200, 675),
            DevicePreset.TwitterHeader => (1500, 500),
            DevicePreset.FacebookCover => (820, 312),
            DevicePreset.LinkedInBanner => (1584, 396),
            DevicePreset.YouTubeThumbnail => (1280, 720),
            // Wallpapers
            DevicePreset.Wallpaper1080p => (1920, 1080),
            DevicePreset.Wallpaper1440p => (2560, 1440),
            DevicePreset.Wallpaper4K => (3840, 2160),
            DevicePreset.WallpaperUltraWide => (3440, 1440),
            // Stored landscape-first (w, h); GetPresetDimensions swaps when IsPortrait=true.
            DevicePreset.WallpaperPhone => (3840, 2160),
            _ => (0, 0)
        };

        return portrait ? (h, w) : (w, h);
    }

    /// <summary>
    /// Gets the display name for a device preset.
    /// </summary>
    public static string GetPresetDisplayName(DevicePreset preset)
    {
        return preset switch
        {
            DevicePreset.None => "None",
            DevicePreset.Original => "Original (No Resize)",
            DevicePreset.Custom => "Custom",
            DevicePreset.iPhone16ProMax => "iPhone 16 Pro Max",
            DevicePreset.iPhone16Pro => "iPhone 16 Pro",
            DevicePreset.iPhone16 => "iPhone 16",
            DevicePreset.iPhoneSE => "iPhone SE",
            DevicePreset.SamsungS25Ultra => "Samsung Galaxy S25 Ultra",
            DevicePreset.SamsungS25 => "Samsung Galaxy S25",
            DevicePreset.GooglePixel9Pro => "Google Pixel 9 Pro",
            DevicePreset.GooglePixel9 => "Google Pixel 9",
            DevicePreset.iPadPro13 => "iPad Pro 13\"",
            DevicePreset.iPadPro11 => "iPad Pro 11\"",
            DevicePreset.iPadAir => "iPad Air",
            DevicePreset.iPadMini => "iPad Mini",
            DevicePreset.SamsungTabS10Ultra => "Samsung Tab S10 Ultra",
            DevicePreset.Desktop1080p => "Desktop (1080p)",
            DevicePreset.Desktop1440p => "Desktop (1440p)",
            DevicePreset.Desktop4K => "Desktop (4K)",
            DevicePreset.MacBookPro14 => "MacBook Pro 14\"",
            DevicePreset.MacBookPro16 => "MacBook Pro 16\"",
            DevicePreset.MacBookAir => "MacBook Air",
            DevicePreset.InstagramPost => "Instagram Post (1:1)",
            DevicePreset.InstagramStory => "Instagram Story (9:16)",
            DevicePreset.TwitterPost => "Twitter/X Post (16:9)",
            DevicePreset.TwitterHeader => "Twitter/X Header",
            DevicePreset.FacebookCover => "Facebook Cover",
            DevicePreset.LinkedInBanner => "LinkedIn Banner",
            DevicePreset.YouTubeThumbnail => "YouTube Thumbnail",
            DevicePreset.Wallpaper1080p => "Wallpaper (1080p)",
            DevicePreset.Wallpaper1440p => "Wallpaper (1440p)",
            DevicePreset.Wallpaper4K => "Wallpaper (4K)",
            DevicePreset.WallpaperUltraWide => "Wallpaper (UltraWide)",
            DevicePreset.WallpaperPhone => "Wallpaper (Phone 4K)",
            _ => preset.ToString()
        };
    }
}

/// <summary>
/// Image adjustment parameters for the editing pipeline.
/// All values are in range -100 to +100 unless otherwise specified.
/// </summary>
public class ImageAdjustments
{
    public int Brightness { get; set; } = 0;           // -100 to +100
    public int Contrast { get; set; } = 0;           // -100 to +100
    public int Saturation { get; set; } = 100;       // 0 (greyscale) to 200 (vivid), default 100
    public int HueRotation { get; set; } = 0;         // -180 to +180 degrees
    public int Temperature { get; set; } = 0;         // -100 (cool) to +100 (warm)
    public int Tint { get; set; } = 0;                // -100 (green) to +100 (magenta)
    public int Highlights { get; set; } = 0;          // -100 to +100
    public int Shadows { get; set; } = 0;             // -100 to +100
    public int Sharpness { get; set; } = 0;            // 0 to 100
    public int BlurRadius { get; set; } = 0;           // 0 to 20 (Gaussian blur radius)
    public int VignetteIntensity { get; set; } = 0;    // 0 to 100
    public int VignetteRadius { get; set; } = 50;      // 0 to 100
    public string? ColorOverlayHex { get; set; }        // Hex color for overlay
    public int ColorOverlayOpacity { get; set; } = 0;  // 0 to 100
    public int Opacity { get; set; } = 100;             // 0 (transparent) to 100 (fully opaque)

    /// <summary>
    /// Resets all adjustments to default values.
    /// </summary>
    public void Reset()
    {
        Brightness = 0;
        Contrast = 0;
        Saturation = 100;
        HueRotation = 0;
        Temperature = 0;
        Tint = 0;
        Highlights = 0;
        Shadows = 0;
        Sharpness = 0;
        BlurRadius = 0;
        VignetteIntensity = 0;
        VignetteRadius = 50;
        ColorOverlayHex = null;
        ColorOverlayOpacity = 0;
        Opacity = 100;
    }

    /// <summary>
    /// Returns true if any adjustment is non-default.
    /// </summary>
    public bool HasAdjustments
    {
        get
        {
            return Brightness != 0 ||
                Contrast != 0 ||
                Saturation != 100 ||
                HueRotation != 0 ||
                Temperature != 0 ||
                Tint != 0 ||
                Highlights != 0 ||
                Shadows != 0 ||
                Sharpness != 0 ||
                BlurRadius != 0 ||
                VignetteIntensity != 0 ||
                !string.IsNullOrEmpty(ColorOverlayHex) ||
                ColorOverlayOpacity != 0 ||
                Opacity != 100;
        }
    }
}

/// <summary>
/// Crop region for image editing.
/// </summary>
public class CropRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsRelative { get; set; } = false; // If true, values are 0.0-1.0 ratios
}

/// <summary>
/// How to handle ugoira (animated) artworks.
/// </summary>
public enum UgoiraPresetMode
{
    /// <summary>Export as animated format (WebP/MP4/GIF).</summary>
    WatchAnimation,
    /// <summary>Extract and edit a single frame as static image.</summary>
    EditSingleFrame,
    /// <summary>Extract and edit all frames for batch processing.</summary>
    EditAllFrames,
    /// <summary>Extract only the cover (first) frame.</summary>
    EditCover
}

/// <summary>
/// Complete image edit preset including resize settings and adjustments.
/// </summary>
public class ImageEditPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Custom";
    public bool IsBuiltIn { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    // Resize settings
    public DevicePreset DevicePreset { get; set; } = DevicePreset.Original;
    public ResizeSettings ResizeSettings { get; set; } = new();

    // Adjustments
    public ImageAdjustments Adjustments { get; set; } = new();

    // Crop
    public CropRegion? CropRegion { get; set; }

    // Ugoira (animated) handling
    public UgoiraPresetMode UgoiraMode { get; set; } = UgoiraPresetMode.EditSingleFrame;
    /// <summary>Which formats to create for ugoira downloads (null = use global settings).</summary>
    public List<UgoiraFormat>? UgoiraFormats { get; set; }
    /// <summary>When true, saves individual processed frames to a subfolder.</summary>
    public bool SaveUgoiraFrames { get; set; } = false;
    /// <summary>When true, only saves frames without encoding animation.</summary>
    public bool UgoiraFramesOnly { get; set; } = false;

    // Output options
    public bool SaveAsNew { get; set; } = true;
    public bool ApplyToAllPages { get; set; } = true;
    /// <summary>
    /// When ApplyToAllPages is false, this contains the 0-based indices of pages to process.
    /// If null/empty, all pages are processed (fallback).
    /// </summary>
    public List<int>? SelectedPageIndices { get; set; }
    /// <summary>
    /// When true, only pages that have an explicit per-page preset are downloaded/processed.
    /// This is computed per artwork using PerPagePresets and the batch artwork index.
    /// </summary>
    public bool DownloadOnlyPagesWithPresets { get; set; } = false;
    /// <summary>
    /// Per-artwork/per-page preset overrides used by the batch download preset dialog.
    /// Key: (artwork index among selected artworks, page index within that artwork) -> override preset.
    /// Entries without a matching override use this preset as fallback.
    /// </summary>
    public Dictionary<(int ArtworkIndex, int PageIndex), ImageEditPreset>? PerPagePresets { get; set; }
    public string? CustomOutputFolder { get; set; }
    /// <summary>
    /// When true, downloads an unprocessed copy alongside any processed versions.
    /// Skips the existing file check for unprocessed files.
    /// </summary>
    public bool AlsoDownloadUnprocessed { get; set; } = false;

    /// <summary>
    /// Gets built-in presets.
    /// </summary>
    public static List<ImageEditPreset> GetBuiltInPresets()
    {
        var presets = new List<ImageEditPreset>
        {
            new()
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Original (No Processing)",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Original
            },
            new()
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Phone Wallpaper (4K)",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.WallpaperPhone,
                ResizeSettings = new ResizeSettings
                {
                    Mode = ResizeMode.Fill,
                    OutputFormat = ResizeOutputFormat.Jpeg,
                    JpegQuality = 95,
                    // Phone wallpapers default to portrait so the preview matches
                    // the on-device aspect ratio (2160x3840).
                    IsPortrait = true
                }
            },
            new()
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Name = "Desktop Wallpaper (4K)",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Wallpaper4K,
                ResizeSettings = new ResizeSettings
                {
                    Mode = ResizeMode.Fill,
                    OutputFormat = ResizeOutputFormat.Jpeg,
                    JpegQuality = 95
                }
            },
            new()
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                Name = "Instagram Post",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.InstagramPost,
                ResizeSettings = new ResizeSettings
                {
                    Mode = ResizeMode.Fill,
                    OutputFormat = ResizeOutputFormat.Jpeg,
                    JpegQuality = 90
                }
            },
            new()
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Name = "Vivid Colors",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Original,
                Adjustments = new ImageAdjustments
                {
                    Saturation = 130,
                    Contrast = 10,
                    Sharpness = 20
                }
            },
            new()
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                Name = "Black & White",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Original,
                Adjustments = new ImageAdjustments
                {
                    Saturation = 0,
                    Contrast = 15
                }
            },
            new()
            {
                Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                Name = "Warm Vintage",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Original,
                Adjustments = new ImageAdjustments
                {
                    Temperature = 25,
                    Saturation = 85,
                    Contrast = -10,
                    VignetteIntensity = 30
                }
            },
            new()
            {
                Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                Name = "Cool Modern",
                IsBuiltIn = true,
                DevicePreset = DevicePreset.Original,
                Adjustments = new ImageAdjustments
                {
                    Temperature = -20,
                    Saturation = 110,
                    Contrast = 15,
                    Sharpness = 30
                }
            }
        };

        return presets;
    }
}

/// <summary>
/// User's preset storage.
/// </summary>
public class UserPresets
{
    public List<ImageEditPreset> CustomPresets { get; set; } = new();
    public Guid? LastUsedPresetId { get; set; }
}
