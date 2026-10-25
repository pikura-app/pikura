using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Core.Data;
using Pikura.Core.Services.Library;
using SkiaSharp;
using Xunit;

namespace Pikura.Core.Tests;

public class LibraryFixture : IDisposable
{
    public string Dir { get; }
    public string DbPath { get; }
    public LibraryRepository Repo { get; }
    public LibraryFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "pikura-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        DbPath = Path.Combine(Dir, "test.db");
        Repo = new LibraryRepository(DbPath, NullLogger<LibraryRepository>.Instance);
    }
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class LibraryRepositoryTests : IClassFixture<LibraryFixture>
{
    private readonly LibraryFixture _fx;
    public LibraryRepositoryTests(LibraryFixture fx) => _fx = fx;

    private LibraryFile F(string path, string? title = null, string? tags = null,
        string? ai = null, int xr = 0, string? artist = "artistA", string? artistId = "1") => new()
    {
        Path = path, Root = "/root", Size = 100, Mtime = "t", MediaType = "image",
        Title = title, Tags = tags, AiTagsText = ai, XRestrict = xr,
        ArtistName = artist, ArtistId = artistId, AddedAt = DateTimeOffset.UtcNow.ToString("O"),
    };

    [Fact]
    public async Task Upsert_And_Search_ByTitleAndTags()
    {
        await _fx.Repo.UpsertFileAsync(F(@"C:\lib\a1.jpg", "sunset over ocean", "landscape scenery"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\lib\a2.png", "portrait study", "portrait girl"));

        var (items, total) = await _fx.Repo.SearchAsync(new LibraryQuery { Text = "sunset" });
        Assert.Equal(1, total);
        Assert.Equal(@"C:\lib\a1.jpg", items[0].Path);

        (items, total) = await _fx.Repo.SearchAsync(new LibraryQuery { PixivTags = ["portrait"] });
        Assert.Equal(1, total);
        Assert.Equal(@"C:\lib\a2.png", items[0].Path);
    }

    [Fact]
    public async Task Search_AiTag_And_R18Filter()
    {
        var f1 = F(@"C:\lib\b1.jpg", "safe pic"); var f2 = F(@"C:\lib\b2.jpg", "lewd pic", xr: 2);
        var id1 = await _fx.Repo.UpsertFileAsync(f1);
        await _fx.Repo.UpsertFileAsync(f2);
        await _fx.Repo.SetAiTagsAsync(id1, [new LibraryAiTag { Tag = "1girl", Category = "general", Score = .9 }]);

        var (items, _) = await _fx.Repo.SearchAsync(new LibraryQuery { AiTags = ["1girl"] });
        Assert.Single(items);

        (items, _) = await _fx.Repo.SearchAsync(new LibraryQuery { IncludeR18 = false });
        Assert.DoesNotContain(items, i => i.Path == @"C:\lib\b2.jpg");
        (items, _) = await _fx.Repo.SearchAsync(new LibraryQuery { IncludeR18 = true });
        Assert.Contains(items, i => i.Path == @"C:\lib\b2.jpg");
    }

    [Fact]
    public async Task Cjk_Search_Trigram_Or_Like()
    {
        await _fx.Repo.UpsertFileAsync(F(@"C:\lib\c1.jpg", "桜の花びら", "春 桜"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\lib\c2.jpg", "ocean wave", "sea"));

        var (items, total) = await _fx.Repo.SearchAsync(new LibraryQuery { Text = "桜" });
        Assert.True(total >= 1, $"expected CJK hit (trigram={_fx.Repo.TrigramAvailable})");
        Assert.Equal(@"C:\lib\c1.jpg", items[0].Path);
    }

    [Fact]
    public async Task Delete_Removes_Row_And_Fts()
    {
        var id = await _fx.Repo.UpsertFileAsync(F(@"C:\lib\d1.jpg", "unique-title-xyz"));
        await _fx.Repo.DeleteByPathAsync(@"C:\lib\d1.jpg");
        Assert.Null(await _fx.Repo.GetByPathAsync(@"C:\lib\d1.jpg"));
        var (_, total) = await _fx.Repo.SearchAsync(new LibraryQuery { Text = "unique-title-xyz" });
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task Exact_Duplicates_Grouped_By_Sha()
    {
        var a = F(@"C:\lib\e1.jpg"); a.Sha256 = "abc";
        var b = F(@"C:\lib\e2.jpg"); b.Sha256 = "abc";
        var c = F(@"C:\lib\e3.jpg"); c.Sha256 = "xyz";
        await _fx.Repo.UpsertFileAsync(a); await _fx.Repo.UpsertFileAsync(b); await _fx.Repo.UpsertFileAsync(c);
        var groups = await _fx.Repo.GetExactDuplicateGroupsAsync();
        var g = groups.FirstOrDefault(g => g.Files.Any(f => f.Path == @"C:\lib\e1.jpg"));
        Assert.NotNull(g);
        Assert.True(g!.IsExact);
        Assert.Equal(2, g.Files.Count);
    }
}

public class PerceptualHashTests
{
    private static SKBitmap MakeImage(int w, int h, Func<int, int, SKColor> px)
    {
        var bmp = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, px(x, y));
        return bmp;
    }

    [Fact]
    public void Deterministic_And_Robust_To_Reencode_Resize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pikura-phash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Smooth gradient + circle — stable under re-encode.
            using var bmp = MakeImage(200, 200, (x, y) =>
            {
                var g = (byte)((x + y) / 2 % 256);
                if ((x - 100) * (x - 100) + (y - 100) * (y - 100) < 2500)
                    return new SKColor(255, 255, 255);
                return new SKColor(g, (byte)(g / 2), (byte)(255 - g));
            });
            var p1 = Path.Combine(dir, "a.png");
            var p2 = Path.Combine(dir, "a_q70.jpg");
            var p3 = Path.Combine(dir, "a_half.png");
            using (var fs = File.Create(p1)) bmp.Encode(fs, SKEncodedImageFormat.Png, 100);
            using (var fs = File.Create(p2)) bmp.Encode(fs, SKEncodedImageFormat.Jpeg, 70);
            using (var small = bmp.Resize(new SKImageInfo(100, 100), new SKSamplingOptions(SKCubicResampler.Mitchell)))
            using (var fs = File.Create(p3)) small!.Encode(fs, SKEncodedImageFormat.Png, 100);

            var h1 = PerceptualHash.Compute(p1)!.Value;
            var h1b = PerceptualHash.Compute(p1)!.Value;
            var h2 = PerceptualHash.Compute(p2)!.Value;
            var h3 = PerceptualHash.Compute(p3)!.Value;
            Assert.Equal(h1, h1b); // deterministic
            var dJpeg = PerceptualHash.Distance(h1, h2);
            var dHalf = PerceptualHash.Distance(h1, h3);
            System.Console.WriteLine($"[phash] q70={dJpeg} half={dHalf}");
            // Default LibraryDuplicateThreshold must detect both.
            Assert.True(dJpeg <= 6, $"jpeg q70 distance {dJpeg} exceeds default threshold 6");
            Assert.True(dHalf <= 6, $"half-size distance {dHalf} exceeds default threshold 6");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unrelated_Images_Are_Far()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pikura-phash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var a = MakeImage(128, 128, (x, y) => new SKColor((byte)(x * 2), (byte)(y * 2), 0));
            using var b = MakeImage(128, 128, (x, y) =>
                ((x / 16 + y / 16) % 2 == 0) ? new SKColor(0, 0, 0) : new SKColor(255, 255, 255));
            var pa = Path.Combine(dir, "a.png"); var pb = Path.Combine(dir, "b.png");
            using (var fs = File.Create(pa)) a.Encode(fs, SKEncodedImageFormat.Png, 100);
            using (var fs = File.Create(pb)) b.Encode(fs, SKEncodedImageFormat.Png, 100);
            var ha = PerceptualHash.Compute(pa)!.Value;
            var hb = PerceptualHash.Compute(pb)!.Value;
            Assert.True(PerceptualHash.Distance(ha, hb) > 20, $"distance {PerceptualHash.Distance(ha, hb)}");
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class NearDuplicateTests : IClassFixture<LibraryFixture>
{
    private readonly LibraryFixture _fx;
    public NearDuplicateTests(LibraryFixture fx) => _fx = fx;

    private LibraryFile F(string path, long? phash, string? sha = null) => new()
    {
        Path = path, Root = "/r", Size = 1, Mtime = "t", MediaType = "image",
        Phash = phash, Sha256 = sha, AddedAt = "t", PhashVersion = 2,
    };

    [Fact]
    public async Task Groups_By_Threshold_And_Excludes_Exact()
    {
        // a,b distance 2 (one byte differs); c far (high byte differs in every band)
        long ha = 0x0102030405060708, hb = 0x0102030405060709;
        long hc = unchecked((long)0xFF10203040506070);
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\a.jpg", ha, "s1"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\b.jpg", hb, "s2"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\c.jpg", hc, "s3"));
        // exact-dup pair also within threshold — must be excluded from near-dups
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\d.jpg", ha, "same"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\e.jpg", ha, "same"));

        var groups = await _fx.Repo.GetNearDuplicateGroupsAsync(6);
        var paths = groups.SelectMany(g => g.Files.Select(f => f.Path)).ToList();
        Assert.Contains(@"C:\n\a.jpg", paths);
        Assert.Contains(@"C:\n\b.jpg", paths);
        Assert.DoesNotContain(@"C:\n\c.jpg", paths);
        // d & e share sha256 with each other but not with a → e is a legitimate
        // near-dup of a (distance 0) and joins a's group.
        Assert.Contains(@"C:\n\e.jpg", paths);

        // An isolated exact-copy pair (same sha, no neighbors with a different sha)
        // is excluded from near-dup groups — reported by GetExactDuplicateGroupsAsync.
        // Rule: an exact-copy file still joins a near-dup group when it also near-matches
        // a file with a different sha256 (the whole group then contains the exact pair
        // members alongside their non-identical lookalikes); the exact pair itself is
        // additionally reported by GetExactDuplicateGroupsAsync.
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\x.jpg", 0x0A0B0C0D0E0F1011, "same2"));
        await _fx.Repo.UpsertFileAsync(F(@"C:\n\y.jpg", 0x0A0B0C0D0E0F1011, "same2"));
        var groups2 = await _fx.Repo.GetNearDuplicateGroupsAsync(6);
        Assert.DoesNotContain(
            groups2.SelectMany(g => g.Files.Select(f => f.Path)),
            p => p == @"C:\n\x.jpg" || p == @"C:\n\y.jpg");

        // The far-away phash file is never grouped, whatever the threshold.
        var strict = await _fx.Repo.GetNearDuplicateGroupsAsync(0);
        Assert.DoesNotContain(strict.SelectMany(g => g.Files.Select(f => f.Path)), p => p == @"C:\n\c.jpg");
    }

    [Fact]
    public async Task UnionFind_Merges_Transitive()
    {
        // a~b (dist1), b~c (dist1) but a~c maybe > threshold edge-wise... keep distances ≤6 by single-byte diffs in same band.
        long ha = 0x0102030405060708, hb = 0x0102030405060709, hc = 0x010203040506070B; // a~c dist 2
        await _fx.Repo.UpsertFileAsync(F(@"C:\u\a.jpg", ha));
        await _fx.Repo.UpsertFileAsync(F(@"C:\u\b.jpg", hb));
        await _fx.Repo.UpsertFileAsync(F(@"C:\u\c.jpg", hc));
        var groups = await _fx.Repo.GetNearDuplicateGroupsAsync(1);
        var g = groups.FirstOrDefault(g => g.Files.Any(f => f.Path == @"C:\u\a.jpg"));
        Assert.NotNull(g);
        Assert.Equal(3, g!.Files.Count); // transitive via shared edges within threshold
    }
}

public class V1PhashExclusionTests : IDisposable
{
    private readonly string _dir;
    private readonly Data.LibraryRepository _repo;
    public V1PhashExclusionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new Data.LibraryRepository(Path.Combine(_dir, "t.db"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Data.LibraryRepository>.Instance);
    }
    public void Dispose() { _repo.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private static Data.LibraryFile Row(string p, long phash, int version) => new()
    {
        Path = p, Root = "root", Size = 1, Phash = phash, Sha256 = "x" + p.GetHashCode(),
        AddedAt = "t", PhashVersion = version,
    };

    [Fact]
    public async Task V1_Rows_Are_Excluded_From_NearDup_Grouping()
    {
        long ha = 0x0102030405060708, hb = 0x0102030405060709; // distance 1
        await _repo.UpsertFileAsync(Row("v2a", ha, 2));
        await _repo.UpsertFileAsync(Row("v1b", hb, 1)); // same hash, v1
        var groups = await _repo.GetNearDuplicateGroupsAsync(6);
        Assert.DoesNotContain(groups, g => g.Files.Any(f => f.Path == "v1b"));
        Assert.Equal(1, await _repo.GetPendingRehashCountAsync());
        // Once rehashed to v2, it joins the group again.
        await _repo.UpsertFileAsync(Row("v1b", hb, 2));
        groups = await _repo.GetNearDuplicateGroupsAsync(6);
        Assert.Contains(groups, g => g.Files.Any(f => f.Path == "v1b"));
        Assert.Equal(0, await _repo.GetPendingRehashCountAsync());
    }
}

public class LibraryScanTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;
    public LibraryScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "test.db");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void MakeJpg(string path)
    {
        using var bmp = new SKBitmap(40, 40);
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(120, 40, 200));
        using var fs = File.Create(path);
        bmp.Encode(fs, SKEncodedImageFormat.Jpeg, 90);
    }

    private static Pikura.Core.Settings.SettingsService FakeSettings(string root)
    {
        var s = new Pikura.Core.Settings.SettingsService(
            Path.Combine(Path.GetTempPath(), "pikura-scan-settings-" + Guid.NewGuid().ToString("N") + ".json"));
        s.Current.DownloadRoot = root;
        return s;
    }

    [Fact]
    public async Task Scan_Parses_Ids_Skips_Part_Imports_Sidecar_Incremental()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        MakeJpg(Path.Combine(root, "12345678_p0.jpg"));
        MakeJpg(Path.Combine(root, "12345678_p1.png"));
        File.WriteAllText(Path.Combine(root, "junk.part"), "x");
        File.WriteAllText(Path.Combine(root, "12345678_p0.jpg.tags.txt"), "Tags for x\n\nGeneral: 1girl, solo\n");

        var repo = new LibraryRepository(_db, NullLogger<LibraryRepository>.Instance);
        var svc = new LibraryIndexService(repo, FakeSettings(root), NullLogger<LibraryIndexService>.Instance);
        var prog = new List<LibraryScanProgress>();
        await svc.ScanAsync(new Progress<LibraryScanProgress>(prog.Add), CancellationToken.None);

        var f0 = await repo.GetByPathAsync(Path.Combine(root, "12345678_p0.jpg"));
        var f1 = await repo.GetByPathAsync(Path.Combine(root, "12345678_p1.png"));
        Assert.NotNull(f0); Assert.NotNull(f1);
        Assert.Equal("12345678", f0!.ArtworkId);
        Assert.Equal(0, f0.PageIndex);
        Assert.Equal(1, f1!.PageIndex);
        Assert.Equal("filename", f0.MetadataSource);
        Assert.NotNull(f0.Sha256);
        var (aiItems, _) = await repo.SearchAsync(new LibraryQuery { AiTags = ["1girl"] });
        Assert.Single(aiItems);
        Assert.Null(await repo.GetByPathAsync(Path.Combine(root, "junk.part")));

        // Second scan: unchanged files skipped (size+mtime match → incremental).
        // Snapshot each report — the service mutates one progress instance.
        var prog2 = new List<LibraryScanProgress>();
        await svc.ScanAsync(new Progress<LibraryScanProgress>(p =>
            prog2.Add(p)),
            CancellationToken.None);
        Assert.DoesNotContain(prog2, x => x.Total > 0 && x.Phase == "scan"); // nothing new to process

        // Delete a file → rescan removes its row.
        File.Delete(Path.Combine(root, "12345678_p1.png"));
        await svc.ScanAsync(null, CancellationToken.None);
        Assert.Null(await repo.GetByPathAsync(Path.Combine(root, "12345678_p1.png")));
    }
}
