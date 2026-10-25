using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Models;

namespace Pikura.Avalonia.Views.Artwork;

public partial class FullscreenViewerWindow : Window
{
    private double _imgX, _imgY, _imgW, _imgH;
    private double _scale = 1.0;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartX, _panStartY;
    private DateTime _pressTime;
    private readonly GalleryViewModel _gallery;

    /// <summary>When set, the authoritative navigation list — overrides whatever the gallery would
    /// otherwise supply. Used so a collage only ever navigates its own artworks.</summary>
    private readonly IReadOnlyList<ArtworkCardViewModel>? _explicitNavList;

    public FullscreenViewerWindow()
    {
        InitializeComponent();
        _gallery = null!;
    }

    /// <param name="seedBitmap">
    /// Optional already-decoded image from the calling viewer. Shown immediately so fullscreen
    /// opens to the artwork instead of a blank canvas while the pages list and full-resolution
    /// image are re-fetched. Owned by the caller — displayed, never disposed here.
    /// </param>
    /// <param name="navList">
    /// Explicit list this viewer may navigate. Pass the collage's own items when opening from a
    /// collage so arrow keys and the artwork strip stay scoped to that collage instead of wandering
    /// into the whole gallery. When null, the list is derived from the gallery as before.
    /// </param>
    public FullscreenViewerWindow(ArtworkPreview artwork, GalleryViewModel gallery, Bitmap? seedBitmap = null,
        IReadOnlyList<ArtworkCardViewModel>? navList = null)
    {
        InitializeComponent();
        _gallery = gallery;
        _explicitNavList = navList;
        KeyDown += OnKeyDown;
        Canvas.PointerWheelChanged += OnWheel;
        Canvas.PointerPressed += OnPressed;
        Canvas.PointerMoved += OnMoved;
        Canvas.PointerReleased += OnReleased;
        // Belt and braces: if capture is taken away (popup opening, window losing focus) make sure
        // we don't stay stuck in panning mode.
        Canvas.PointerCaptureLost += (_, _) => _isPanning = false;
        Canvas.SizeChanged += (_, _) =>
        {
            if (PageGridOverlay is not null)
            {
                PageGridOverlay.Width = Canvas.Bounds.Width;
                PageGridOverlay.Height = Canvas.Bounds.Height;
            }
            LayoutArtworkStrip();
            if (DataContext is ArtworkViewerViewModel cur && (cur.CurrentPageBitmap != null || cur.IsUgoira))
                FitToCanvas();
        };

        if (PageGridOverlay is not null)
        {
            PageGridOverlay.Width = Canvas.Bounds.Width;
            PageGridOverlay.Height = Canvas.Bounds.Height;
        }

        AttachViewModel(new ArtworkViewerViewModel(artwork, gallery, this), seedBitmap);
    }

    private void AttachViewModel(ArtworkViewerViewModel vm, Bitmap? seedBitmap = null)
    {
        DataContext = vm;

        // Paint the caller's existing bitmap first so there's never an empty fullscreen window.
        // LoadFirstPageAsync replaces it with the Regular, then the Original.
        if (seedBitmap != null)
        {
            vm.CurrentPageBitmap = seedBitmap;
            FitToCanvasWhenReady();
        }
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArtworkViewerViewModel.CurrentPageBitmap) ||
                e.PropertyName == nameof(ArtworkViewerViewModel.UgoiraPreviewPath))
                Dispatcher.UIThread.Post(FitToCanvasWhenReady);
            // The multi-page strip appearing/disappearing moves the artwork strip above it.
            if (e.PropertyName == nameof(ArtworkViewerViewModel.HasMultiplePages))
                Dispatcher.UIThread.Post(LayoutArtworkStrip);
        };
        LayoutArtworkStrip();
        _ = vm.LoadFirstPageAsync();
    }

    // ── Artwork jump strip ───────────────────────────────────────────────────
    // A Canvas doesn't stretch children, so the strip's width and vertical position are driven
    // from code. It sits above the multi-page strip when that's showing.
    private const double ArtworkStripHeight = 136;   // source picker row + thumbnail row
    private const double PageStripHeight = 54;

    private void LayoutArtworkStrip()
    {
        if (ArtworkStrip is null || Canvas is null) return;
        ArtworkStrip.Width = Canvas.Bounds.Width;
        var pageStripVisible = DataContext is ArtworkViewerViewModel { HasMultiplePages: true };
        Canvas.SetBottom(ArtworkStrip, pageStripVisible ? PageStripHeight : 0);
    }

    /// <summary>The list this viewer navigates — the active tab's nav list, the externally supplied
    /// list, or the filtered gallery. Mirrors <see cref="NavigateGalleryArtwork"/> so the strip and
    /// the arrow keys always agree on what "next artwork" means.</summary>
    /// <summary>
    /// One selectable entry in the strip's "Show" dropdown — a named set of artworks. Listing each
    /// tab and collage by name makes switching between them a visible choice; the previous
    /// cycling toggle hid both the options and which one you were on.
    /// </summary>
    private sealed class StripSource
    {
        public required string Label { get; init; }
        public required IReadOnlyList<ArtworkCardViewModel> Items { get; init; }

        /// <summary>
        /// The artist's announced catalogue size, when known. Only informational — it can be 0 or
        /// stale, so it must not be the thing that decides whether more can be fetched.
        /// </summary>
        public int? TotalAvailable { get; init; }

        /// <summary>
        /// How to fetch more of this source, when its section supports paging. Null for sources
        /// that are already complete (a collage, a single submission).
        /// </summary>
        public SectionPager? Pager { get; init; }

        /// <summary>True when more of this source can still be fetched.</summary>
        public bool CanPageMore => Pager?.CanMore() == true;

        /// <summary>ComboBox renders plain objects via ToString().</summary>
        public override string ToString()
        {
            if (TotalAvailable is { } total && total > Items.Count)
                return $"{Label}  ({Items.Count} of {total})";
            if (CanPageMore)
                return $"{Label}  ({Items.Count}+)";
            return $"{Label}  ({Items.Count})";
        }
    }

    /// <summary>
    /// How to page a particular section. Lets the strip's "Load more" work for any section that
    /// supports it — Gallery, Rankings, Bookmarks — instead of being hard-wired to the gallery.
    /// </summary>
    /// <param name="CanMore">Whether another page can be fetched right now.</param>
    /// <param name="LoadMore">Fetches the next page.</param>
    /// <param name="Count">Items loaded so far, used to detect that a page returned nothing.</param>
    /// <param name="Total">The section's full size when it reports one.</param>
    private sealed record SectionPager(
        Func<bool> CanMore,
        Func<Task> LoadMore,
        Func<int> Count,
        int? Total);

    /// <summary>
    /// Builds a pager for a <see cref="ViewerTab.Source"/>. Both Rankings and Bookmarks call
    /// <c>SyncViewerTabs</c> after loading, so the owning tab's NavList grows and a rebuilt
    /// dropdown picks the new artworks up automatically.
    /// </summary>
    private SectionPager? PagerFor(string? source)
    {
        var head = (source ?? string.Empty).Split(':')[0];
        try
        {
            switch (head)
            {
                case "Rankings":
                {
                    var vm = AppServices.Get<EnhancedRankingsViewModel>();
                    if (vm is null) return null;
                    return new SectionPager(
                        () => vm.CanLoadMore && !vm.IsLoading,
                        () => vm.LoadMoreCommand.ExecuteAsync(null),
                        () => vm.Items.Count,
                        vm.TotalItems > 0 ? vm.TotalItems : null);
                }
                case "Bookmarks":
                {
                    var vm = AppServices.Get<BookmarksViewModel>();
                    if (vm is null) return null;
                    return new SectionPager(
                        () => vm.CanLoadMore && !vm.IsLoading,
                        () => vm.LoadMoreCommand.ExecuteAsync(null),
                        () => vm.PublicBookmarks.Count + vm.PrivateBookmarks.Count,
                        vm.TotalCount > 0 ? vm.TotalCount : null);
                }
                case "Gallery":
                    return GalleryPager();
            }
        }
        catch { /* section unavailable — just means no paging offered */ }
        return null;
    }

    private SectionPager? GalleryPager()
    {
        if (_gallery is null) return null;
        return new SectionPager(
            () => _gallery.CanLoadMore && !_gallery.IsLoading,
            () => _gallery.LoadMoreArtworksCommand.ExecuteAsync(null),
            // VisibleArtworks, not FilteredArtworks — the latter is paginated to one page.
            () => _gallery.VisibleArtworks.Count,
            _gallery.ArtworksTotal > 0 ? _gallery.ArtworksTotal : null);
    }

    /// <summary>The artworks of the currently chosen source. Null until the strip is first opened,
    /// in which case navigation uses the tab-derived default.</summary>
    private IReadOnlyList<ArtworkCardViewModel>? _selectedSourceItems;

    private IReadOnlyList<ArtworkCardViewModel> CurrentNavList()
    {
        if (_gallery is null) return [];

        // Whatever the user picked in the dropdown wins, so the strip and the arrow keys always
        // agree on what "next artwork" means.
        if (_selectedSourceItems is { Count: > 0 }) return _selectedSourceItems;

        // An explicitly supplied list wins — that's how a collage keeps navigation scoped to its
        // own artworks instead of leaking into the surrounding gallery.
        if (_explicitNavList is { Count: > 0 }) return _explicitNavList;
        var tab = _gallery.SelectedViewerTab;
        if (tab?.NavList.Count > 0) return tab.NavList;
        if (_gallery.InlineViewerCardList is { } ext) return ext;
        return _gallery.FilteredArtworks;
    }

    /// <summary>Caps how many per-artist gallery groups are listed — a rankings page can contain
    /// a hundred different artists, and a hundred dropdown entries is no more usable than none.</summary>
    private const int MaxArtistGroups = 12;

    /// <summary>
    /// Builds the dropdown contents, in this order:
    /// <list type="number">
    /// <item>This submission — only the artwork currently open (its pages live on the page strip).</item>
    /// <item>Each collage tab, by name.</item>
    /// <item>The gallery grouped per artist, collage-only artworks excluded.</item>
    /// <item>Everything, collages included.</item>
    /// </list>
    /// </summary>
    private List<StripSource> BuildSources()
    {
        var sources = new List<StripSource>();
        if (_gallery is null) return sources;

        var active = _gallery.SelectedViewerTab;

        // 1. Just this submission. Previously "This tab" meant the tab's whole nav list, which is
        //    what made the picker confusing — opening one artwork appeared to select a hundred.
        var currentCard = _gallery.InlineViewerCard ?? active?.Card;
        if (currentCard != null)
            sources.Add(new StripSource { Label = "This submission", Items = [currentCard] });

        // 2. Every collage tab, individually, so you can hop straight between collages.
        foreach (var tab in _gallery.ViewerTabs.Where(t => t.IsCollage))
        {
            var items = tab.CollageItems.ToList();
            if (items.Count == 0) continue;
            var prefix = ReferenceEquals(tab, active) ? "● ⊞ " : "⊞ ";
            sources.Add(new StripSource { Label = prefix + tab.Header, Items = items });
        }

        // 3. Every other open tab, labelled by the section it came from. ViewerTab.Source records
        //    that ("Rankings", "Discover", "Pixivision", "Bookmarks", "Search"…), so a tab opened
        //    from Rankings offers the ranking list rather than being lumped into a gallery group.
        //    These lists already hold fully-loaded cards, so nothing is re-fetched.
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in _gallery.ViewerTabs.Where(t => !t.IsCollage))
        {
            if (tab.NavList.Count <= 1) continue;   // a lone card is already "This submission"
            var section = SectionLabel(tab.Source);
            var label = string.IsNullOrWhiteSpace(section) ? tab.Header : $"{section}: {tab.Header}";
            if (ReferenceEquals(tab, active)) label = "● " + label;
            if (!seenLabels.Add(label)) continue;

            var pager = PagerFor(tab.Source);
            sources.Add(new StripSource
            {
                Label = label,
                Items = tab.NavList.ToList(),
                Pager = pager,
                TotalAvailable = pager?.Total is { } t && t > tab.NavList.Count ? t : null,
            });
        }

        // 4. Whatever each section is currently showing, even if no tab was opened from it.
        AddSectionSources(sources, seenLabels);

        // 5. Gallery artworks per artist, excluding collages.
        sources.AddRange(BuildArtistGroups());

        // 6. Absolutely everything, collages included. Also pageable, so "Load all" is available
        //    without having to switch to the artist's group first.
        var everything = BuildEverythingList();
        if (everything.Count > 0)
            sources.Add(new StripSource
            {
                Label = "Everything (incl. collages)",
                Items = everything,
                Pager = GalleryPager(),
            });

        return sources;
    }

    /// <summary>
    /// Friendly section name for a <see cref="ViewerTab.Source"/>. Gallery sources carry an artist
    /// id suffix ("Gallery:12345"), which isn't worth showing.
    /// </summary>
    private static string SectionLabel(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var head = source.Split(':')[0];
        return head switch
        {
            "Gallery"     => "Gallery",
            "Rankings"    => "Rankings",
            "Discover"    => "Discover",
            "Pixivision"  => "Pixivision",
            "Bookmarks"   => "Bookmarks",
            "Search"      => "Search",
            "Collection"  => "Collection",
            "Collage"     => "Collage",
            "Viewed"      => "Viewed history",
            _             => head,
        };
    }

    /// <summary>
    /// Adds whatever each section is currently displaying, so the dropdown reflects where the user
    /// has actually been browsing even when they never opened a tab from there.
    /// <para>
    /// Each section is resolved independently and guarded: these view models are optional
    /// singletons, and one not being registered yet must not take out the whole dropdown.
    /// Rankings is intentionally absent — it stores RankingCardViewModel, and converting those
    /// would mint fresh cards with no loaded thumbnails; its opened tabs are covered above.
    /// </para>
    /// </summary>
    private static void AddSectionSources(List<StripSource> sources, HashSet<string> seenLabels)
    {
        void AddSet(string label, IEnumerable<ArtworkCardViewModel>? items)
        {
            if (items is null) return;
            var list = items.Where(c => !string.IsNullOrEmpty(c.Id)).ToList();
            if (list.Count <= 1) return;
            if (!seenLabels.Add(label)) return;
            sources.Add(new StripSource { Label = label, Items = list });
        }

        static void Safely(Action action)
        {
            try { action(); } catch { /* section unavailable — skip it */ }
        }

        Safely(() =>
        {
            var vm = AppServices.Get<BookmarksViewModel>();
            AddSet("Bookmarks: Public", vm.FilteredPublic);
            AddSet("Bookmarks: Private", vm.FilteredPrivate);
            AddSet("Bookmarks: Local favorites", vm.FilteredFavorites);
        });

        Safely(() =>
        {
            var vm = AppServices.Get<DiscoverViewModel>();
            AddSet("Discover: Recommended", vm.FilteredWorks);
            AddSet("Discover: Artist works", vm.FilteredArtistWorks);
        });

        Safely(() => AddSet("Pixivision: Featured", AppServices.Get<PixivisionViewModel>().FeaturedWorks));
        Safely(() => AddSet("Search: Results", AppServices.Get<GlobalSearchViewModel>().DisplayResults));
        Safely(() => AddSet("Collection", AppServices.Get<CollectionsViewModel>().Works));
    }

    /// <summary>
    /// Gallery/tab artworks grouped by artist, deliberately excluding anything that only exists in
    /// a collage — collages get their own entries, and mixing them in here would double-count.
    /// The artist of the artwork currently open is listed first; the rest follow by size.
    /// </summary>
    private List<StripSource> BuildArtistGroups()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pool = new List<ArtworkCardViewModel>();

        void Add(ArtworkCardViewModel? card)
        {
            if (card is null || string.IsNullOrEmpty(card.Id)) return;
            if (seen.Add(card.Id)) pool.Add(card);
        }

        foreach (var tab in _gallery.ViewerTabs.Where(t => !t.IsCollage))
        {
            Add(tab.Card);
            foreach (var card in tab.NavList) Add(card);
        }

        // VisibleArtworks, not FilteredArtworks: the latter is *paginated*
        // (Skip/Take ItemsPerPage, default 50), so reading it capped every artist group at one
        // page no matter how much had actually been loaded.
        foreach (var card in _gallery.VisibleArtworks) Add(card);

        if (pool.Count == 0) return [];

        // Group by UserId, not display name. Names can be blank, duplicated, or differ between an
        // artwork's UserName and the followed-artist record's Name — matching on them meant the
        // gallery artist often failed to line up, so "Load all" never appeared.
        static string ArtistKey(ArtworkCardViewModel c) =>
            !string.IsNullOrWhiteSpace(c.UserId) ? c.UserId
            : !string.IsNullOrWhiteSpace(c.UserName) ? c.UserName
            : "unknown";

        static string ArtistLabel(IGrouping<string, ArtworkCardViewModel> g)
        {
            var named = g.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.UserName));
            return named?.UserName ?? g.Key;
        }

        var currentArtistId = (_gallery.InlineViewerCard ?? _gallery.SelectedViewerTab?.Card) is { } cur
            ? ArtistKey(cur)
            : null;

        // The gallery pages artworks in (50 at a time), so a group only holds what's been fetched.
        // Only the artist the gallery is actually browsing can be paged further, and whether more
        // exists comes from CanLoadMore — ArtworksTotal is just for the nicer "50 of 112" label.
        var galleryArtistId = _gallery.SelectedArtist?.UserId;
        var canPage = _gallery.CanLoadMore;

        return pool
            .GroupBy(ArtistKey)
            // Current artist first so the common case is right at the top, then biggest groups.
            .OrderByDescending(g => g.Key == currentArtistId)
            .ThenByDescending(g => g.Count())
            .Take(MaxArtistGroups)
            .Select(g => new StripSource
            {
                Label = $"{ArtistLabel(g)} — all in gallery",
                Items = g.ToList(),
                // Only the artist the gallery is actually browsing can be paged further.
                Pager = canPage && g.Key == galleryArtistId ? GalleryPager() : null,
                TotalAvailable = g.Key == galleryArtistId && _gallery.ArtworksTotal > g.Count()
                    ? _gallery.ArtworksTotal
                    : null,
            })
            .ToList();
    }

    /// <summary>
    /// The broadest scope: collage tiles, every tab's own artwork, every tab's full nav list, and
    /// finally the current gallery — so a single-artwork tab contributes the whole list it was
    /// opened from rather than just the one image it happens to be showing.
    /// </summary>
    private IReadOnlyList<ArtworkCardViewModel> BuildEverythingList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<ArtworkCardViewModel>();

        void Add(ArtworkCardViewModel? card)
        {
            if (card is null || string.IsNullOrEmpty(card.Id)) return;
            if (seen.Add(card.Id)) all.Add(card);
        }

        foreach (var tab in _gallery.ViewerTabs)
        {
            if (tab.IsCollage)
                foreach (var card in tab.CollageItems) Add(card);
            else
                Add(tab.Card);

            foreach (var card in tab.NavList) Add(card);
        }

        // VisibleArtworks is the full loaded set; FilteredArtworks is only the current page.
        foreach (var card in _gallery.VisibleArtworks) Add(card);

        return all;
    }

    /// <summary>
    /// The tab that actually owns <paramref name="card"/>, so clicking a strip entry can select
    /// that tab rather than retargeting whichever tab happens to be active.
    /// </summary>
    private ViewerTab? FindOwningTab(ArtworkCardViewModel card)
    {
        if (_gallery is null || string.IsNullOrEmpty(card.Id)) return null;

        foreach (var tab in _gallery.ViewerTabs)
        {
            if (tab.IsCollage)
            {
                if (tab.CollageItems.Any(c => c.Id == card.Id)) return tab;
            }
            else if (tab.Card?.Id == card.Id)
            {
                return tab;
            }
        }
        return null;
    }

    private CancellationTokenSource? _stripThumbCts;

    private void OnArtworkStripToggled(object? sender, RoutedEventArgs e)
    {
        if (ArtworkStrip is null) return;
        var show = ArtworkStripToggle?.IsChecked == true;

        ArtworkStrip.Height = ArtworkStripHeight;
        ArtworkStrip.IsVisible = show;

        if (show) PopulateSourceCombo();
        RefreshArtworkStrip(show);
        LayoutArtworkStrip();
    }

    /// <summary>
    /// Rebuilds the "Show" dropdown. Re-selects the entry the user was already on where possible,
    /// so reopening the strip (or opening it after switching tabs) doesn't silently reset the view.
    /// </summary>
    private void PopulateSourceCombo()
    {
        if (ArtworkSourceCombo is null) return;

        var previousLabel = (ArtworkSourceCombo.SelectedItem as StripSource)?.Label;
        var sources = BuildSources();

        _suppressSourceChanged = true;
        ArtworkSourceCombo.ItemsSource = sources;

        var restored = previousLabel is null
            ? null
            : sources.FirstOrDefault(s => s.Label == previousLabel);
        ArtworkSourceCombo.SelectedItem = restored ?? sources.FirstOrDefault();
        _suppressSourceChanged = false;

        _selectedSourceItems = (ArtworkSourceCombo.SelectedItem as StripSource)?.Items;
        UpdateStripHint();
    }

    private bool _suppressSourceChanged;

    private void OnArtworkSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceChanged) return;
        _selectedSourceItems = (ArtworkSourceCombo?.SelectedItem as StripSource)?.Items;
        UpdateStripHint();
        RefreshArtworkStrip(ArtworkStrip?.IsVisible == true);
    }

    private void UpdateStripHint()
    {
        var source = ArtworkSourceCombo?.SelectedItem as StripSource;
        var count = _selectedSourceItems?.Count ?? 0;

        if (ArtworkStripHint != null)
        {
            ArtworkStripHint.Text = count == 0
                ? string.Empty
                : "click to view, right-click to open in a new tab";
        }

        // Offer paging whenever the selected source's section supports it.
        var canPage = source?.CanPageMore == true;

        if (LoadMoreButton != null)
        {
            LoadMoreButton.IsVisible = canPage;
            LoadMoreButton.Content = Pikura.Avalonia.Localization.LocalizationService.Instance["Collections_LoadMore"];
        }

        if (LoadAllButton != null)
        {
            LoadAllButton.IsVisible = canPage;
            if (canPage)
            {
                LoadAllButton.Content = source!.TotalAvailable is { } total
                    ? $"Load all {total}"
                    : "Load all";
            }
        }
    }

    /// <summary>
    /// Pages in the artist's remaining artworks, then rebuilds the strip so it covers the whole
    /// gallery rather than just the first page the gallery happened to have loaded.
    /// </summary>
    // async void is required here: Avalonia's Click expects EventHandler<RoutedEventArgs>.
    // PageSourceAsync swallows its own exceptions, so nothing can escape onto the UI thread.

    /// <summary>Fetches a single further page of the selected source.</summary>
    private async void OnLoadMoreClicked(object? sender, RoutedEventArgs e)
        => await PageSourceAsync(maxPages: 1);

    /// <summary>Fetches every remaining page of the selected source.</summary>
    private async void OnLoadAllClicked(object? sender, RoutedEventArgs e)
        => await PageSourceAsync(maxPages: 200);

    /// <summary>
    /// Pages the selected source one request at a time, stopping early if a page brings nothing
    /// new. Driving the loop here (rather than calling a section's own "load all") means we can
    /// detect no-progress, refresh the strip between pages so the growth is visible, and work
    /// identically for Gallery, Rankings and Bookmarks.
    /// </summary>
    private async Task PageSourceAsync(int maxPages)
    {
        var pager = (ArtworkSourceCombo?.SelectedItem as StripSource)?.Pager;
        if (pager is null) return;

        if (LoadMoreButton != null) LoadMoreButton.IsEnabled = false;
        if (LoadAllButton != null) LoadAllButton.IsEnabled = false;

        try
        {
            for (var page = 0; page < maxPages && pager.CanMore(); page++)
            {
                var before = pager.Count();
                if (LoadAllButton != null) LoadAllButton.Content = $"Loading… ({before})";

                await pager.LoadMore();

                // Nothing new came back — stop rather than loop forever.
                if (pager.Count() <= before) break;

                // Refresh as we go so the strip visibly fills up.
                PopulateSourceCombo();
                RefreshArtworkStrip(ArtworkStrip?.IsVisible == true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Paging from fullscreen strip failed: {ex}");
        }
        finally
        {
            if (LoadMoreButton != null) LoadMoreButton.IsEnabled = true;
            if (LoadAllButton != null) LoadAllButton.IsEnabled = true;
            // Final rebuild so every source — including "Everything" — reflects the new totals.
            PopulateSourceCombo();
            RefreshArtworkStrip(ArtworkStrip?.IsVisible == true);
        }
    }

    /// <summary>Repopulates the strip for the current scope and starts fetching missing thumbnails.
    /// Any in-flight thumbnail run is cancelled first so switching scope doesn't stack downloads.</summary>
    private void RefreshArtworkStrip(bool show)
    {
        _stripThumbCts?.Cancel();
        _stripThumbCts?.Dispose();
        _stripThumbCts = null;

        if (!show || ArtworkStripList is null) return;

        var list = CurrentNavList();
        ArtworkStripList.ItemsSource = list;
        _stripThumbCts = new CancellationTokenSource();
        _ = LoadStripThumbnailsAsync(list, _stripThumbCts.Token);
    }

    /// <summary>
    /// Fills in any strip thumbnails that aren't already decoded, so a whole-gallery list shows
    /// real images instead of empty boxes.
    /// <para>
    /// Fetched in small batches rather than all at once: a gallery list can run to hundreds of
    /// artworks, and firing every request together saturates the shared image loader — starving
    /// the full-resolution image the user is actually looking at. Cancelled when the strip is
    /// hidden or the window closes so a big list doesn't keep downloading in the background.
    /// </para>
    /// </summary>
    private static async Task LoadStripThumbnailsAsync(IReadOnlyList<ArtworkCardViewModel> list, CancellationToken ct)
    {
        var loader = AppServices.Get<Pikura.Core.Services.PixivImageLoader>();
        if (loader is null) return;

        const int batchSize = 8;
        var pending = list.Where(c => c.Thumbnail is null).ToList();

        for (var i = 0; i < pending.Count; i += batchSize)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await Task.WhenAll(pending.Skip(i).Take(batchSize).Select(c => c.LoadThumbnailAsync(loader)));
            }
            catch
            {
                // An individual thumbnail failing just leaves that one tile blank.
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _stripThumbCts?.Cancel();
        _stripThumbCts?.Dispose();
        _stripThumbCts = null;
    }

    private void OnArtworkStripClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        if (_gallery is null) return;

        // Select the tab that owns this artwork rather than retargeting the active one. Without
        // this, clicking an artwork belonging to another tab rewrote the current tab's card — so a
        // single-artwork tab could never actually be reached from the strip.
        var owning = FindOwningTab(card);
        if (owning != null)
        {
            _gallery.SelectedViewerTab = owning;
            owning.NavigateTo(card);
            // A collage tab has to be told it's showing one item, or it springs back to the grid.
            if (owning.IsCollage) owning.IsViewingCollageItem = true;
        }
        else
        {
            // Not open in any tab (e.g. reached via the gallery-wide scope) — navigate in place.
            _gallery.SelectedViewerTab?.NavigateTo(card);
        }

        _gallery.InlineViewerCard = card;

        // Reset zoom/pan so the incoming artwork isn't shown at the previous one's transform.
        _scale = 1.0;
        _imgX = _imgY = _imgW = _imgH = 0;
        if (Image != null) { Image.Width = 0; Image.Height = 0; }

        AttachViewModel(new ArtworkViewerViewModel(card.Artwork, _gallery, this), card.Thumbnail);
        LayoutArtworkStrip();
        // Keep the strip populated with the same scoped list after navigating.
        if (ArtworkStripList != null) ArtworkStripList.ItemsSource = CurrentNavList();
    }

    /// <summary>
    /// Opens the right-clicked artwork as its own viewer tab rather than navigating this window to
    /// it, then exits fullscreen so the new tab is visible. Left-click still navigates in place.
    /// </summary>
    private void OnOpenArtworkInNewTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ArtworkCardViewModel card) return;
        if (_gallery is null) return;

        var list = CurrentNavList();
        _gallery.OpenInNewTab(card, list, list.Count, source: _gallery.SelectedViewerTab?.Source ?? "Gallery");
        _gallery.ShowPreview = true;
        Close();
    }

    private void FitToCanvasWhenReady()
    {
        if (Canvas == null) return;
        if (Canvas.Bounds.Width > 0 && Canvas.Bounds.Height > 0)
        {
            FitToCanvas();
            return;
        }
        // Canvas hasn't been laid out yet — retry after the next layout pass.
        void OnLayoutUpdated(object? s, EventArgs _)
        {
            Canvas.LayoutUpdated -= OnLayoutUpdated;
            FitToCanvas();
        }
        Canvas.LayoutUpdated += OnLayoutUpdated;
    }

    private void FitToCanvas()
    {
        if (Canvas == null) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;

        var cw = Canvas.Bounds.Width;
        var ch = Canvas.Bounds.Height;
        if (cw <= 0 || ch <= 0) return;

        if (vm.IsUgoira)
        {
            // Ugoira: the AnimatedImage intrinsic size isn't known until rendered.
            // Use the canvas size and let Stretch=Uniform handle aspect; just center it.
            var ugoira = UgoiraImage;
            if (ugoira == null) return;
            var iw = ugoira.DesiredSize.Width > 0 ? ugoira.DesiredSize.Width : cw;
            var ih = ugoira.DesiredSize.Height > 0 ? ugoira.DesiredSize.Height : ch;
            var scaleX = cw / iw;
            var scaleY = ch / ih;
            var s = Math.Min(scaleX, scaleY);
            var fw = iw * s;
            var fh = ih * s;
            ugoira.Width  = cw;   // fill canvas width; Stretch=Uniform keeps aspect
            ugoira.Height = ch;
            Canvas.SetLeft(ugoira, 0);
            Canvas.SetTop(ugoira, 0);
            _ = (fw, fh); // suppress unused warning
            return;
        }

        if (Image == null) return;
        var bmp = vm.CurrentPageBitmap;
        if (bmp == null) return;

        var bw = bmp.PixelSize.Width;
        var bh = bmp.PixelSize.Height;

        var scX = cw / bw;
        var scY = ch / bh;
        _scale = Math.Min(scX, scY);
        _imgW = bw * _scale;
        _imgH = bh * _scale;
        _imgX = (cw - _imgW) / 2;
        _imgY = (ch - _imgH) / 2;

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (Image == null || Canvas == null) return;

        // Size the image element to its logical display size
        Image.Width = _imgW;
        Image.Height = _imgH;

        // Position on canvas
        Canvas.SetLeft(Image, _imgX);
        Canvas.SetTop(Image, _imgY);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        var pos = e.GetPosition(Canvas);
        ZoomAroundPoint(factor, pos.X, pos.Y);
        e.Handled = true;
    }

    private void ZoomAroundPoint(double factor, double cx, double cy)
    {
        if (Canvas == null || Image == null) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;
        var bmp = vm.CurrentPageBitmap;
        if (bmp == null) return;

        var newScale = _scale * factor;
        if (newScale < 0.1) newScale = 0.1;
        if (newScale > 50) newScale = 50;

        var bw = bmp.PixelSize.Width;
        var bh = bmp.PixelSize.Height;

        var oldW = bw * _scale;
        var oldH = bh * _scale;
        var newW = bw * newScale;
        var newH = bh * newScale;

        _imgX = cx - (cx - _imgX) * (newW / oldW);
        _imgY = cy - (cy - _imgY) * (newH / oldH);
        _scale = newScale;
        _imgW = newW;
        _imgH = newH;

        ApplyTransform();
    }

    /// <summary>
    /// True when a pointer press came from the viewer's own chrome — the artwork strip and its
    /// "Show" dropdown, the page strip, the all-pages overlay, any button — rather than from the
    /// image itself.
    /// <para>
    /// Panning has to ignore those. Pressing the dropdown used to start a pan, and because the
    /// release then landed on the dropdown's popup (a separate visual root the Canvas never sees)
    /// the pan never ended — so the image kept following the mouse until you clicked back on it.
    /// </para>
    /// </summary>
    private bool IsChromePress(PointerPressedEventArgs e)
    {
        // A child control that already dealt with the press (button, combo box) — leave it alone.
        if (e.Handled) return true;
        if (e.Source is not Visual source) return false;

        foreach (var ancestor in source.GetSelfAndVisualAncestors())
        {
            // Reached the image or the bare canvas background: a genuine pan.
            if (ReferenceEquals(ancestor, Image) || ReferenceEquals(ancestor, UgoiraImage)) return false;
            if (ReferenceEquals(ancestor, Canvas)) return false;

            if (ancestor is Button or ComboBox) return true;
            if (ReferenceEquals(ancestor, ArtworkStrip) ||
                ReferenceEquals(ancestor, PageStrip) ||
                ReferenceEquals(ancestor, PageGridOverlay)) return true;
        }

        // Didn't reach the Canvas at all — the press came from a popup, so it isn't the image.
        return true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed) return;
        if (IsChromePress(e)) return;

        _isPanning = true;
        _pressTime = DateTime.Now;
        _panStart = e.GetPosition(Canvas);
        _panStartX = _imgX;
        _panStartY = _imgY;
        // Capture the pointer so the release always comes back here even if the cursor ends up
        // over the strip or off-window. Without it a pan could latch on indefinitely.
        e.Pointer.Capture(Canvas);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || Canvas == null) return;
        var pos = e.GetPosition(Canvas);
        _imgX = _panStartX + (pos.X - _panStart.X);
        _imgY = _panStartY + (pos.Y - _panStart.Y);
        ApplyTransform();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        e.Pointer.Capture(null);

        if (WindowState == WindowState.FullScreen && DataContext is ArtworkViewerViewModel vm)
        {
            var clickDuration = DateTime.Now - _pressTime;
            if (clickDuration.TotalMilliseconds < 300)
            {
                var pos = e.GetPosition(Canvas);
                var canvasWidth = Canvas?.Bounds.Width ?? 0;
                
                if (pos.X < canvasWidth * 0.3 && vm.PrevPageCommand.CanExecute(null))
                    vm.PrevPageCommand.Execute(null);
                else if (pos.X > canvasWidth * 0.7 && vm.NextPageCommand.CanExecute(null))
                    vm.NextPageCommand.Execute(null);
            }
        }
    }

    private void OnPageStripClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PageThumbnailItem item) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;
        _ = vm.GoToPageAsync(item.Index);
    }

    private void OnClosePageGrid(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ArtworkViewerViewModel vm) return;
        vm.TogglePageGridCommand.Execute(null);
    }

    private void OnPageGridItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PageThumbnailItem item) return;
        if (DataContext is not ArtworkViewerViewModel vm) return;
        vm.SelectPageFromGridCommand.Execute(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (DataContext is not ArtworkViewerViewModel vm) return;

        var settings = AppServices.Get<Pikura.Core.Settings.SettingsService>().Current;

        // Left/Right: page navigation within the artwork first.
        // If no page turn is possible (single-page or at the edge) and gallery keyboard
        // nav is enabled, fall through to artwork-level navigation in the nav list.
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (vm.CurrentPageIndex > 0)
                vm.PrevPageCommand.Execute(null);
            else if (settings.FullscreenKeyboardNavEnabled)
                NavigateGalleryArtwork(-1, vm);
            return;
        }

        if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (vm.CurrentPageIndex < vm.PageCount - 1)
                vm.NextPageCommand.Execute(null);
            else if (settings.FullscreenKeyboardNavEnabled)
                NavigateGalleryArtwork(+1, vm);
            return;
        }
    }

    private void NavigateGalleryArtwork(int direction, ArtworkViewerViewModel currentVm)
    {
        var tab = _gallery.SelectedViewerTab;
        // Single source of truth shared with the artwork strip, so an explicitly scoped list
        // (e.g. a collage's own four images) is honoured here too.
        var list = CurrentNavList();
        if (list.Count == 0) return;

        var currentCard = _gallery.InlineViewerCard;
        if (currentCard == null) return;
        var idx = list.TakeWhile(c => c.Id != currentCard.Id).Count();
        if (idx >= list.Count) return;

        var nextIdx = idx + direction;
        if (nextIdx < 0 || nextIdx >= list.Count) return;

        var nextCard = list[nextIdx];
        tab?.NavigateTo(nextCard);
        _gallery.InlineViewerCard = nextCard;

        // Reset canvas state so stale image/position from previous artwork is cleared
        _scale = 1.0;
        _imgX = _imgY = _imgW = _imgH = 0;
        if (Image != null) { Image.Width = 0; Image.Height = 0; }
        AttachViewModel(new ArtworkViewerViewModel(nextCard.Artwork, _gallery, this));
    }
}
