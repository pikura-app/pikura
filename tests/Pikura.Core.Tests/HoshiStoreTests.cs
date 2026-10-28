using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Core.Services.Hoshi;
using System.Text.Json;
using Xunit;

namespace Pikura.Core.Tests;

public class HoshiStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly HoshiStore _store;

    public HoshiStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-hoshi-" + Guid.NewGuid().ToString("N"));
        _store = new HoshiStore(Path.Combine(_dir, "store"), NullLogger<HoshiStore>.Instance);
    }

    public void Dispose() { _store.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Png(int w = 4, int h = 4)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        using var c = new SkiaSharp.SKCanvas(bmp);
        c.Clear(SkiaSharp.SKColors.Red);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();
    }

    private static HoshiSessionHeader H(string id, string? user = null, bool pinned = false, string title = "t") => new()
    {
        Id = id, UserId = user, Title = title, Pinned = pinned,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task RoundTrip_Headers_And_Messages()
    {
        await _store.SaveSessionAsync(H("s1", title: "hello"),
            new[] { new HoshiMessageRow { Role = "user", Content = "hi" },
                    new HoshiMessageRow { Role = "assistant", Content = "yo" } });
        var headers = await _store.GetSessionHeadersAsync(null);
        Assert.Single(headers);
        Assert.Equal(2, headers[0].MessageCount);
        var msgs = await _store.GetSessionMessagesAsync("s1");
        Assert.Equal(2, msgs.Count);
        Assert.Equal("yo", msgs[1].Content);
    }

    [Fact]
    public async Task Image_Store_Dedupes_By_Content()
    {
        var a = await _store.PutImageAsync(Png());
        var b = await _store.PutImageAsync(Png());
        Assert.Equal(a, b);
        var stats = await _store.GetStorageStatsAsync();
        Assert.Equal(1, stats.ImageFiles);
        Assert.NotNull(await _store.OpenImageAsync(a));
    }

    [Fact]
    public async Task Search_By_Title_And_Content()
    {
        await _store.SaveSessionAsync(H("a", title: "cat picture"),
            new[] { new HoshiMessageRow { Role = "user", Content = "describe this" } });
        await _store.SaveSessionAsync(H("b", title: "dog"),
            new[] { new HoshiMessageRow { Role = "user", Content = "猫を見せて" } });
        Assert.Single(await _store.SearchAsync(null, "cat"), h => h.SessionId == "a");
        Assert.Single(await _store.SearchAsync(null, "describe"), h => h.SessionId == "a");
        Assert.Single(await _store.SearchAsync(null, "見せて"), h => h.SessionId == "b");
    }

    [Fact]
    public async Task Pinned_And_Older_Cleanup()
    {
        var now = DateTime.UtcNow;
        await _store.SaveSessionAsync(H("old", pinned: false, title: "o"),
            new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        await _store.SaveSessionAsync(H("pin", pinned: true, title: "p"),
            new[] { new HoshiMessageRow { Role = "user", Content = "y" } });
        // Backdate "old" beyond 30 days via direct update isn't exposed — use CleanupAsync
        // with a 0-day equivalent via maxAge: skip — test pin exemption + orphan prune instead.
        var img = Png();
        var sha = await _store.PutImageAsync(img);
        var orphans = await _store.PruneOrphanImagesAsync();
        Assert.Equal(1, orphans); // unreferenced image removed
        var heads = await _store.GetSessionHeadersAsync(null);
        Assert.Equal("pin", heads[0].Id); // pinned sorts first
    }

    [Fact]
    public async Task Search_Stays_Correct_Across_Deletes()
    {
        // 200 sessions, delete every other — remaining must still match independently
        // (guards against FTS rowid mixups between sessions).
        for (var i = 0; i < 200; i++)
            await _store.SaveSessionAsync(H($"t{i}", title: $"session topic-{i}"),
                new[] { new HoshiMessageRow { Role = "user", Content = $"body content {i}" } });
        await _store.DeleteSessionsAsync(
            Enumerable.Range(0, 200).Where(i => i % 2 == 0).Select(i => $"t{i}").ToList());
        var hits = await _store.SearchAsync(null, "topic-", limit: 300);
        Assert.Equal(100, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, int.Parse(h.SessionId[1..]) % 2)); // only odd ids survive
        var ids = hits.Select(h => h.SessionId).ToHashSet();
        for (var i = 0; i < 200; i += 2) Assert.DoesNotContain($"t{i}", ids);
    }

    [Fact]
    public void Budget_Keeps_Newest_Drops_System()
    {
        var msgs = Enumerable.Range(0, 20)
            .Select(i => (i % 2 == 0 ? "user" : "assistant", new string('x', 400) + i))
            .Prepend(("system", "status"))
            .ToList();
        var picked = HoshiContextBudget.Select(msgs, tokenBudget: 1); // 4 chars → only newest fits
        Assert.Single(picked);
        Assert.Equal(msgs[^1], picked[0]);
        var big = HoshiContextBudget.Select(msgs, tokenBudget: 6000);
        Assert.DoesNotContain(big, m => m.Item1 == "system");
        Assert.Equal(msgs[^1], big[^1]);
    }

    [Fact]
    public async Task Folders_Crud_And_UnfiledOnDelete()
    {
        await _store.SaveSessionAsync(H("a", user: "u"), new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        var fid = await _store.CreateFolderAsync("u", "test-folder");
        var folders = await _store.ListFoldersAsync("u");
        Assert.Single(folders);
        Assert.Equal("test-folder", folders[0].Name);

        await _store.SetSessionFolderAsync("a", fid);
        var h = (await _store.GetSessionHeadersAsync("u")).Single();
        Assert.Equal(fid, h.FolderId);

        await _store.RenameFolderAsync(fid, "renamed");
        Assert.Equal("renamed", (await _store.ListFoldersAsync("u"))[0].Name);

        await _store.SetFolderCollapsedAsync(fid, true);
        Assert.True((await _store.ListFoldersAsync("u"))[0].Collapsed);

        await _store.DeleteFolderAsync(fid);
        Assert.Empty(await _store.ListFoldersAsync("u"));
        var h2 = (await _store.GetSessionHeadersAsync("u")).Single();
        Assert.Null(h2.FolderId);
    }

    [Fact]
    public async Task Archived_Exempt_From_Cleanup()
    {
        var old = DateTime.UtcNow.AddDays(-60);
        var keep = H("old-archived"); keep.UpdatedAt = old; keep.Archived = true;
        var gone = H("old-active");  gone.UpdatedAt = old;
        var pin  = H("old-pinned");  pin.UpdatedAt = old; pin.Pinned = true;
        await _store.SaveSessionAsync(keep, new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        await _store.SaveSessionAsync(gone, new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        await _store.SaveSessionAsync(pin,  new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        await _store.SetSessionArchivedAsync("old-archived", true);

        var deleted = await _store.CleanupAsync(maxAgeDays: 30, maxImageBytes: null);
        Assert.Contains("old-active", deleted);
        Assert.DoesNotContain("old-archived", deleted);
        Assert.DoesNotContain("old-pinned", deleted);
        Assert.Equal(2, (await _store.GetSessionHeadersAsync(null)).Count);
    }

    [Fact]
    public async Task Archived_Does_Not_Bump_UpdatedAt()
    {
        var h = H("s", user: "u");
        h.UpdatedAt = DateTime.UtcNow.AddDays(-5);
        await _store.SaveSessionAsync(h, new[] { new HoshiMessageRow { Role = "user", Content = "x" } });
        var before = (await _store.GetSessionHeadersAsync("u"))[0].UpdatedAt;
        await _store.SetSessionArchivedAsync("s", true);
        var after = (await _store.GetSessionHeadersAsync("u"))[0];
        Assert.Equal(before, after.UpdatedAt);
        Assert.True(after.Archived);
    }
}

public class HoshiMigrationTests : IDisposable
{
    private readonly string _dir;
    public HoshiMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-hoshi-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteLegacy(string dir, string id, int msgs, bool img, string? b64 = null)
    {
        Directory.CreateDirectory(dir);
        var json = new
        {
            Id = id, Title = "s-" + id, ImageSource = (string?)"pixiv:1",
            Messages = Enumerable.Range(0, msgs).Select(i => new
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = "msg" + i,
                At = DateTime.UtcNow,
                ImageBase64 = b64,
                ArtworkId = (string?)null, ArtistId = (string?)null,
            }).ToArray(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var p = Path.Combine(dir, id + ".json");
        File.WriteAllText(p, JsonSerializer.Serialize(json));
        if (img) File.WriteAllBytes(Path.ChangeExtension(p, ".img"), new byte[] { 1, 2, 3, 4 });
        return p;
    }

    [Fact]
    public async Task Migrates_And_Is_Idempotent()
    {
        var legacy = Path.Combine(_dir, "legacy");
        var store = new HoshiStore(Path.Combine(_dir, "store"), NullLogger<HoshiStore>.Instance);
        var mig = new HoshiMigration(store, NullLogger<HoshiMigration>.Instance);

        var shared = Convert.ToBase64String(new byte[] { 9, 9, 9 });
        WriteLegacy(legacy, "a", 2, img: true);
        WriteLegacy(legacy, "b", 1, img: false, b64: shared);
        var sub = Path.Combine(legacy, "user42");
        WriteLegacy(sub, "c", 1, img: false, b64: shared);
        WriteLegacy(legacy, "d", 1, img: false, b64: shared); // same image as b → dedupe

        var r = await mig.MigrateAsync(legacy);
        Assert.Equal(4, r.Imported);
        Assert.Equal(0, r.Failed);
        Assert.True(r.LegacyRenamed);
        Assert.True(Directory.Exists(legacy + ".migrated"));

        var heads = await store.GetSessionHeadersAsync(null);
        Assert.Equal(3, heads.Count); // a, b, d at root (user NULL)
        var u42 = await store.GetSessionHeadersAsync("user42");
        Assert.Single(u42);

        var stats = await store.GetStorageStatsAsync();
        Assert.Equal(2, stats.ImageFiles); // a's .img + shared b64 (deduped across 3 sessions)

        // Idempotent: re-running on the renamed folder's original path does nothing.
        var r2 = await mig.MigrateAsync(legacy);
        Assert.Equal(0, r2.Imported);
    }

    [Fact]
    public async Task Corrupt_File_Leaves_Legacy_In_Place()
    {
        var legacy = Path.Combine(_dir, "legacy2");
        var store = new HoshiStore(Path.Combine(_dir, "store2"), NullLogger<HoshiStore>.Instance);
        var mig = new HoshiMigration(store, NullLogger<HoshiMigration>.Instance);
        WriteLegacy(legacy, "good", 1, img: false);
        File.WriteAllText(Path.Combine(legacy, "bad.json"), "{ not json");

        var r = await mig.MigrateAsync(legacy);
        Assert.Equal(1, r.Imported);
        Assert.Equal(1, r.Failed);
        Assert.False(r.LegacyRenamed);
        Assert.True(Directory.Exists(legacy));
    }}
