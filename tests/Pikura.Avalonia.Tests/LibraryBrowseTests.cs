using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Avalonia.Views.Gallery;
using Pikura.Avalonia.Views.Library;
using Pikura.Core.Data;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Services.Library;
using Pikura.Core.Settings;
using Xunit;

namespace Pikura.Avalonia.Tests;

public sealed class LibraryBrowseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _settingsPath;

    public LibraryBrowseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-av-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "library.db");
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release the database file
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Issue #24 part 1: the preview must not create Library view models ────

    [Fact]
    public void Advancing_The_Preview_Keeps_One_LibraryViewModel_And_One_First_Page_Load() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 205);

        var services = new ServiceCollection();
        services.AddSingleton<LibraryRepository>(lib.Repo);
        services.AddSingleton(lib.Index);
        services.AddSingleton(lib.Settings);
        services.AddSingleton(lib.Dialog);
        services.AddSingleton(lib.AutoTag);
        services.AddSingleton(UninitializedGalleryVm());
        services.AddSingleton((ImageLookupService)RuntimeHelpers.GetUninitializedObject(typeof(ImageLookupService)));
        AppServices.RegisterLibraryViewModel(services);
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(LibraryViewModel));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        await using var provider = services.BuildServiceProvider();
        var library = provider.GetRequiredService<LibraryViewModel>(); // the open Library page
        await library.CurrentRefreshTask;
        Assert.Equal(200, library.Items.Count);
        var firstPageLoads = lib.Repo.FirstPageSearches;

        // Each "next artwork" in the preview: the info line reads the file from the repository,
        // and the action buttons (find original, tag, delete) resolve the Library view model.
        foreach (var id in ids.Take(10))
        {
            var info = await InlineArtworkViewer.GetLibraryInfoTextAsync(provider.GetRequiredService<LibraryRepository>(), id);
            Assert.NotEqual("", info);
            Assert.Same(library, provider.GetRequiredService<LibraryViewModel>());
        }

        Assert.Equal(firstPageLoads, lib.Repo.FirstPageSearches); // no extra first page + thumbnails
        Assert.Equal(1, GetRunCompletedSubscriberCount(lib.AutoTag));
    });

    [Fact]
    public void Preview_Metadata_Helpers_Format_Info_And_Source() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var mtime = new DateTimeOffset(2026, 5, 10, 12, 30, 0, TimeSpan.Zero).ToString("O");
        var ids = await lib.Repo.UpsertFilesAsync([
            new LibraryFile
            {
                Path = Path.Combine(_dir, "a.jpg"), Root = _dir, Width = 1920, Height = 1080,
                Size = 2 * 1024 * 1024, Mtime = mtime, MediaType = "image", AddedAt = mtime,
                SourceUrl = "https://example.test/art/1",
            },
            new LibraryFile
            {
                Path = Path.Combine(_dir, "b.jpg"), Root = _dir, Width = 1200, Height = 1600,
                Size = 4096, Mtime = mtime, MediaType = "image", AddedAt = mtime,
                ArtworkId = "98765432",
            },
            new LibraryFile
            {
                Path = Path.Combine(_dir, "c.jpg"), Root = _dir, Width = 640, Height = 480,
                Size = 512, Mtime = mtime, MediaType = "image", AddedAt = mtime,
            },
        ]);

        var expectedDate = DateTime.Parse(mtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToLocalTime().ToString("g");
        Assert.Equal($"1920×1080 · {2.0:F1} MB · {expectedDate}", await InlineArtworkViewer.GetLibraryInfoTextAsync(lib.Repo, ids[0]));
        Assert.Equal($"1200×1600 · 4 KB · {expectedDate}", await InlineArtworkViewer.GetLibraryInfoTextAsync(lib.Repo, ids[1]));
        Assert.Equal($"640×480 · 512 B · {expectedDate}", await InlineArtworkViewer.GetLibraryInfoTextAsync(lib.Repo, ids[2]));
        Assert.Equal("", await InlineArtworkViewer.GetLibraryInfoTextAsync(lib.Repo, 999999));

        Assert.Equal("https://example.test/art/1", await InlineArtworkViewer.GetLibrarySourceUrlAsync(lib.Repo, ids[0]));
        Assert.Equal("https://www.pixiv.net/artworks/98765432", await InlineArtworkViewer.GetLibrarySourceUrlAsync(lib.Repo, ids[1]));
        Assert.Null(await InlineArtworkViewer.GetLibrarySourceUrlAsync(lib.Repo, ids[2]));
    });

    // ── Issue #24 part 2: scrolling pages through the grid ───────────────────

    [Fact]
    public void Dragging_The_Grid_To_The_Bottom_Pages_Through_401_Files() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 401);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;
        Assert.Equal(200, vm.Items.Count);

        var pager = new LibraryScrollPager();
        var grid = new FakeGrid(viewportHeight: 700, columns: 5, rowHeight: 100);

        // Page 1 laid out: the grid grew, the user hasn't scrolled, so page 2 isn't requested yet.
        var (offsetDelta, extentDelta) = grid.Layout(vm.Items.Count);
        Assert.False(OnScrollChanged(pager, grid, vm, offsetDelta, extentDelta));

        // Drag the thumb to the bottom: the offset moves and the extent doesn't (ExtentDelta.Y == 0).
        Assert.True(OnScrollChanged(pager, grid, vm, grid.ScrollToBottom(), 0));
        Assert.True(vm.IsLoadingPage);
        // Wiggling at the bottom while page 2 loads requests nothing more.
        Assert.False(OnScrollChanged(pager, grid, vm, grid.ScrollTo(grid.Offset - 20), 0));
        Assert.False(OnScrollChanged(pager, grid, vm, grid.ScrollToBottom(), 0));

        await vm.LoadMoreAsync(); // joins the page already in flight
        Assert.Equal(400, vm.Items.Count);
        // Page 2 is in, but layout hasn't grown the extent: the stale extent still reads "bottom".
        Assert.False(OnScrollChanged(pager, grid, vm, grid.ScrollTo(grid.Offset - 20), 0));
        Assert.False(OnScrollChanged(pager, grid, vm, grid.ScrollToBottom(), 0));
        Assert.True(pager.IsAwaitingLayout);

        // Layout grows the extent under the unmoved thumb: no cascade into page 3.
        (offsetDelta, extentDelta) = grid.Layout(vm.Items.Count);
        Assert.False(OnScrollChanged(pager, grid, vm, offsetDelta, extentDelta));
        Assert.False(pager.IsAwaitingLayout);

        // Drag to the new bottom: this crosses the second page boundary.
        Assert.True(OnScrollChanged(pager, grid, vm, grid.ScrollToBottom(), 0));
        await vm.LoadMoreAsync();
        (offsetDelta, extentDelta) = grid.Layout(vm.Items.Count);
        Assert.False(OnScrollChanged(pager, grid, vm, offsetDelta, extentDelta));

        Assert.Equal(401, vm.Items.Count);
        Assert.False(vm.HasMore);
        Assert.Equal(ids.Order(), vm.Items.Select(i => i.File.Id).Order());
    });

    [Fact]
    public void Paging_Held_By_Busy_Work_Resumes_When_The_Work_Ends() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        await SeedFilesAsync(lib.Repo, _dir, 450);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;
        var unblocked = 0;
        vm.PagingUnblocked += (_, _) => unblocked++;

        var pager = new LibraryScrollPager();
        // Small cards in a tall window: 200 cards don't fill the viewport, so there's no scrollbar.
        var grid = new FakeGrid(viewportHeight: 2000, columns: 20, rowHeight: 150);

        // A rescan reloads page 1 while it's still busy; that layout pass can't request page 2.
        vm.IsBusy = true;
        var (offsetDelta, extentDelta) = grid.Layout(vm.Items.Count);
        Assert.False(OnScrollChanged(pager, grid, vm, offsetDelta, extentDelta));

        vm.IsBusy = false;
        Assert.Equal(1, unblocked);
        // What LibraryView does on PagingUnblocked once layout has run.
        Assert.True(pager.OnPagingUnblocked(grid.Metrics, Paging(vm)));
        vm.OnScrolledToEnd();
        await vm.LoadMoreAsync();
        Assert.Equal(400, vm.Items.Count);
    });

    [Fact]
    public void Overlapping_And_Premature_Page_Loads_Are_Guarded() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        await SeedFilesAsync(lib.Repo, _dir, 450);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;
        Assert.Equal(200, vm.Items.Count);

        // Overlapping requests at one boundary (scrolling plus the preview's "next") run one query.
        var page2Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lib.Repo.Gate = _ => page2Gate.Task;
        var searches = lib.Repo.Searches;
        var overlapping = Enumerable.Range(0, 5).Select(_ => vm.LoadMoreAsync()).ToArray();
        vm.OnScrolledToEnd();
        Assert.True(vm.IsLoadingPage);
        Assert.False(vm.CanLoadMore);

        page2Gate.SetResult();
        await Task.WhenAll(overlapping);
        lib.Repo.Gate = null;
        Assert.Equal(searches + 1, lib.Repo.Searches);
        Assert.Equal(400, vm.Items.Count);
        Assert.Equal(400, vm.Items.Select(i => i.File.Id).Distinct().Count());

        // Busy work blocks paging.
        vm.IsBusy = true;
        Assert.False(vm.CanLoadMore);
        await vm.LoadMoreAsync();
        Assert.Equal(400, vm.Items.Count);
        vm.IsBusy = false;

        // A refresh supersedes a page in flight. That stale page landing first must neither append
        // its items nor clear the refresh's loading guard.
        var staleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lib.Repo.Gate = q => q.Offset > 0 ? staleGate.Task : refreshGate.Task;
        searches = lib.Repo.Searches;
        var stale = vm.LoadMoreAsync();
        var refresh = vm.RefreshAsync();
        Assert.True(vm.IsLoadingPage);
        Assert.False(vm.HasMore);
        Assert.False(vm.CanLoadMore);

        staleGate.SetResult();
        await stale;
        Assert.Empty(vm.Items);
        Assert.True(vm.IsLoadingPage);
        Assert.False(vm.CanLoadMore);

        // Requests while the refresh is loading don't query.
        vm.OnScrolledToEnd();
        await vm.LoadMoreAsync();

        refreshGate.SetResult();
        await refresh;
        lib.Repo.Gate = null;
        Assert.Equal(searches + 2, lib.Repo.Searches);
        Assert.Equal(200, vm.Items.Count);
        Assert.Equal(200, vm.Items.Select(i => i.File.Id).Distinct().Count());
        Assert.False(vm.IsLoadingPage);
        Assert.True(vm.CanLoadMore);
    });

    // ── OFFSET paging when rows come and go ──────────────────────────────────

    [Fact]
    public void Deleting_Loaded_Files_Keeps_The_Next_Page_Aligned() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 401);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;
        var unblocked = 0;
        vm.PagingUnblocked += (_, _) => unblocked++;

        var doomed = vm.Items.Take(3).Select(i => i.File.Id).ToArray();
        Assert.Equal(doomed, await vm.DeleteAndUnloadAsync(doomed));
        Assert.Equal(197, vm.Items.Count);
        Assert.Equal(1, unblocked); // a grid parked at the bottom gets its next page now

        await LoadAllAsync(vm);
        // Left at 200, the offset would start page 2 three rows late and skip three files.
        Assert.Equal(ids.Except(doomed).Order(), vm.Items.Select(i => i.File.Id).Order());
    });

    [Fact]
    public void A_Delete_Waits_For_The_Page_In_Flight_And_Holds_Paging() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 450);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;

        var page2Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lib.Repo.Gate = _ => page2Gate.Task;
        var page2 = vm.LoadMoreAsync(); // counted from the row positions before the delete
        var doomed = vm.Items.Take(2).Select(i => i.File.Id).ToArray();
        var delete = vm.DeleteAndUnloadAsync(doomed);

        Assert.False(vm.CanLoadMore);     // held: row positions are about to move
        Assert.False(delete.IsCompleted); // waits for page 2 to land first
        Assert.NotNull(await lib.Repo.GetByIdAsync(doomed[0]));

        page2Gate.SetResult();
        await page2;
        await delete;
        lib.Repo.Gate = null;
        Assert.True(vm.CanLoadMore);

        await LoadAllAsync(vm);
        Assert.Equal(ids.Except(doomed).Order(), vm.Items.Select(i => i.File.Id).Order());
    });

    [Fact]
    public void Files_Indexed_Above_The_Loaded_Range_Do_Not_Repeat_Cards() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 401);
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;

        // Downloads index themselves with added_at = now: the top of the default "recently added" sort.
        var downloads = await SeedFilesAsync(lib.Repo, _dir, 5, prefix: "download", firstSecond: 100_000);
        await LoadAllAsync(vm);

        var shown = vm.Items.Select(i => i.File.Id).ToList();
        Assert.Equal(shown.Count, shown.Distinct().Count());
        Assert.Equal(ids.Order(), shown.Except(downloads).Order());
    });

    // ── Tag chips ────────────────────────────────────────────────────────────

    [Fact]
    public void Tag_Chips_Are_Replaced_When_They_Reload() => SingleThreadContext.Run(async () =>
    {
        using var lib = CreateLibrary();
        var ids = await SeedFilesAsync(lib.Repo, _dir, 3, tags: "landscape sky");
        await lib.Repo.SetAiTagsAsync(ids[0], new[] { new LibraryAiTag { Tag = "1girl", Score = 0.9 } });
        var vm = lib.NewViewModel();
        await vm.CurrentRefreshTask;

        // Every completed auto-tag pass (LibraryAutoTagService.RunCompleted) reloads the chips.
        await vm.LoadChipsAsync();
        await vm.LoadChipsAsync();
        Assert.Equal(new[] { "1girl" }, vm.AiTagChips);
        Assert.Equal(new[] { "landscape", "sky" }, vm.PixivTagChips);

        // A pass that tagged more files changes the top tags: the chips become the new list.
        await lib.Repo.SetAiTagsAsync(ids[1], new[] { new LibraryAiTag { Tag = "solo", Score = 0.8 } });
        await vm.LoadChipsAsync();
        Assert.Equal(new[] { "1girl", "solo" }, vm.AiTagChips);
    });

    // ── Preview navigation ───────────────────────────────────────────────────

    [Fact]
    public void Preview_Navigation_Tells_Apart_Files_Of_One_Multi_Page_Work()
    {
        // A downloaded 3-page work is three library files that share the work's pixiv id.
        var pages = Enumerable.Range(0, 3).Select(i => LibraryCard("12345", 10 + i)).ToList();
        var list = pages.Append(LibraryCard("67890", 20)).ToList();

        for (var i = 0; i < pages.Count; i++)
            Assert.Equal(i, InlineArtworkViewer.IndexOfCard(list, pages[i])); // "next" from p1 reaches p2
        // Load-more rebuilds the cards; a new card for the same file still finds its place.
        Assert.Equal(2, InlineArtworkViewer.IndexOfCard(list, LibraryCard("12345", 12)));
        // Pixiv cards keep matching by id.
        Assert.Equal(0, InlineArtworkViewer.IndexOfCard([PixivCard("555")], PixivCard("555")));

        // Load-more returns every loaded file again: each file is kept once and none is dropped.
        var nav = pages.Take(2).ToList();
        InlineArtworkViewer.MergeNavCards(nav,
            [LibraryCard("12345", 10), LibraryCard("12345", 11), LibraryCard("12345", 12), LibraryCard("67890", 20)]);
        Assert.Equal(new long[] { 10, 11, 12, 20 }, nav.Select(c => c.LibraryFileId));
        Assert.Same(pages[0], nav[0]);
    }

    [Fact]
    public void Deleting_A_File_Closes_The_Tab_Showing_It_And_Drops_It_From_Other_Tabs()
    {
        // Tab A was opened on file 10 and the user pressed "next" to file 11. Tab B shows file 30
        // and navigates over 10-12. Tab C was opened on file 11 but has moved on to file 12.
        var a = LibraryTab(openedFrom: 10, shows: 11, files: [10, 11, 12]);
        var b = LibraryTab(openedFrom: 30, shows: 30, files: [10, 11, 12, 30]);
        var c = LibraryTab(openedFrom: 11, shows: 12, files: [11, 12]);
        var tabs = new ObservableCollection<ViewerTab> { a, b, c };
        var closed = new List<ViewerTab>();

        LibraryViewModel.RemoveDeletedFromTabs([11], tabs, t => { closed.Add(t); tabs.Remove(t); });

        Assert.Equal(new[] { a }, closed); // only the tab on screen with the deleted file closes
        Assert.Equal(new[] { b, c }, tabs);
        Assert.Equal(new long[] { 10, 12, 30 }, b.NavList.Select(x => x.LibraryFileId));
        Assert.Equal(3, b.TotalCount);
        Assert.Equal(new long[] { 12 }, c.NavList.Select(x => x.LibraryFileId));
        Assert.Equal(12, c.Card!.LibraryFileId);
    }

    [Theory]
    [InlineData("https://example.test/art/1", null, "https://example.test/art/1")]
    [InlineData("http://example.test/a", "5", "http://example.test/a")]
    [InlineData(null, "98765432", "https://www.pixiv.net/artworks/98765432")]
    [InlineData(null, null, null)]
    // Anything the shell would run or open locally is refused; the pixiv page is used when known.
    [InlineData("file:///C:/Windows/System32/calc.exe", null, null)]
    [InlineData(@"C:\Windows\System32\calc.exe", null, null)]
    [InlineData(@"\\attacker\share\run.exe", null, null)]
    [InlineData("ms-msdt:/id PCWDiagnostic", null, null)]
    [InlineData("javascript:alert(1)", "123", "https://www.pixiv.net/artworks/123")]
    [InlineData("not a url", null, null)]
    public void Source_Links_Open_Only_Web_Urls(string? sourceUrl, string? artworkId, string? expected)
    {
        var file = new LibraryFile { Path = "x.jpg", Root = "", SourceUrl = sourceUrl, ArtworkId = artworkId };
        Assert.Equal(expected, LibraryLinks.SourceUrlFor(file));
    }

    // ── Scroll decisions ─────────────────────────────────────────────────────

    [Fact]
    public void Pager_Asks_Again_After_A_Request_That_Added_Nothing()
    {
        var pager = new LibraryScrollPager();
        var bottom = new LibraryScrollMetrics(3300, 700, 4000);
        var nudgedUp = bottom with { OffsetY = 3280 };

        Assert.True(pager.OnScrollChanged(bottom, new(true, false, 200), 3300, 0, 0));
        // In flight: nothing more, however the user nudges the thumb.
        Assert.False(pager.OnScrollChanged(nudgedUp, new(false, true, 200), -20, 0, 0));
        Assert.False(pager.OnScrollChanged(bottom, new(false, true, 200), 20, 0, 0));
        // The page failed: no items and no layout change. The next scroll down asks again.
        Assert.False(pager.OnScrollChanged(nudgedUp, new(true, false, 200), -20, 0, 0));
        Assert.False(pager.IsAwaitingLayout);
        Assert.True(pager.OnScrollChanged(bottom, new(true, false, 200), 20, 0, 0));
    }

    [Theory]
    // Dragging scrollbar to bottom with constant extent (ExtentDelta == 0)
    [InlineData(3300, 700, 4000, 3300, 0, 0, true)]
    // Mouse wheel scroll entering bottom threshold with constant extent
    [InlineData(3100, 700, 4000, 120, 0, 0, true)]
    // Far from bottom while scrolling down
    [InlineData(1000, 700, 4000, 120, 0, 0, false)]
    // Unmeasured / collapsed ScrollViewer
    [InlineData(0, 0, 4000, 0, 0, 0, false)]
    [InlineData(0, 700, 0, 0, 0, 0, false)]
    // Scrollable content sitting untouched at the top must not prematurely load page 2
    [InlineData(0, 700, 900, 0, 0, 900, false)]
    // Scrolling upward near the bottom does not trigger next page
    [InlineData(3200, 700, 4000, -100, 0, 0, false)]
    // Already scrolled down and viewport expands to expose the bottom
    [InlineData(3000, 900, 4000, 0, 300, 0, true)]
    // Already scrolled down and extent shrinks (Avalonia may clamp OffsetDelta < 0)
    [InlineData(2800, 700, 3500, -500, 0, -500, true)]
    // Non-scrollable content shorter than viewport allows loading more items so user can scroll
    [InlineData(0, 1200, 600, 0, 0, 600, true)]
    // Content shrank below the viewport (smaller cards, deletes) and the offset clamped to 0
    [InlineData(0, 1200, 600, -800, 0, -2000, true)]
    public void ShouldLoadMoreOnScroll_Evaluates_Scroll_And_Layout_Cases(
        double offsetY, double viewportHeight, double extentHeight,
        double offsetDeltaY, double viewportDeltaY, double extentDeltaY,
        bool expected)
    {
        var actual = LibraryScrollPager.ShouldLoadMoreOnScroll(
            offsetY, viewportHeight, extentHeight,
            offsetDeltaY, viewportDeltaY, extentDeltaY);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(3300, 700, 4000, true)]  // scrolled to the bottom
    [InlineData(3100, 700, 4000, true)]  // within the threshold
    [InlineData(1000, 700, 4000, false)] // mid-grid
    [InlineData(0, 700, 900, false)]     // scrollable, still at the top
    [InlineData(0, 2000, 1500, true)]    // doesn't fill the viewport
    [InlineData(0, 0, 1500, false)]      // not measured yet
    public void IsParkedAtEnd_Evaluates_Positions_Without_A_Scroll_Event(
        double offsetY, double viewportHeight, double extentHeight, bool expected)
    {
        Assert.Equal(expected, LibraryScrollPager.IsParkedAtEnd(new LibraryScrollMetrics(offsetY, viewportHeight, extentHeight)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class LibraryStack : IDisposable
    {
        public required GatedLibraryRepository Repo { get; init; }
        public required LibraryIndexService Index { get; init; }
        public required SettingsService Settings { get; init; }
        public required DialogService Dialog { get; init; }
        public required LibraryAutoTagService AutoTag { get; init; }

        public LibraryViewModel NewViewModel() =>
            new(Index, Settings, Dialog, UninitializedGalleryVm(), imageLookup: null, AutoTag);

        public void Dispose()
        {
            AutoTag.Dispose();
            Index.Dispose();
            Repo.Dispose();
            Settings.Dispose();
        }
    }

    private LibraryStack CreateLibrary()
    {
        var repo = new GatedLibraryRepository(_dbPath);
        var settings = new SettingsService(_settingsPath);
        settings.Current.LibraryFolders = [_dir];
        var index = new LibraryIndexService(repo, settings, NullLogger<LibraryIndexService>.Instance);
        return new LibraryStack
        {
            Repo = repo,
            Index = index,
            Settings = settings,
            Dialog = new DialogService(NullLogger<DialogService>.Instance),
            AutoTag = new LibraryAutoTagService(index, settings, () => false, NullLogger<LibraryAutoTagService>.Instance),
        };
    }

    // The browse paths under test never touch the gallery, and a real one needs the whole app.
    private static GalleryViewModel UninitializedGalleryVm() =>
        (GalleryViewModel)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewModel));

    private static async Task<List<long>> SeedFilesAsync(LibraryRepository repo, string root, int count,
        string prefix = "sample", int firstSecond = 1, string? tags = null)
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var files = new List<LibraryFile>(count);
        for (var i = 1; i <= count; i++)
        {
            var stamp = baseTime.AddSeconds(firstSecond + i - 1).ToString("O");
            files.Add(new LibraryFile
            {
                Path = Path.Combine(root, $"{prefix}_{i:D4}.jpg"),
                Root = root,
                Size = 1024L * i,
                Mtime = stamp,
                Width = 800 + i,
                Height = 600 + i,
                MediaType = "image",
                Title = $"{prefix} {i:D4}",
                ArtistId = "1",
                ArtistName = "Tester",
                Tags = tags,
                AddedAt = stamp,
            });
        }
        return await repo.UpsertFilesAsync(files);
    }

    private static async Task LoadAllAsync(LibraryViewModel vm)
    {
        while (vm.CanLoadMore) await vm.LoadMoreAsync();
    }

    private static int GetRunCompletedSubscriberCount(LibraryAutoTagService autoTag)
    {
        var field = typeof(LibraryAutoTagService).GetField("RunCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var del = field!.GetValue(autoTag) as Delegate;
        return del?.GetInvocationList().Length ?? 0;
    }

    private static LibraryPagingState Paging(LibraryViewModel vm) => new(vm.CanLoadMore, vm.IsLoadingPage, vm.Items.Count);

    /// <summary>What LibraryView.OnLibraryScrollChanged does with one ScrollChanged event.</summary>
    private static bool OnScrollChanged(LibraryScrollPager pager, FakeGrid grid, LibraryViewModel vm,
        double offsetDelta, double extentDelta, double viewportDelta = 0)
    {
        if (!pager.OnScrollChanged(grid.Metrics, Paging(vm), offsetDelta, viewportDelta, extentDelta)) return false;
        vm.OnScrolledToEnd();
        return true;
    }

    private static ArtworkCardViewModel LibraryCard(string artworkId, long libraryFileId) =>
        new(new ArtworkPreview { Id = artworkId, Title = $"file {libraryFileId}", PageCount = 1 }) { LibraryFileId = libraryFileId };

    private static ArtworkCardViewModel PixivCard(string id) =>
        new(new ArtworkPreview { Id = id, Title = id, PageCount = 1 });

    private static ViewerTab LibraryTab(long openedFrom, long shows, long[] files)
    {
        var cards = files.Select(id => LibraryCard("12345", id)).ToList();
        var tab = new ViewerTab(cards.First(c => c.LibraryFileId == openedFrom), cards, cards.Count, source: "Library")
        {
            LibraryFileId = openedFrom,
        };
        tab.NavigateTo(cards.First(c => c.LibraryFileId == shows));
        return tab;
    }

    /// <summary>The browse ScrollViewer over a fixed-height grid, as Avalonia reports it after layout.</summary>
    private sealed class FakeGrid(double viewportHeight, int columns, double rowHeight)
    {
        public double Offset { get; private set; }
        public double Extent { get; private set; }
        public LibraryScrollMetrics Metrics => new(Offset, viewportHeight, Extent);

        /// <summary>Layout after the item count changed: the extent follows the rows and the offset
        /// clamps. Returns the deltas the next ScrollChanged carries.</summary>
        public (double Offset, double Extent) Layout(int itemCount)
        {
            var (offset, extent) = (Offset, Extent);
            Extent = Math.Ceiling(itemCount / (double)columns) * rowHeight;
            Offset = Math.Clamp(Offset, 0, Math.Max(0, Extent - viewportHeight));
            return (Offset - offset, Extent - extent);
        }

        /// <summary>Drags the scrollbar thumb; returns the offset delta.</summary>
        public double ScrollTo(double offset)
        {
            var old = Offset;
            Offset = Math.Clamp(offset, 0, Math.Max(0, Extent - viewportHeight));
            return Offset - old;
        }

        public double ScrollToBottom() => ScrollTo(double.MaxValue);
    }
}
