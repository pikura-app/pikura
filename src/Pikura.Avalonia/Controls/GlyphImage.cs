using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Pikura.Avalonia.Controls;

/// <summary>A glyph rendered once into a bitmap. Drawing text under a per-frame rotation and scale
/// re-rasterizes color emoji every frame; drawing a cached bitmap is a cheap textured quad.</summary>
internal sealed class GlyphImage : IDisposable
{
    /// <summary>Pixels per logical unit, so particles scaled up to about 2x stay sharp.</summary>
    private const double Density = 2;

    private readonly RenderTargetBitmap _bitmap;

    /// <summary>Margin around the text box: color emoji ink overflows the measured line box.</summary>
    private readonly double _pad;

    public double Width { get; }
    public double Height { get; }

    public GlyphImage(FormattedText text)
    {
        Width = Math.Max(1, text.WidthIncludingTrailingWhitespace);
        Height = Math.Max(1, text.Height);
        _pad = Math.Ceiling(Math.Max(Width, Height) * 0.5);
        _bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling((Width + 2 * _pad) * Density), (int)Math.Ceiling((Height + 2 * _pad) * Density)),
            new Vector(96, 96));
        using var ctx = _bitmap.CreateDrawingContext();
        using (ctx.PushTransform(Matrix.CreateScale(Density, Density)))
            ctx.DrawText(text, new Point(_pad, _pad));
    }

    public void Draw(DrawingContext context) => context.DrawImage(_bitmap, new Rect(-_pad, -_pad, Width + 2 * _pad, Height + 2 * _pad));

    public void Dispose() => _bitmap.Dispose();
}
