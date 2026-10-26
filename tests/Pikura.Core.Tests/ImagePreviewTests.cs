using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Core.Models;
using Pikura.Core.Services;
using SkiaSharp;
using Xunit;

namespace Pikura.Core.Tests;

public class ImagePreviewTests
{
    private static SKBitmap Source(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.CornflowerBlue);
        return bmp;
    }

    [Fact]
    public async Task Preview_Of_4K_Preset_Renders_At_Preview_Size()
    {
        var svc = new ImageResizeService(NullLogger<ImageResizeService>.Instance);
        using var src = Source(600, 900);
        var preset = new ImageEditPreset
        {
            DevicePreset = DevicePreset.WallpaperPhone,
            ResizeSettings = new ResizeSettings { IsPortrait = true, Mode = ResizeMode.Fill },
            Adjustments = new ImageAdjustments { Brightness = 20, BlurRadius = 10 }
        };

        using var preview = await svc.ProcessForPreviewAsync(src, preset, default, 600);

        Assert.NotNull(preview);
        Assert.Equal(600, preview!.Width);
        // Same aspect as the 2160×3840 target.
        Assert.InRange(preview.Height, 1066, 1068);
        // The live preset must not be modified by preview scaling.
        Assert.Equal(DevicePreset.WallpaperPhone, preset.DevicePreset);
        Assert.Equal(10, preset.Adjustments.BlurRadius);
    }

    [Fact]
    public async Task Preview_Survives_Caller_Disposing_Source()
    {
        var svc = new ImageResizeService(NullLogger<ImageResizeService>.Instance);
        var src = Source(600, 400);
        var task = svc.ProcessForPreviewAsync(src, new ImageEditPreset { Adjustments = new ImageAdjustments { Contrast = 30 } }, default, 600);
        src.Dispose();
        using var preview = await task;
        Assert.NotNull(preview);
        Assert.Equal(600, preview!.Width);
    }
}
