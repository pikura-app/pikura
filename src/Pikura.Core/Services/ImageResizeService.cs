using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using SkiaSharp;

namespace Pikura.Core.Services;

/// <summary>
/// Service for resizing and applying image adjustments using SkiaSharp.
/// </summary>
public class ImageResizeService
{
    private readonly ILogger<ImageResizeService> _logger;

    public ImageResizeService(ILogger<ImageResizeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Processes an image file with the given preset and saves the result.
    /// </summary>
    public async Task<string?> ProcessAsync(string filePath, ImageEditPreset preset, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
            }

            // Load the image
            using var inputStream = File.OpenRead(filePath);
            using var originalBitmap = SKBitmap.Decode(inputStream);
            if (originalBitmap == null)
            {
                _logger.LogWarning("Failed to decode image: {FilePath}", filePath);
                return null;
            }

            // Process the image
            using var processedBitmap = await ProcessBitmapAsync(originalBitmap, preset, ct);
            if (processedBitmap == null)
            {
                _logger.LogWarning("Failed to process image: {FilePath}", filePath);
                return null;
            }

            // Determine output path
            string outputPath;
            if (preset.SaveAsNew)
            {
                var directory = preset.CustomOutputFolder != null && Directory.Exists(preset.CustomOutputFolder)
                    ? preset.CustomOutputFolder
                    : Path.GetDirectoryName(filePath)!;
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var extension = GetOutputExtension(preset);
                outputPath = Path.Combine(directory, $"{fileName}_processed{extension}");
            }
            else
            {
                outputPath = filePath;
            }

            // Save the processed image
            await SaveBitmapAsync(processedBitmap, outputPath, preset, ct);

            _logger.LogInformation("Processed image saved to: {OutputPath}", outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing image: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Processes a bitmap in memory (for preview).
    /// Returns a downscaled bitmap for preview (max maxPreviewWidth wide, default 800px).
    /// </summary>
    public async Task<SKBitmap?> ProcessForPreviewAsync(SKBitmap original, ImageEditPreset preset, CancellationToken ct = default, int maxPreviewWidth = 800)
    {
        try
        {
            SKBitmap previewBitmap;
            bool ownsPreviewBitmap;

            if (original.Width > maxPreviewWidth)
            {
                var scale = (float)maxPreviewWidth / original.Width;
                var newHeight = (int)(original.Height * scale);
                previewBitmap = original.Resize(new SKImageInfo(maxPreviewWidth, newHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                ownsPreviewBitmap = true;
            }
            else
            {
                // Source is already at or below preview size - reuse it directly.
                // ProcessBitmapAsync makes its own working copy internally.
                previewBitmap = original;
                ownsPreviewBitmap = false;
            }

            // Scale down the crop region for preview if needed
            var previewPreset = preset;
            if (preset.CropRegion != null && original.Width > maxPreviewWidth)
            {
                var scale = (float)maxPreviewWidth / original.Width;
                previewPreset = ClonePreset(preset);
                previewPreset.CropRegion = new CropRegion
                {
                    X = (int)(preset.CropRegion.X * scale),
                    Y = (int)(preset.CropRegion.Y * scale),
                    Width = (int)(preset.CropRegion.Width * scale),
                    Height = (int)(preset.CropRegion.Height * scale),
                    IsRelative = false
                };

                // Also scale down the resize settings proportionally
                if (preset.ResizeSettings.TargetWidth > 0)
                {
                    previewPreset.ResizeSettings.TargetWidth = (int)(preset.ResizeSettings.TargetWidth * scale);
                    previewPreset.ResizeSettings.TargetHeight = (int)(preset.ResizeSettings.TargetHeight * scale);
                }
            }

            var result = await ProcessBitmapAsync(previewBitmap, previewPreset, ct);
            if (ownsPreviewBitmap) previewBitmap.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating preview");
            return null;
        }
    }

    /// <summary>
    /// Processes a bitmap with the full adjustment pipeline.
    /// </summary>
    private async Task<SKBitmap?> ProcessBitmapAsync(SKBitmap original, ImageEditPreset preset, CancellationToken ct)
    {
        var currentBitmap = original.Copy();

        try
        {
            // 1. Apply crop if specified
            if (preset.CropRegion != null)
            {
                var cropRect = preset.CropRegion.IsRelative
                    ? new SKRectI(
                        (int)(preset.CropRegion.X * currentBitmap.Width),
                        (int)(preset.CropRegion.Y * currentBitmap.Height),
                        (int)((preset.CropRegion.X + preset.CropRegion.Width) * currentBitmap.Width),
                        (int)((preset.CropRegion.Y + preset.CropRegion.Height) * currentBitmap.Height))
                    : new SKRectI(
                        Math.Max(0, preset.CropRegion.X),
                        Math.Max(0, preset.CropRegion.Y),
                        Math.Min(currentBitmap.Width, preset.CropRegion.X + preset.CropRegion.Width),
                        Math.Min(currentBitmap.Height, preset.CropRegion.Y + preset.CropRegion.Height));

                if (cropRect.Width > 0 && cropRect.Height > 0)
                {
                    var cropped = new SKBitmap(cropRect.Width, cropRect.Height);
                    original.ExtractSubset(cropped, cropRect);
                    currentBitmap.Dispose();
                    currentBitmap = cropped;
                }
            }

            // 2. Apply resize if specified
            if (preset.DevicePreset != DevicePreset.Original &&
                preset.DevicePreset != DevicePreset.None)
            {
                var (targetWidth, targetHeight) = preset.DevicePreset == DevicePreset.Custom
                    ? (preset.ResizeSettings.TargetWidth, preset.ResizeSettings.TargetHeight)
                    : ResizeSettings.GetPresetDimensions(preset.DevicePreset, preset.ResizeSettings.IsPortrait);

                if (targetWidth > 0 && targetHeight > 0)
                {
                    currentBitmap = ApplyResize(currentBitmap, targetWidth, targetHeight, preset.ResizeSettings.Mode, preset.ResizeSettings);
                }
            }

            // 3. Apply adjustments
            var adj = preset.Adjustments;
            if (adj.HasAdjustments)
            {
                currentBitmap = ApplyAdjustments(currentBitmap, adj);
            }

            ct.ThrowIfCancellationRequested();
            return currentBitmap;
        }
        catch (Exception)
        {
            currentBitmap?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies resize to bitmap.
    /// </summary>
    private SKBitmap ApplyResize(SKBitmap source, int targetWidth, int targetHeight, ResizeMode mode, ResizeSettings? settings = null)
    {
        SKBitmap result;
        settings ??= new ResizeSettings();

        switch (mode)
        {
            case ResizeMode.Fit:
                // Letterbox: scale to fit within target, maintaining aspect ratio
                var fitScale = Math.Min((float)targetWidth / source.Width, (float)targetHeight / source.Height);
                var fitWidth = (int)(source.Width * fitScale);
                var fitHeight = (int)(source.Height * fitScale);
                result = source.Resize(new SKImageInfo(fitWidth, fitHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                break;

            case ResizeMode.Fill:
                // Crop to fill: scale to cover target, then crop. Honour the
                // user-supplied CropOffsetX/Y (normalized -1..1) so they can
                // pan the visible region away from center.
                var fillScale = Math.Max((float)targetWidth / source.Width, (float)targetHeight / source.Height);
                var scaledWidth = (int)(source.Width * fillScale);
                var scaledHeight = (int)(source.Height * fillScale);
                using (var scaled = source.Resize(new SKImageInfo(scaledWidth, scaledHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                {
                    var maxOffsetX = Math.Max(0, scaledWidth - targetWidth);
                    var maxOffsetY = Math.Max(0, scaledHeight - targetHeight);
                    var offX = Math.Clamp(settings.CropOffsetX, -1.0, 1.0);
                    var offY = Math.Clamp(settings.CropOffsetY, -1.0, 1.0);
                    var cropX = (int)Math.Round(maxOffsetX * (0.5 + offX / 2.0));
                    var cropY = (int)Math.Round(maxOffsetY * (0.5 + offY / 2.0));
                    cropX = Math.Clamp(cropX, 0, maxOffsetX);
                    cropY = Math.Clamp(cropY, 0, maxOffsetY);
                    result = new SKBitmap(targetWidth, targetHeight);
                    using (var canvas = new SKCanvas(result))
                    {
                        canvas.DrawBitmap(scaled, -cropX, -cropY);
                    }
                }
                break;

            case ResizeMode.Stretch:
                // Distort to fill exactly
                result = source.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                break;

            default:
                result = source.Copy();
                break;
        }

        return result;
    }

    /// <summary>
    /// Applies all image adjustments. Color-matrix filters (brightness, contrast,
    /// saturation, hue rotation, temperature/tint, highlights/shadows) are composed
    /// into a SINGLE SKColorFilter and applied in one bitmap pass for performance.
    /// Spatial filters (sharpness, blur, vignette, overlay) run sequentially.
    /// </summary>
    private SKBitmap ApplyAdjustments(SKBitmap source, ImageAdjustments adj)
    {
        // Stage 1: Compose all color-matrix filters into one paint pass.
        SKColorFilter? composed = null;

        if (adj.Brightness != 0 || adj.Contrast != 0)
            composed = ComposeFilters(composed, BuildBrightnessContrastFilter(adj.Brightness, adj.Contrast));
        if (adj.Saturation != 100)
            composed = ComposeFilters(composed, BuildSaturationFilter(adj.Saturation));
        if (adj.HueRotation != 0)
            composed = ComposeFilters(composed, BuildHueFilter(adj.HueRotation));
        if (adj.Temperature != 0 || adj.Tint != 0)
            composed = ComposeFilters(composed, BuildTemperatureTintFilter(adj.Temperature, adj.Tint));
        if (adj.Highlights != 0 || adj.Shadows != 0)
            composed = ComposeFilters(composed, BuildHighlightsShadowsFilter(adj.Highlights, adj.Shadows));

        SKBitmap result;
        if (composed != null)
        {
            result = new SKBitmap(source.Info);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { ColorFilter = composed })
            {
                canvas.DrawBitmap(source, 0, 0, paint);
            }
            composed.Dispose();
        }
        else
        {
            result = source.Copy();
        }

        // Stage 2: Sequential spatial filters.
        if (adj.Sharpness > 0)
        {
            var next = ApplySharpness(result, adj.Sharpness);
            result.Dispose();
            result = next;
        }
        if (adj.BlurRadius > 0)
        {
            var next = ApplyBlur(result, adj.BlurRadius);
            result.Dispose();
            result = next;
        }
        if (adj.VignetteIntensity > 0)
        {
            var next = ApplyVignette(result, adj.VignetteIntensity, adj.VignetteRadius);
            result.Dispose();
            result = next;
        }
        if (adj.ColorOverlayOpacity > 0)
        {
            var hex = string.IsNullOrEmpty(adj.ColorOverlayHex) ? "#000000" : adj.ColorOverlayHex;
            var next = ApplyColorOverlay(result, hex, adj.ColorOverlayOpacity);
            result.Dispose();
            result = next;
        }
        if (adj.Opacity < 100)
        {
            var next = ApplyOpacity(result, adj.Opacity);
            result.Dispose();
            result = next;
        }

        return result;
    }

    private static SKColorFilter ComposeFilters(SKColorFilter? outer, SKColorFilter inner)
    {
        if (outer == null) return inner;
        var composed = SKColorFilter.CreateCompose(outer, inner);
        outer.Dispose();
        inner.Dispose();
        return composed;
    }

    private static SKColorFilter BuildBrightnessContrastFilter(int brightness, int contrast)
    {
        var brightnessOffset = brightness / 100f * 0.4f;
        var contrastMultiplier = 1f + (contrast / 100f * 0.5f);
        var contrastOffset = 0.5f * (1f - contrastMultiplier);
        var totalOffset = brightnessOffset + contrastOffset;
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            contrastMultiplier, 0, 0, 0, totalOffset,
            0, contrastMultiplier, 0, 0, totalOffset,
            0, 0, contrastMultiplier, 0, totalOffset,
            0, 0, 0, 1, 0
        });
    }

    private static SKColorFilter BuildSaturationFilter(int saturation)
    {
        var s = saturation / 100f;
        var lumR = 0.3086f; var lumG = 0.6094f; var lumB = 0.0820f;
        var sr = (1 - s) * lumR; var sg = (1 - s) * lumG; var sb = (1 - s) * lumB;
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            sr + s, sg, sb, 0, 0,
            sr, sg + s, sb, 0, 0,
            sr, sg, sb + s, 0, 0,
            0, 0, 0, 1, 0
        });
    }

    private static SKColorFilter BuildHueFilter(int hueDegrees)
    {
        var angle = hueDegrees * Math.PI / 180;
        var cos = (float)Math.Cos(angle);
        var sin = (float)Math.Sin(angle);
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            0.213f + 0.787f * cos - 0.213f * sin, 0.715f - 0.715f * cos - 0.715f * sin, 0.072f - 0.072f * cos + 0.928f * sin, 0, 0,
            0.213f - 0.213f * cos + 0.143f * sin, 0.715f + 0.285f * cos + 0.140f * sin, 0.072f - 0.072f * cos - 0.283f * sin, 0, 0,
            0.213f - 0.213f * cos - 0.787f * sin, 0.715f - 0.715f * cos + 0.715f * sin, 0.072f + 0.928f * cos + 0.072f * sin, 0, 0,
            0, 0, 0, 1, 0
        });
    }

    private static SKColorFilter BuildTemperatureTintFilter(int temperature, int tint)
    {
        var tempR = temperature > 0 ? temperature / 100f * 0.1f : 0;
        var tempB = temperature < 0 ? -temperature / 100f * 0.1f : 0;
        var tintG = tint < 0 ? -tint / 100f * 0.05f : 0;
        var tintM = tint > 0 ? tint / 100f * 0.05f : 0;
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            1 + tempR, 0, 0, 0, 0,
            0, 1 + tintG - tintM, 0, 0, 0,
            0, 0, 1 + tempB, 0, 0,
            0, 0, 0, 1, 0
        });
    }

    private static SKColorFilter BuildHighlightsShadowsFilter(int highlights, int shadows)
    {
        var brightness = (highlights - shadows) * 0.5f;
        var brightnessOffset = brightness / 100f * 0.3f;
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            1, 0, 0, 0, brightnessOffset,
            0, 1, 0, 0, brightnessOffset,
            0, 0, 1, 0, brightnessOffset,
            0, 0, 0, 1, 0
        });
    }

    private SKBitmap ApplyBrightnessContrast(SKBitmap source, int brightness, int contrast)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint();

        // SkiaSharp 3.x: color matrix translation column is in 0-1 range (1.0 = +255)
        // Brightness: gentle additive shift, ±0.4 max at ±100 (≈ ±100/255)
        var brightnessOffset = brightness / 100f * 0.4f;

        // Contrast: multiplier pivots around 0.5 (middle gray in 0-1 range)
        // result = (pixel - 0.5) * multiplier + 0.5 + brightness
        var contrastMultiplier = 1f + (contrast / 100f * 0.5f); // up to 1.5x at +100
        var contrastOffset = 0.5f * (1f - contrastMultiplier);  // pivot adjustment in 0-1 range

        var totalOffset = brightnessOffset + contrastOffset;

        var matrix = SKColorFilter.CreateColorMatrix(new float[]
        {
            contrastMultiplier, 0, 0, 0, totalOffset,
            0, contrastMultiplier, 0, 0, totalOffset,
            0, 0, contrastMultiplier, 0, totalOffset,
            0, 0, 0, 1, 0
        });

        paint.ColorFilter = matrix;
        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    private SKBitmap ApplySaturation(SKBitmap source, int saturation)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint();

        var saturationFactor = saturation / 100f;
        var lumR = 0.3086f;
        var lumG = 0.6094f;
        var lumB = 0.0820f;
        var s = saturationFactor;
        var sr = (1 - s) * lumR;
        var sg = (1 - s) * lumG;
        var sb = (1 - s) * lumB;

        var matrix = SKColorFilter.CreateColorMatrix(new float[]
        {
            sr + s, sg, sb, 0, 0,
            sr, sg + s, sb, 0, 0,
            sr, sg, sb + s, 0, 0,
            0, 0, 0, 1, 0
        });

        paint.ColorFilter = matrix;
        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    private SKBitmap ApplyHueRotation(SKBitmap source, int hueDegrees)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint();

        // Use a simplified hue rotation via color matrix
        var angle = hueDegrees * Math.PI / 180;
        var cos = (float)Math.Cos(angle);
        var sin = (float)Math.Sin(angle);

        var matrix = SKColorFilter.CreateColorMatrix(new float[]
        {
            0.213f + 0.787f * cos - 0.213f * sin, 0.715f - 0.715f * cos - 0.715f * sin, 0.072f - 0.072f * cos + 0.928f * sin, 0, 0,
            0.213f - 0.213f * cos + 0.143f * sin, 0.715f + 0.285f * cos + 0.140f * sin, 0.072f - 0.072f * cos - 0.283f * sin, 0, 0,
            0.213f - 0.213f * cos - 0.787f * sin, 0.715f - 0.715f * cos + 0.715f * sin, 0.072f + 0.928f * cos + 0.072f * sin, 0, 0,
            0, 0, 0, 1, 0
        });

        paint.ColorFilter = matrix;
        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    private SKBitmap ApplyTemperatureTint(SKBitmap source, int temperature, int tint)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint();

        var tempR = temperature > 0 ? temperature / 100f * 0.1f : 0;
        var tempB = temperature < 0 ? -temperature / 100f * 0.1f : 0;
        var tintG = tint < 0 ? -tint / 100f * 0.05f : 0;
        var tintM = tint > 0 ? tint / 100f * 0.05f : 0;

        var matrix = SKColorFilter.CreateColorMatrix(new float[]
        {
            1 + tempR, 0, 0, 0, 0,
            0, 1 + tintG - tintM, 0, 0, 0,
            0, 0, 1 + tempB, 0, 0,
            0, 0, 0, 1, 0
        });

        paint.ColorFilter = matrix;
        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    private SKBitmap ApplyHighlightsShadows(SKBitmap source, int highlights, int shadows)
    {
        // Simplified implementation: apply brightness with different curves
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint();

        // For highlights: boost bright pixels more
        // For shadows: boost dark pixels more
        var highlightFactor = highlights / 100f * 0.5f;
        var shadowFactor = shadows / 100f * 0.5f;

        // Simple approach: adjust brightness based on pixel value
        // Full implementation would require per-pixel processing
        var brightness = (int)((highlights - shadows) * 0.5f);
        if (brightness != 0)
        {
            // SkiaSharp 3.x: translation column is 0-1 range
            var brightnessOffset = brightness / 100f * 0.3f;
            var matrix = SKColorFilter.CreateColorMatrix(new float[]
            {
                1, 0, 0, 0, brightnessOffset,
                0, 1, 0, 0, brightnessOffset,
                0, 0, 1, 0, brightnessOffset,
                0, 0, 0, 1, 0
            });
            paint.ColorFilter = matrix;
        }

        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }

    private SKBitmap ApplySharpness(SKBitmap source, int sharpness)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);

        // Apply sharpening using a 3x3 convolution kernel (unsharp mask approximation)
        // Strength scales with sharpness slider (0-100)
        var strength = sharpness / 100f;
        // Center value increases with strength; surrounding values are negative
        var center = 1f + 4f * strength;
        var side = -strength;

        using var sharpenFilter = SKImageFilter.CreateMatrixConvolution(
            kernelSize: new SKSizeI(3, 3),
            kernel: new float[]
            {
                0,    side, 0,
                side, center, side,
                0,    side, 0
            },
            gain: 1f,
            bias: 0f,
            kernelOffset: new SKPointI(1, 1),
            tileMode: SKShaderTileMode.Clamp,
            convolveAlpha: false);

        using var paint = new SKPaint { ImageFilter = sharpenFilter };
        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    private SKBitmap ApplyBlur(SKBitmap source, int radius)
    {
        // For radius >= 4 use the standard downscale-blur-upscale trick:
        // halving the image and halving the radius gives near-identical
        // visual output but ~4x fewer pixels processed by the blur kernel.
        if (radius >= 4 && source.Width >= 64 && source.Height >= 64)
        {
            var halfInfo = new SKImageInfo(source.Width / 2, source.Height / 2,
                                            source.ColorType, source.AlphaType);
            using var half = source.Resize(halfInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (half != null)
            {
                using var blurredHalf = new SKBitmap(halfInfo);
                using (var hc = new SKCanvas(blurredHalf))
                using (var hp = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(radius / 2f, radius / 2f) })
                {
                    hc.DrawBitmap(half, 0, 0, hp);
                }

                var result = new SKBitmap(source.Info);
                using (var canvas = new SKCanvas(result))
#pragma warning disable CS0618
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
#pragma warning restore CS0618
                {
                    canvas.DrawBitmap(blurredHalf,
                        new SKRect(0, 0, blurredHalf.Width, blurredHalf.Height),
                        new SKRect(0, 0, source.Width, source.Height),
                        paint);
                }
                return result;
            }
        }

        // Small-radius path: blur directly.
        var direct = new SKBitmap(source.Info);
        using (var canvas = new SKCanvas(direct))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(radius, radius) })
        {
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        return direct;
    }

    private SKBitmap ApplyVignette(SKBitmap source, int intensity, int radius)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);

        // Draw original
        canvas.DrawBitmap(source, 0, 0);

        // Create vignette overlay
        var centerX = source.Width / 2f;
        var centerY = source.Height / 2f;
        var maxRadius = Math.Max(centerX, centerY) * (radius / 100f);

        using var vignettePaint = new SKPaint();
        vignettePaint.IsAntialias = true;
        vignettePaint.BlendMode = SKBlendMode.SrcOver;

        // Create radial gradient for vignette
        var colors = new[] { SKColors.Transparent, new SKColor(0, 0, 0, (byte)(intensity * 2.55)) };
        var positions = new[] { radius / 100f, 1.0f };

        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(centerX, centerY),
            maxRadius,
            colors,
            positions,
            SKShaderTileMode.Clamp);

        vignettePaint.Shader = shader;
        canvas.DrawRect(0, 0, source.Width, source.Height, vignettePaint);

        return result;
    }

    private static SKBitmap ApplyOpacity(SKBitmap source, int opacity)
    {
        // Output must be RGBA with premultiplied alpha so transparency is preserved on export.
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var result = new SKBitmap(info);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 2.55), 0, 255);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
        canvas.DrawBitmap(source, 0, 0, paint);
        return result;
    }

    private SKBitmap ApplyColorOverlay(SKBitmap source, string hexColor, int opacity)
    {
        var result = new SKBitmap(source.Info);
        using var canvas = new SKCanvas(result);

        // Draw original
        canvas.DrawBitmap(source, 0, 0);

        // Parse hex color
        var color = SKColor.Parse(hexColor);
        var overlayColor = new SKColor(color.Red, color.Green, color.Blue, (byte)(opacity * 2.55));

        // Apply color overlay with blend mode
        using var overlayPaint = new SKPaint();
        overlayPaint.Color = overlayColor;
        overlayPaint.BlendMode = SKBlendMode.SrcOver;
        canvas.DrawRect(0, 0, source.Width, source.Height, overlayPaint);

        return result;
    }

    /// <summary>
    /// Saves a bitmap to file with the specified format.
    /// </summary>
    private async Task SaveBitmapAsync(SKBitmap bitmap, string filePath, ImageEditPreset preset, CancellationToken ct)
    {
        var format = preset.ResizeSettings.OutputFormat;
        if (format == ResizeOutputFormat.KeepOriginal)
        {
            format = GetFormatFromExtension(filePath);
        }

        using var image = SKImage.FromBitmap(bitmap);
        SKData data;

        switch (format)
        {
            case ResizeOutputFormat.Jpeg:
                data = image.Encode(SKEncodedImageFormat.Jpeg, preset.ResizeSettings.JpegQuality);
                break;
            case ResizeOutputFormat.Png:
                data = image.Encode(SKEncodedImageFormat.Png, 100);
                break;
            case ResizeOutputFormat.Webp:
                data = image.Encode(SKEncodedImageFormat.Webp, preset.ResizeSettings.JpegQuality);
                break;
            default:
                data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                break;
        }

        await using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);
        await stream.FlushAsync(ct);
    }

    private string GetOutputExtension(ImageEditPreset preset)
    {
        var format = preset.ResizeSettings.OutputFormat;
        if (format == ResizeOutputFormat.KeepOriginal)
        {
            return ".jpg"; // Default to jpg if keeping original
        }

        return format switch
        {
            ResizeOutputFormat.Jpeg => ".jpg",
            ResizeOutputFormat.Png => ".png",
            ResizeOutputFormat.Webp => ".webp",
            _ => ".jpg"
        };
    }

    private ResizeOutputFormat GetFormatFromExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => ResizeOutputFormat.Png,
            ".webp" => ResizeOutputFormat.Webp,
            _ => ResizeOutputFormat.Jpeg
        };
    }

    /// <summary>
    /// Clones a preset for preview scaling.
    /// </summary>
    private ImageEditPreset ClonePreset(ImageEditPreset original)
    {
        return new ImageEditPreset
        {
            Id = original.Id,
            Name = original.Name,
            IsBuiltIn = original.IsBuiltIn,
            DevicePreset = original.DevicePreset,
            ResizeSettings = new ResizeSettings
            {
                TargetWidth = original.ResizeSettings.TargetWidth,
                TargetHeight = original.ResizeSettings.TargetHeight,
                Mode = original.ResizeSettings.Mode,
                OutputFormat = original.ResizeSettings.OutputFormat,
                JpegQuality = original.ResizeSettings.JpegQuality,
                LockAspectRatio = original.ResizeSettings.LockAspectRatio,
                AspectRatio = original.ResizeSettings.AspectRatio,
                IsPortrait = original.ResizeSettings.IsPortrait,
                CropOffsetX = original.ResizeSettings.CropOffsetX,
                CropOffsetY = original.ResizeSettings.CropOffsetY
            },
            Adjustments = new ImageAdjustments
            {
                Brightness = original.Adjustments.Brightness,
                Contrast = original.Adjustments.Contrast,
                Saturation = original.Adjustments.Saturation,
                HueRotation = original.Adjustments.HueRotation,
                Temperature = original.Adjustments.Temperature,
                Tint = original.Adjustments.Tint,
                Highlights = original.Adjustments.Highlights,
                Shadows = original.Adjustments.Shadows,
                Sharpness = original.Adjustments.Sharpness,
                BlurRadius = original.Adjustments.BlurRadius,
                VignetteIntensity = original.Adjustments.VignetteIntensity,
                VignetteRadius = original.Adjustments.VignetteRadius,
                ColorOverlayHex = original.Adjustments.ColorOverlayHex,
                ColorOverlayOpacity = original.Adjustments.ColorOverlayOpacity
            },
            SaveAsNew = original.SaveAsNew,
            ApplyToAllPages = original.ApplyToAllPages,
            CustomOutputFolder = original.CustomOutputFolder
        };
    }

    /// <summary>
    /// Loads an image from file for preview.
    /// </summary>
    public async Task<SKBitmap?> LoadImageAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            await using var stream = File.OpenRead(filePath);
            return SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading image: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Loads an image from URL for preview.
    /// </summary>
    public async Task<SKBitmap?> LoadImageFromUrlAsync(string url, HttpClient httpClient, CancellationToken ct = default)
    {
        try
        {
            var data = await httpClient.GetByteArrayAsync(url, ct);
            return SKBitmap.Decode(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading image from URL: {Url}", url);
            return null;
        }
    }
}
