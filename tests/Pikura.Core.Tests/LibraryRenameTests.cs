using Pikura.Core.Data;
using Pikura.Core.Services;
using Pikura.Core.Services.Library;
using Xunit;

namespace Pikura.Core.Tests;

public class LibraryRenamePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pikura-rename-" + Guid.NewGuid().ToString("N"));

    public LibraryRenamePlanTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private LibraryFile F(long id, string rel, string? artworkId = "123", int? page = 0, string? artist = "Alice",
        string? artistId = "9", string? title = "Sunset", string? source = "pixiv") => new()
    {
        Id = id, Path = Path.Combine(_root, rel), Root = _root, ArtworkId = artworkId, PageIndex = page,
        ArtistName = artist, ArtistId = artistId, Title = title, MetadataSource = source, AddedAt = "t",
    };

    private static RenameOptions O(string folder, string name, bool r18 = false) => new(folder, name, SeparateR18Folder: r18);

    [Fact]
    public void Resolves_Folder_And_Filename_Keeping_Extension()
    {
        var plan = LibraryRenameService.Plan([new RenameFileInfo(F(1, @"old\x.png"))], O("%artist% (%member_id%)", "%title%_%image_id%"));
        var item = Assert.Single(plan);
        Assert.Equal(RenameStatus.Rename, item.Status);
        Assert.Equal(Path.Combine(_root, "Alice (9)", "Sunset_123.png"), item.NewPath);
    }

    [Theory]
    [InlineData(@"..\escaped")]
    [InlineData(@"\elsewhere")]
    public void Folder_Template_Cannot_Leave_The_Library_Folder(string folder)
    {
        var plan = LibraryRenameService.Plan([new RenameFileInfo(F(1, "a.jpg"))], O(folder, "%image_id%"));
        Assert.Equal(RenameStatus.Skipped, plan[0].Status);
        Assert.True(plan[0].OutsideLibrary);
    }

    [Fact]
    public void Multi_Page_Without_Page_Token_Gets_P_Suffix()
    {
        var plan = LibraryRenameService.Plan([new RenameFileInfo(F(1, "a.jpg", page: 2), PageCount: 3)], O("", "%image_id%"));
        Assert.Equal(Path.Combine(_root, "123_p2.jpg"), plan[0].NewPath);
    }

    [Fact]
    public void Missing_Data_Token_Skips_With_Reason()
    {
        var plan = LibraryRenameService.Plan([new RenameFileInfo(F(1, "a.jpg", artist: null))], O("%artist%", "%image_id%"));
        Assert.Equal(RenameStatus.Skipped, plan[0].Status);
        Assert.Equal("%artist%", plan[0].MissingTokens);
        Assert.Equal(plan[0].OldPath, plan[0].NewPath);
    }

    [Fact]
    public void Character_And_Series_Tokens()
    {
        var info = new RenameFileInfo(F(1, "a.jpg", artworkId: null, artist: null, title: null, source: null),
            Character: "hatsune miku", Series: "vocaloid");
        var plan = LibraryRenameService.Plan([info], O("%series%", "%character%"));
        Assert.Equal(Path.Combine(_root, "vocaloid", "hatsune miku.jpg"), plan[0].NewPath);
    }

    [Fact]
    public void Conflicts_Are_Numbered_And_Same_Path_Is_Unchanged()
    {
        File.WriteAllText(Path.Combine(_root, "Sunset.jpg"), "existing");
        var plan = LibraryRenameService.Plan(
        [
            new RenameFileInfo(F(1, "a.jpg", artworkId: "1")),
            new RenameFileInfo(F(2, "b.jpg", artworkId: "2")),
            new RenameFileInfo(F(3, "Alice.jpg", artworkId: "3")),
        ], O("", "%title%"));
        Assert.Equal(Path.Combine(_root, "Sunset (2).jpg"), plan[0].NewPath);
        Assert.Equal(Path.Combine(_root, "Sunset (3).jpg"), plan[1].NewPath);

        var same = LibraryRenameService.Plan([new RenameFileInfo(F(3, "Alice.jpg"))], O("", "%artist%"));
        Assert.Equal(RenameStatus.Unchanged, same[0].Status);
    }

    [Fact]
    public void Separate_R18_Folder_Uses_Ai_Rating_For_Files_Without_Pixiv_Rating()
    {
        var file = F(1, "a.jpg", source: null);
        file.AiRating = "explicit";
        var plan = LibraryRenameService.Plan([new RenameFileInfo(file)], O("%artist%", "%image_id%", r18: true));
        Assert.Equal(Path.Combine(_root, "Alice", "R-18", "123.jpg"), plan[0].NewPath);

        var pixivRated = F(2, "b.jpg", source: "pixiv");
        pixivRated.AiRating = "explicit";
        Assert.True(file.IsR18);
        Assert.False(pixivRated.IsR18);
    }

    [Fact]
    public void Top_Rating_Ignores_Other_Categories()
    {
        var top = AnimeTaggerService.PickTopRating(
        [
            ("1girl", 0, 0.99), ("general", 9, 0.10), ("questionable", 9, 0.30), ("explicit", 9, 0.55),
        ]);
        Assert.Equal("explicit", top!.Name);
        Assert.Null(AnimeTaggerService.PickTopRating([("1girl", 0, 0.9)]));
    }
}

public class LibraryRenameApplyTests
{
    [Fact]
    public async Task Apply_Moves_Files_Sidecar_And_Index_Then_Undo_Restores()
    {
        using var fx = new LibraryFixture();
        var root = Path.Combine(fx.Dir, "lib");
        var oldDir = Path.Combine(root, "unsorted");
        Directory.CreateDirectory(oldDir);
        var oldPath = Path.Combine(oldDir, "123_p0.jpg");
        File.WriteAllText(oldPath, "img");
        File.WriteAllText(oldPath + ".tags.txt", "General: 1girl");
        var id = await fx.Repo.UpsertFileAsync(new LibraryFile
        {
            Path = oldPath, Root = root, MediaType = "image", ArtworkId = "123", PageIndex = 0,
            ArtistName = "Alice", ArtistId = "9", Title = "Sunset", MetadataSource = "pixiv", AddedAt = "t",
        });
        var svc = new LibraryRenameService(fx.Repo);
        var files = (await fx.Repo.SearchAsync(new LibraryQuery { IncludeR18 = true })).Items;
        var plan = LibraryRenameService.Plan(await svc.LoadInfoAsync(files), new RenameOptions("%artist%", "%title%"));

        var result = await svc.ApplyAsync(plan, [root]);
        var newPath = Path.Combine(root, "Alice", "Sunset.jpg");
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(newPath));
        Assert.True(File.Exists(newPath + ".tags.txt"));
        Assert.False(Directory.Exists(oldDir)); // emptied folder removed
        Assert.True(Directory.Exists(root));    // library root kept
        Assert.Equal(newPath, (await fx.Repo.GetByIdAsync(id))!.Path);
        Assert.Single((await fx.Repo.SearchAsync(new LibraryQuery { Text = "Alice" })).Items, f => f.Path == newPath);

        await svc.UndoAsync(result, [root]);
        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(oldPath + ".tags.txt"));
        Assert.Equal(oldPath, (await fx.Repo.GetByIdAsync(id))!.Path);
    }

    [Fact]
    public async Task Apply_Handles_Name_Swaps()
    {
        using var fx = new LibraryFixture();
        var root = Path.Combine(fx.Dir, "lib");
        Directory.CreateDirectory(root);
        string P(string n) => Path.Combine(root, n);
        File.WriteAllText(P("a.jpg"), "A");
        File.WriteAllText(P("b.jpg"), "B");
        var ida = await fx.Repo.UpsertFileAsync(new LibraryFile { Path = P("a.jpg"), Root = root, AddedAt = "t" });
        var idb = await fx.Repo.UpsertFileAsync(new LibraryFile { Path = P("b.jpg"), Root = root, AddedAt = "t" });
        var plan = new[]
        {
            new RenamePlanItem(ida, P("a.jpg"), P("b.jpg"), RenameStatus.Rename),
            new RenamePlanItem(idb, P("b.jpg"), P("a.jpg"), RenameStatus.Rename),
        };
        var result = await new LibraryRenameService(fx.Repo).ApplyAsync(plan, [root]);
        Assert.Empty(result.Failed);
        Assert.Equal("A", File.ReadAllText(P("b.jpg")));
        Assert.Equal("B", File.ReadAllText(P("a.jpg")));
    }

    [Fact]
    public async Task R18_Filter_Uses_Explicit_Ai_Rating_Only_Without_Pixiv_Rating()
    {
        using var fx = new LibraryFixture();
        LibraryFile F(string n, string? source) => new() { Path = Path.Combine(fx.Dir, n), Root = fx.Dir, MediaType = "image", MetadataSource = source, AddedAt = "t" };
        var expl = await fx.Repo.UpsertFileAsync(F("explicit.jpg", null));
        var quest = await fx.Repo.UpsertFileAsync(F("questionable.jpg", null));
        var pixiv = await fx.Repo.UpsertFileAsync(F("pixiv.jpg", "pixiv"));
        var untagged = await fx.Repo.UpsertFileAsync(F("untagged.jpg", null));
        await fx.Repo.SetAiTagsAsync(expl, [], "explicit");
        await fx.Repo.SetAiTagsAsync(quest, [], "questionable");
        await fx.Repo.SetAiTagsAsync(pixiv, [], "explicit");

        var safe = (await fx.Repo.SearchAsync(new LibraryQuery { IncludeR18 = false })).Items.Select(f => Path.GetFileName(f.Path)).ToList();
        Assert.DoesNotContain("explicit.jpg", safe);
        Assert.Contains("questionable.jpg", safe);
        Assert.Contains("pixiv.jpg", safe);
        Assert.Contains("untagged.jpg", safe);
        Assert.Equal(4, (await fx.Repo.SearchAsync(new LibraryQuery { IncludeR18 = true })).Total);

        // Rated files leave the "needs tagging" queue; unrated ones (incl. sidecar-only) stay in it.
        var missing = (await fx.Repo.GetFilesMissingAiTagsAsync()).Select(f => f.Id).ToList();
        Assert.Equal([untagged], missing);
    }

    [Fact]
    public async Task Folder_Filter_And_Facets()
    {
        using var fx = new LibraryFixture();
        var root = Path.Combine(fx.Dir, "lib");
        LibraryFile F(string rel, string? artistId, string? name) => new()
        {
            Path = Path.Combine(root, rel), Root = root, MediaType = "image", ArtistId = artistId, ArtistName = name, AddedAt = "t",
        };
        await fx.Repo.UpsertFileAsync(F(@"Alice\a.jpg", "1", "Alice"));
        await fx.Repo.UpsertFileAsync(F(@"Alice\sub\b.jpg", "1", "Alice"));
        await fx.Repo.UpsertFileAsync(F(@"Bob\c.jpg", "2", "Bob"));
        await fx.Repo.UpsertFileAsync(F(@"Alice_x\d.jpg", null, null));

        Assert.Equal(2, (await fx.Repo.SearchAsync(new LibraryQuery { Folder = Path.Combine(root, "Alice") })).Total);
        Assert.Equal([("1", "Alice", 2L), ("2", "Bob", 1L)], await fx.Repo.GetArtistFacetsAsync());

        var folders = (await fx.Repo.GetFolderFacetsAsync()).ToDictionary(f => Path.GetRelativePath(root, f.Folder), f => f.Count);
        Assert.Equal(4, folders["."]);
        Assert.Equal(2, folders["Alice"]);
        Assert.Equal(1, folders[Path.Combine("Alice", "sub")]);
    }
}
