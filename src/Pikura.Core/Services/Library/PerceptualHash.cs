using SkiaSharp;
using System.Numerics;

namespace Pikura.Core.Services.Library;

/// <summary>
/// 64-bit DCT perceptual hash: decode → 32×32 grayscale → 2D DCT →
/// top-left 8×8 (excluding DC) → median threshold → 64 bits.
/// Videos are skipped (null). Animated images hash their first frame.
/// </summary>
public static class PerceptualHash
{
    private const int SampleSize = 32;
    private const int HashSide = 8;

    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".avi", ".mov"];

    public static bool IsVideoExtension(string ext) =>
        VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>Computes the pHash of an image file, or null when it can't be hashed (video/undecodable).</summary>
    public static long? Compute(string path)
    {
        var ext = Path.GetExtension(path);
        if (IsVideoExtension(ext)) return null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            using var bmp = new SKBitmap(codec.Info);
            if (codec.GetPixels(bmp.Info, bmp.GetPixels()) != SKCodecResult.Success) return null;
            return Compute(bmp);
        }
        catch { return null; }
    }

    /// <summary>Deterministic Hamming distance between two hashes.</summary>
    public static int Distance(long a, long b) =>
        BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    /// <summary>Computes the pHash of a decoded bitmap.</summary>
    public static long Compute(SKBitmap src)
    {
        using var small = src.Resize(new SKImageInfo(SampleSize, SampleSize, SKColorType.Gray8), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (small == null) return 0;

        var g = new double[SampleSize * SampleSize];
        for (var y = 0; y < SampleSize; y++)
            for (var x = 0; x < SampleSize; x++)
                g[y * SampleSize + x] = small.GetPixel(x, y).Red;

        var dct = Dct2D(g);
        var values = new double[HashSide * HashSide];
        for (var y = 0; y < HashSide; y++)
            for (var x = 0; x < HashSide; x++)
                values[y * HashSide + x] = dct[y * SampleSize + x];

        // Median of everything except the DC term.
        var sorted = values.Skip(1).OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];

        long hash = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] > median) hash |= 1L << i;
        return hash;
    }

    private static double[] Dct2D(double[] input)
    {
        var output = new double[SampleSize * SampleSize];
        var c = new double[SampleSize];
        c[0] = 1.0 / Math.Sqrt(2.0);
        for (var i = 1; i < SampleSize; i++) c[i] = 1.0;

        // Precompute cosines: cos[(2x+1)*u*π/64]
        var cos = new double[SampleSize, SampleSize];
        for (var x = 0; x < SampleSize; x++)
            for (var u = 0; u < SampleSize; u++)
                cos[x, u] = Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * SampleSize));

        var factor = 2.0 / SampleSize;
        for (var v = 0; v < SampleSize; v++)
            for (var u = 0; u < SampleSize; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < SampleSize; y++)
                {
                    var rowBase = y * SampleSize;
                    var cy = cos[y, v];
                    for (var x = 0; x < SampleSize; x++)
                        sum += cos[x, u] * cy * input[rowBase + x];
                }
                output[v * SampleSize + u] = factor * c[u] * c[v] * sum;
            }
        return output;
    }
}
