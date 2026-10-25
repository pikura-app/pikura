using SkiaSharp;
using System.Buffers;
using System.Security.Cryptography;

namespace Pikura.Core.Services.Library;

/// <summary>
/// Single-pass file probe: reads the file once and derives sha256, dimensions
/// and the 64-bit pHash from the same bytes. Small files (≤64 MB) are read into
/// a pooled buffer; larger files stream through IncrementalHash and decode
/// separately (they're rare — usually big videos which don't get a pHash).
/// Scaled decode (nearest supported size ≥32 px) keeps big JPEGs cheap.
/// </summary>
public static class ImageProbe
{
    /// <summary>phash algorithm version stamped on rows hashed by this path.</summary>
    public const int CurrentPhashVersion = 2;
    private const long MaxPooledSize = 64 * 1024 * 1024;

    public readonly record struct Result(string Sha256, long? Phash, int PhashVersion, int Width, int Height);

    /// <summary>Full probe: sha256 + dims + pHash (null for videos/undecodable).</summary>
    public static Result IndexFile(string path, bool wantPhash = true)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length == 0)
            return new Result("", null, 0, 0, 0);

        var isVideo = PerceptualHash.IsVideoExtension(Path.GetExtension(path));
        var needPixels = !isVideo && wantPhash;
        var needDims = !isVideo; // videos report no dims, matching prior behavior

        if (fi.Length <= MaxPooledSize)
        {
            var rent = ArrayPool<byte>.Shared.Rent((int)fi.Length);
            try
            {
                var bytes = rent.AsMemory(0, (int)fi.Length);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var span = bytes.Span;
                    var read = 0;
                    while (read < span.Length)
                    {
                        var n = fs.Read(span[read..]);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != span.Length)
                        bytes = bytes[..read];
                }
                var sha = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
                var (ph, w, h) = DecodeForProbe(bytes.Span, needPixels, needDims);
                return new Result(sha, ph, ph.HasValue ? CurrentPhashVersion : 0, w, h);
            }
            finally { ArrayPool<byte>.Shared.Return(rent); }
        }

        // Large file: stream the hash, decode separately.
        string hash;
        using (var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                inc.AppendData(buf, 0, n);
            hash = Convert.ToHexString(inc.GetHashAndReset()).ToLowerInvariant();
        }
        using var codec = SKCodec.Create(path);
        if (codec == null) return new Result(hash, null, 0, 0, 0);
        if (!needDims) return new Result(hash, null, 0, 0, 0);
        if (!needPixels) return new Result(hash, null, 0, codec.Info.Width, codec.Info.Height);
        var ph2 = PerceptualHash.Compute(codec);
        return new Result(hash, ph2, ph2.HasValue ? CurrentPhashVersion : 0, codec.Info.Width, codec.Info.Height);
    }

    private static (long? Ph, int W, int H) DecodeForProbe(ReadOnlySpan<byte> bytes, bool needPixels, bool needDims)
    {
        if (!needDims) return (null, 0, 0);
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return (null, 0, 0);
            var w = codec.Info.Width; var h = codec.Info.Height;
            if (!needPixels) return (null, w, h);
            return (PerceptualHash.Compute(codec), w, h);
        }
        catch { return (null, 0, 0); }
    }
}
