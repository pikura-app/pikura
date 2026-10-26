using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Data;
using Pikura.Core.Services.Library;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.ViewModels;

public partial class LibraryItemViewModel : ObservableObject
{
    public LibraryFile File { get; }
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;
    public LibraryItemViewModel(LibraryFile f) => File = f;
    public string DisplayName => !string.IsNullOrEmpty(File.Title) ? File.Title! : System.IO.Path.GetFileName(File.Path);
    public string SubText => File.ArtistName ?? "";
    public string PathTooltip => File.Path;
    public string DetailsLine
    {
        get
        {
            var line = $"{File.Width}x{File.Height} · {DuplicateFileRow.FormatSize(File.Size)} · {File.Mtime}";
            var url = File.SourceUrl ?? (File.ArtworkId != null ? $"https://www.pixiv.net/artworks/{File.ArtworkId}" : null);
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u)) line += " · " + u.Host;
            return line;
        }
    }
    /// <summary>Call after File's metadata was filled in so the card text refreshes.</summary>
    public void NotifyMetadataChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SubText));
    }
    /// <summary>Display height: CardSize in fixed mode; aspect-scaled in natural mode.</summary>
    [ObservableProperty] private double _imageHeight = 180;
    /// <summary>Recomputes <see cref="ImageHeight"/> for the given card size + mode.</summary>
    internal void UpdateImageHeight(int cardSize, bool natural)
    {
        if (!natural) { ImageHeight = cardSize; return; }
        var w = File.Width; var h = File.Height;
        var ratio = (w > 0 && h > 0) ? (double)h / w : 1.0;
        ImageHeight = cardSize * Math.Clamp(ratio, 0.5, 3.0);
    }
}

public partial class DuplicateFileRow : ObservableObject
{
    public LibraryFile File { get; }
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isMarked;
    public DuplicateFileRow(LibraryFile f) => File = f;
    public string Info => $"{File.Width}x{File.Height} · {FormatSize(File.Size)}";
    public string DateText => File.Mtime ?? "";
    public static string FormatSize(long b) => b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : b >= 1024 ? $"{b / 1024} KB" : $"{b} B";
}

public partial class DuplicateGroupViewModel : ObservableObject
{
    public ObservableCollection<DuplicateFileRow> Rows { get; } = [];
    public string Badge { get; }
    public DuplicateGroupViewModel(DuplicateGroup g)
    {
        Badge = g.IsExact
            ? LocalizationService.Instance["Library_Exact"]
            : string.Format(LocalizationService.Instance["Library_SimilarDistance"], g.MaxDistance);
        foreach (var f in g.Files) Rows.Add(new DuplicateFileRow(f));
    }
    public void KeepBest()
    {
        var best = Rows.OrderByDescending(r => r.File.Width * (long)r.File.Height)
            .ThenByDescending(r => r.File.Size).ThenBy(r => r.File.Mtime).FirstOrDefault();
        foreach (var r in Rows) r.IsMarked = r != best;
    }
}

public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryIndexService _library;
    private readonly SettingsService _settings;
    private readonly DialogService _dialog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseTab), nameof(IsDuplicatesTab))]
    private int _tabIndex;
    public bool IsBrowseTab => TabIndex == 0;
    public bool IsDuplicatesTab => TabIndex == 1;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _artistFilter = "";
    [ObservableProperty] private bool _includeR18;
    [ObservableProperty] private int _mediaTypeIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private int _orientationIndex;
    [ObservableProperty] private int _yearIndex;
    [ObservableProperty] private DateTimeOffset? _dateFrom;
    [ObservableProperty] private DateTimeOffset? _dateTo;
    [ObservableProperty] private string _resultsText = "";
    public ObservableCollection<string> YearOptions { get; } = [""]; // index 0 = "Any year" (label set in RefreshFolders)
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(ArtistFilter)
        || YearIndex > 0 || DateFrom != null || DateTo != null || OrientationIndex > 0;
    // ── Multi-select + details panel ──
    public System.Collections.Generic.List<LibraryItemViewModel> SelectedItems =>
        Items.Where(i => i.IsSelected).ToList();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsSingle), nameof(HasMultiSelection))]
    private int _selectedCount;
    public bool HasMultiSelection => SelectedCount > 1;
    public LibraryItemViewModel? DetailsSingle => SelectedCount == 1 ? SelectedItems.FirstOrDefault() : null;
    [ObservableProperty] private Bitmap? _detailsPreview;
    [ObservableProperty] private bool _showDetails = true;
    [ObservableProperty] private string _selectionSummary = "";
    private int _lastSelectedIndex = -1;
    private bool _updatingYears;
    public GalleryViewModel GalleryVm { get; }
    public bool HasTabs => GalleryVm.HasTabs;
    private readonly Pikura.Core.Services.ImageLookupService? _imageLookup;
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _isViewerExpanded;
    [ObservableProperty] private double _browsePanelWidth;
    partial void OnBrowsePanelWidthChanged(double value)
        => _settings.Update(s => s.BrowsePanelWidth = value);
    // Card display options (Bookmarks parity; persisted in AppSettings).
    [ObservableProperty] private int _cardSize;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedGrid), nameof(ShowNaturalGrid), nameof(ShowList))]
    private bool _isGridView = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedGrid), nameof(ShowNaturalGrid), nameof(ShowList))]
    private bool _isListView;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedGrid), nameof(ShowNaturalGrid))]
    private bool _isFixedHeight = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedGrid), nameof(ShowNaturalGrid))]
    private bool _isNaturalHeight;
    public bool ShowFixedGrid => IsGridView && IsFixedHeight;
    public bool ShowNaturalGrid => IsGridView && IsNaturalHeight;
    public bool ShowList => IsListView;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showTags = true;
    [RelayCommand] public void SetFixedHeight()   { IsFixedHeight = true;  IsNaturalHeight = false; }
    [RelayCommand] public void SetNaturalHeight() { IsFixedHeight = false; IsNaturalHeight = true;  }
    [RelayCommand] public void SetGridView()      { IsGridView = true;  IsListView = false; }
    [RelayCommand] public void SetListView()      { IsGridView = false; IsListView = true;  }
    partial void OnCardSizeChanged(int value)
    {
        _settings.Update(s => s.CardSize = value);
        RefreshImageHeights();
    }
    partial void OnIsNaturalHeightChanged(bool value) => RefreshImageHeights();
    private void RefreshImageHeights()
    { foreach (var i in Items) i.UpdateImageHeight(CardSize, IsNaturalHeight); }
    partial void OnShowInfoChanged(bool value) => _settings.Update(s => s.ShowInfo = value);
    partial void OnShowTagsChanged(bool value) => _settings.Update(s => s.ShowTags = value);
    // session cache: fileId → pixiv id found via reverse search ("" = miss)
    private readonly Dictionary<long, string> _revSearchCache = new();
    private int ResultsTotal;
    private bool _autoSearch, _sauceKeyRejected;
    [ObservableProperty] private string? _r18Notice;
    [ObservableProperty] private string? _revSearchStatus;
    private int _queryGen; // bumped by RefreshAsync; stale page loads are discarded
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string? _progressLine;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isBusy;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasFiles))]
    private long _totalFiles;
    [ObservableProperty] private string? _noticeLine;
    public bool AnyFolderExists => Folders.Any(f => f.Exists);
    public bool IsCustomFolders => _settings.Current.LibraryFolders != null;

    /// <summary>One configured library folder chip.</summary>
    public sealed class LibraryFolderItem
    {
        public required string Path { get; init; }
        public required bool Exists { get; init; }
    }
    public ObservableCollection<LibraryFolderItem> Folders { get; } = [];
    public bool IsEmpty => TotalFiles == 0 && !IsBusy;
    public bool HasFiles => TotalFiles > 0;
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private double _dupThreshold;
    [ObservableProperty] private bool _dupScanDone;
    [ObservableProperty] private string? _pendingRehashText;

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];
    public ObservableCollection<string> AiTagChips { get; } = [];
    public ObservableCollection<string> PixivTagChips { get; } = [];
    public ObservableCollection<string> ActiveFilters { get; } = [];
    public ObservableCollection<DuplicateGroupViewModel> DupGroups { get; } = [];

    private readonly List<string> _reqPixivTags = [];
    private readonly List<string> _reqAiTags = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _workCts;
    private int _offset;
    private bool _loadingPage;
    private readonly Dictionary<long, Bitmap> _thumbCache = new();
    private readonly LinkedList<long> _lru = new();
    private const int ThumbCacheCap = 500;

    public LibraryViewModel(LibraryIndexService library, SettingsService settings, DialogService dialog,
        GalleryViewModel? galleryVm = null)
    {
        _library = library; _settings = settings; _dialog = dialog;
        GalleryVm = galleryVm ?? Services.AppServices.Get<GalleryViewModel>();
        _imageLookup = Services.AppServices.Get<Pikura.Core.Services.ImageLookupService>();
        _browsePanelWidth = settings.Current.BrowsePanelWidth >= 200 ? settings.Current.BrowsePanelWidth : 450;
        _cardSize = settings.Current.CardSize;
        _showInfo = settings.Current.ShowInfo;
        _showTags = settings.Current.ShowTags;
        _includeR18 = settings.Current.R18Mode == Pikura.Core.Settings.R18Mode.Show;
        _dupThreshold = settings.Current.LibraryDuplicateThreshold;
        _ = RefreshAsync();
        _ = LoadChipsAsync();
    }

    private async Task RefreshAsync()
    {
        _queryGen++;
        _offset = 0;
        Items.Clear();
        HasMore = true;
        ResultsText = "";
        ResultsTotal = 0;
        ClearSelection(); // Items replaced — stale selection/details must not linger
        await LoadPageAsync(); // first page is unconditional — not gated by _loadingPage
        var st = await _library.Repository.GetStatsAsync();
        StatusLine = string.Format(LocalizationService.Instance["Library_FilesIndexed"], st.Files);
        TotalFiles = st.Files;
        RefreshFolders();
        // Files without pixiv metadata have no age rating, so hiding R-18 can't catch them — say so.
        var unrated = IncludeR18 ? 0 : await _library.Repository.CountUnratedAsync();
        R18Notice = unrated > 0 ? string.Format(LocalizationService.Instance["Library_UnratedNotice"], unrated) : null;
        var years = await _library.Repository.GetYearsAsync();
        // Rebuild only when the actual year list changed — and preserve the selection by
        // VALUE (the ComboBox's two-way SelectedIndex push otherwise loops back into RefreshAsync).
        var next = years.Select(y => y.ToString()).ToList();
        if (!YearOptions.Skip(1).SequenceEqual(next))
        {
            var selYear = YearIndex > 0 && YearIndex < YearOptions.Count ? YearOptions[YearIndex] : null;
            _updatingYears = true;
            try
            {
                YearOptions.Clear();
                YearOptions.Add(LocalizationService.Instance["Library_AnyYear"]);
                foreach (var y2 in next) YearOptions.Add(y2);
                YearIndex = selYear != null ? next.IndexOf(selYear) + 1 : 0;
                if (YearIndex <= 0) YearIndex = 0;
            }
            finally { _updatingYears = false; }
        }
    }

    private void RefreshFolders()
    {
        Folders.Clear();
        foreach (var f in _library.GetConfiguredFolders())
            Folders.Add(new LibraryFolderItem { Path = f, Exists = System.IO.Directory.Exists(f) });
        OnPropertyChanged(nameof(AnyFolderExists));
        OnPropertyChanged(nameof(IsCustomFolders));
    }

    /// <summary>Switches to a custom folder list (seeded from current configured folders)
    /// if the user hasn't customised yet.</summary>
    private List<string> EnsureCustomFolders()
    {
        if (_settings.Current.LibraryFolders is { } list) return list;
        var seed = _library.GetConfiguredFolders().ToList();
        _settings.Update(s => s.LibraryFolders = seed);
        return seed;
    }

    [RelayCommand]
    private async Task AddFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var list = EnsureCustomFolders();
        var full = System.IO.Path.GetFullPath(path);
        if (!list.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(full);
            _settings.Update(s => s.LibraryFolders = list);
        }
        await RefreshAsync();
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(LibraryFolderItem? item)
    {
        if (item == null) return;
        var list = EnsureCustomFolders();
        var removed = list.FirstOrDefault(x => string.Equals(x, item.Path, StringComparison.OrdinalIgnoreCase));
        if (removed != null)
        {
            list.Remove(removed);
            _settings.Update(s => s.LibraryFolders = list);
        }
        await _library.Repository.DeleteRowsUnderRootAsync(item.Path);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ResetFoldersAsync()
    {
        _settings.Update(s => s.LibraryFolders = null);
        await RefreshAsync();
        await RescanAsync();
    }

    [RelayCommand]
    private void OpenFolder(LibraryFolderItem? item)
    {
        if (item?.Exists == true && OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private LibraryQuery BuildQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        Artist = string.IsNullOrWhiteSpace(ArtistFilter) ? null : ArtistFilter.Trim(),
        Year = YearIndex > 0 && int.TryParse(YearOptions[YearIndex], out var y) ? y : null,
        DateFrom = DateFrom.HasValue ? DateOnly.FromDateTime(DateFrom.Value.Date) : null,
        DateTo = DateTo.HasValue ? DateOnly.FromDateTime(DateTo.Value.Date) : null,
        Orientation = (LibraryOrientation)OrientationIndex,
        PixivTags = [.. _reqPixivTags],
        AiTags = [.. _reqAiTags],
        MediaType = MediaTypeIndex switch { 1 => "image", 2 => "video", 3 => "animation", _ => null },
        IncludeR18 = IncludeR18,
        Sort = SortIndex switch
        {
            1 => LibrarySort.CreatedDesc, 2 => LibrarySort.Title, 3 => LibrarySort.Artist,
            4 => LibrarySort.DateDesc, 5 => LibrarySort.DateAsc, 6 => LibrarySort.SizeDesc,
            7 => LibrarySort.NameAsc, _ => LibrarySort.AddedDesc,
        },
        Offset = _offset,
        Limit = 200,
    };

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        // Scroll paging only — RefreshAsync loads the first page itself.
        if (_loadingPage || IsBusy || !HasMore) return;
        _loadingPage = true;
        var gen = _queryGen;
        try
        {
            var (items, total) = await _library.Repository.SearchAsync(BuildQuery());
            if (gen != _queryGen) return; // a newer query superseded this page — discard
            foreach (var f in items)
            {
                var vm = new LibraryItemViewModel(f);
                vm.UpdateImageHeight(CardSize, IsNaturalHeight);
                Items.Add(vm);
                _ = LoadThumbAsync(vm);
            }
            _offset += items.Count;
            HasMore = items.Count > 0 && _offset < total;
            ResultsText = string.Format(LocalizationService.Instance["Library_Results"], total);
            ResultsTotal = (int)Math.Min(total, int.MaxValue);
        }
        finally { _loadingPage = false; }
    }

    /// <summary>Unconditional first-page load for RefreshAsync — runs even while a scroll
    /// page is in flight (that page's results will be discarded by the gen check).</summary>
    private async Task LoadPageAsync()
    {
        var gen = _queryGen;
        try
        {
            var (items, total) = await _library.Repository.SearchAsync(BuildQuery());
            if (gen != _queryGen) return;
            foreach (var f in items)
            {
                var vm = new LibraryItemViewModel(f);
                vm.UpdateImageHeight(CardSize, IsNaturalHeight);
                Items.Add(vm);
                _ = LoadThumbAsync(vm);
            }
            _offset = items.Count;
            HasMore = items.Count > 0 && _offset < total;
            ResultsText = string.Format(LocalizationService.Instance["Library_Results"], total);
            ResultsTotal = (int)Math.Min(total, int.MaxValue);
        }
        catch { /* keep grid empty on failure — status line already reports it */ }
    }

    private readonly SemaphoreSlim _thumbGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private int ThumbDecodeSize => (int)Math.Clamp(CardSize * 2.0, 256, 640);

    private async Task LoadThumbAsync(LibraryItemViewModel vm)
    {
        try
        {
            if (_thumbCache.TryGetValue(vm.File.Id, out var cached)) { vm.Thumbnail = cached; return; }
            // Sized for the largest card at high-DPI (256px looked blurry on 1.5× screens).
            var target = ThumbDecodeSize;
            // Bounded so a page of 200 thumbnails can't occupy every pool thread at once.
            await _thumbGate.WaitAsync();
            Bitmap? bmp;
            try
            {
            bmp = await Task.Run(() =>
            {
                using var codec = SkiaSharp.SKCodec.Create(vm.File.Path);
                if (codec == null) return (Bitmap?)null;
                // Scaled decode to the nearest supported size ≥ target, then
                // high-quality resize — much cheaper than a full decode.
                // Fit the card width (cards are width-bound); very tall images cap at 3× height.
                var scale = Math.Min(1.0, Math.Min((double)target / codec.Info.Width, 3.0 * target / codec.Info.Height));
                var w = Math.Max(1, (int)(codec.Info.Width * scale));
                var h = Math.Max(1, (int)(codec.Info.Height * scale));
                var info = Pikura.Core.Services.Library.PerceptualHash.ScaledInfo(codec, codec.Info, Math.Min(w, h));
                using var small = new SkiaSharp.SKBitmap(info);
                if (codec.GetPixels(small.Info, small.GetPixels()) != SkiaSharp.SKCodecResult.Success) return (Bitmap?)null;
                using var final = w == small.Width && h == small.Height
                    ? small
                    : small.Resize(new SkiaSharp.SKImageInfo(w, h), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
                return BitmapInterop.SkiaToAvalonia(final!);
            });
            }
            finally { _thumbGate.Release(); }
            if (bmp != null)
            {
                _thumbCache[vm.File.Id] = bmp;
                _lru.AddLast(vm.File.Id);
                if (_lru.Count > ThumbCacheCap) { _thumbCache.Remove(_lru.First!.Value); _lru.RemoveFirst(); }
                vm.Thumbnail = bmp;
            }
        }
        catch { }
    }

    private void DebouncedSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var t = _searchCts.Token;
        Task.Delay(300, t).ContinueWith(async _ =>
        {
            if (t.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(RefreshAsync);
        }, TaskScheduler.Default);
    }

    partial void OnSearchTextChanged(string value) => DebouncedSearch();
    partial void OnArtistFilterChanged(string value) => DebouncedSearch();
    partial void OnIncludeR18Changed(bool value) => _ = RefreshAsync();
    partial void OnMediaTypeIndexChanged(int value) => _ = RefreshAsync();
    partial void OnSortIndexChanged(int value) => _ = RefreshAsync();
    partial void OnOrientationIndexChanged(int value) => _ = RefreshAsync();
    partial void OnYearIndexChanged(int value)
    {
        if (_updatingYears || value < 0) return; // rebuild artifact / deselect
        _ = RefreshAsync();
    }
    partial void OnDateFromChanged(DateTimeOffset? value) => _ = RefreshAsync();
    partial void OnDateToChanged(DateTimeOffset? value) => _ = RefreshAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = ArtistFilter = "";
        YearIndex = OrientationIndex = 0;
        DateFrom = DateTo = null;
    }
    partial void OnDupThresholdChanged(double value)
    {
        _settings.Current.LibraryDuplicateThreshold = (int)Math.Round(value);
    }

    private async Task LoadChipsAsync()
    {
        var ai = await _library.Repository.GetTopAiTagsAsync(12);
        foreach (var (t, _) in ai) AiTagChips.Add(t);
        var px = await _library.Repository.GetTopPixivTagsAsync(12);
        foreach (var (t, _) in px) PixivTagChips.Add(t);
    }

    [RelayCommand]
    private void TogglePixivTag(string tag)
    {
        if (_reqPixivTags.Remove(tag)) ActiveFilters.Remove(tag);
        else { _reqPixivTags.Add(tag); ActiveFilters.Add(tag); }
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ToggleAiTag(string tag)
    {
        if (_reqAiTags.Remove(tag)) ActiveFilters.Remove(tag);
        else { _reqAiTags.Add(tag); ActiveFilters.Add(tag); }
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void RemoveFilter(string tag)
    {
        _reqPixivTags.Remove(tag); _reqAiTags.Remove(tag); ActiveFilters.Remove(tag);
        _ = RefreshAsync();
    }

    // ── Item commands ────────────────────────────────────────────────────────

    [RelayCommand] private void OpenFile(LibraryItemViewModel? vm)
    { if (vm != null && OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(vm.File.Path) { UseShellExecute = true }); }

    [RelayCommand] private void ShowInFolder(LibraryItemViewModel? vm)
    { if (vm != null && OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{vm.File.Path}\""); }

    [RelayCommand] private void OpenOnPixiv(LibraryItemViewModel? vm)
    { if (vm?.File.ArtworkId != null) Process.Start(new ProcessStartInfo($"https://www.pixiv.net/artworks/{vm.File.ArtworkId}") { UseShellExecute = true }); }

    [RelayCommand] private async Task CopyPath(LibraryItemViewModel? vm)
    {
        if (vm == null) return;
        var top = global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow : null;
        if (top?.Clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(vm.File.Path));
        await top.Clipboard.SetDataAsync(dt);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task TagWithAi(LibraryItemViewModel? vm)
    {
        if (vm == null) return;
        if (!_library.IsTaggerModelInstalled(out _))
        {
            var ok = await _dialog.ShowConfirmationAsync(
                LocalizationService.Instance["Library_ModelRequired"],
                LocalizationService.Instance["Library_ModelRequiredMessage"]);
            if (!ok) return;
            await _library.EnsureTaggerModelAsync(null, CancellationToken.None);
        }
        try { await _library.TagSingleFileAsync(vm.File, CancellationToken.None); }
        catch { }
    }

    [RelayCommand]
    private async Task DeleteFile(LibraryItemViewModel? vm)
    {
        if (vm == null) return;
        var ok = await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_DeleteTitle"],
            string.Format(LocalizationService.Instance["Library_DeleteConfirm"], 1));
        if (!ok) return;
        await _library.DeleteFilesAsync([vm.File.Id], true);
        Items.Remove(vm);
    }

    // ── Open artwork in the shared inline viewer ───────────────────────────
    /// <summary>One viewer card per library file. The viewer shows the local file; the pixiv id
    /// (when known) only supplies title/artist. Files without one get a "local-{id}" card.</summary>
    private static ArtworkCardViewModel CardFor(LibraryItemViewModel item)
    {
        var f = item.File;
        return new ArtworkCardViewModel(new Pikura.Core.Models.ArtworkPreview
        {
            Id = f.ArtworkId ?? $"local-{f.Id}",
            Title = f.Title ?? System.IO.Path.GetFileName(f.Path),
            UserName = f.ArtistName ?? "",
            UserId = f.ArtistId ?? "",
            PageCount = 1,
        })
        { LocalPath = f.Path, LibraryFileId = f.Id, Thumbnail = item.Thumbnail };
    }

    private List<ArtworkCardViewModel> NavCards() => Items.Select(CardFor).ToList();

    /// <summary>Opens the clicked file in the shared side-panel viewer. Files without a pixiv id
    /// run the reverse search first (once per session); on a miss they open as a local card.</summary>
    [RelayCommand]
    private async Task OpenInViewer(LibraryItemViewModel? vm) => await OpenInViewerCoreAsync(vm, newTab: false);

    public async Task OpenInViewerCoreAsync(LibraryItemViewModel? vm, bool newTab)
    {
        if (vm == null) return;
        // Auto reverse search only with an API key — without one it would open a browser on every click.
        if (vm.File.ArtworkId == null && !_revSearchCache.ContainsKey(vm.File.Id)
            && !string.IsNullOrWhiteSpace(_settings.Current.SauceNaoApiKey) && !_sauceKeyRejected)
        {
            _autoSearch = true;
            try { await ReverseSearchAsync(vm); }
            finally { _autoSearch = false; }
            if (vm.File.ArtworkId != null) return; // a hit already opened the viewer
        }
        var list = NavCards();
        var target = list.FirstOrDefault(c => c.LibraryFileId == vm.File.Id) ?? CardFor(vm);
        Func<Task<IReadOnlyList<ArtworkCardViewModel>>> loadMore = async () =>
        {
            await LoadMoreAsync();
            return NavCards();
        };
        var total = Math.Max(ResultsTotal, list.Count);
        if (newTab)
        {
            GalleryVm.OpenInNewTab(target, list, total, loadMore, "Library");
            if (GalleryVm.SelectedViewerTab is { } t) { t.LocalPath = vm.File.Path; t.LibraryFileId = vm.File.Id; }
        }
        else
            GalleryVm.OpenInViewer(target, list, total, loadMore, "Library", vm.File.Path, vm.File.Id);
        ShowPreview = true;
        // Save the pixiv details for this file so its title/artist/date are known next time.
        // One request; the result also fills the open viewer card's title/artist.
        if (string.IsNullOrEmpty(vm.File.ArtistId) && vm.File.ArtworkId != null)
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await _library.EnrichFileAsync(vm.File)) return;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        target.Title = vm.File.Title ?? target.Title;
                        target.UserName = vm.File.ArtistName ?? "";
                        target.UserId = vm.File.ArtistId ?? "";
                        vm.NotifyMetadataChanged();
                    });
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Library] enrich failed: {ex.Message}"); }
            });
    }

    [RelayCommand] private void OpenSource(LibraryItemViewModel? vm)
    {
        if (vm == null || !OperatingSystem.IsWindows()) return;
        var src = vm.File.SourceUrl ?? (vm.File.ArtworkId != null ? $"https://www.pixiv.net/artworks/{vm.File.ArtworkId}" : null);
        if (src != null) Process.Start(new ProcessStartInfo(src) { UseShellExecute = true });
    }

    [RelayCommand] private async Task CopyOriginalUrl(LibraryItemViewModel? vm)
    {
        if (vm?.File.OriginalUrl == null) return;
        var top = global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow : null;
        if (top?.Clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(vm.File.OriginalUrl));
        await top.Clipboard.SetDataAsync(dt);
    }

    /// <summary>SauceNAO reverse search: hit → persist id + open viewer; miss → status line.</summary>
    [RelayCommand]
    private async Task ReverseSearchAsync(LibraryItemViewModel? vm)
    {
        if (vm == null) return;
        var id = vm.File.Id;
        if (_revSearchCache.TryGetValue(id, out var cached))
        {
            if (cached.Length > 0) { await OpenWithArtworkIdAsync(vm, cached); return; }
            RevSearchStatus = LocalizationService.Instance["Library_NoMatchSaucenao"];
            return;
        }
        if (_imageLookup == null) { RevSearchStatus = LocalizationService.Instance["Library_ReverseSearchUnavailable"]; return; }
        var apiKey = _settings.Current.SauceNaoApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // SauceNAO refuses anonymous API use — hand the image to the website instead.
            if (!vm.IsSelected) ApplyItemClick(vm, false, false);
            await SearchInBrowserAsync("saucenao");
            RevSearchStatus = LocalizationService.Instance["Library_NoSauceKey"];
            return;
        }
        RevSearchStatus = LocalizationService.Instance["Library_Searching"];
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var sk = SkiaSharp.SKBitmap.Decode(vm.File.Path);
                if (sk == null) return (byte[]?)null;
                var scale = Math.Min(1.0, 1200.0 / Math.Max(sk.Width, sk.Height));
                using var resized = scale < 1.0 ? sk.Resize(new SkiaSharp.SKImageInfo(
                        (int)(sk.Width * scale), (int)(sk.Height * scale)),
                    new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.None)) : null;
                using var img = SkiaSharp.SKImage.FromBitmap(resized ?? sk);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
                return (byte[]?)data.ToArray();
            });
            if (bytes == null) { RevSearchStatus = LocalizationService.Instance["Library_NoMatchSaucenao"]; return; }
            var result = await _imageLookup.LookupByImageBytesAsync(bytes, apiKey, CancellationToken.None);
            if (result?.PixivId is { } pid && pid.Length > 0)
            {
                _revSearchCache[id] = pid;
                RevSearchStatus = null;
                await OpenWithArtworkIdAsync(vm, pid, result);
            }
            else if (_imageLookup.LastError is { } err && err.StartsWith("SauceNAO 403", StringComparison.Ordinal))
            {
                // Key without API access (needs a paid SauceNAO plan): stop auto-searching, and
                // for an explicit request use the website instead.
                _sauceKeyRejected = true;
                if (_autoSearch) return;
                if (!vm.IsSelected) ApplyItemClick(vm, false, false);
                await SearchInBrowserAsync("saucenao");
                RevSearchStatus = LocalizationService.Instance["Library_SauceKeyRejected"];
            }
            else if (_imageLookup.LastError is { } err2)
            {
                // Errors (quota, network) aren't cached, so the user can retry later.
                RevSearchStatus = string.Format(LocalizationService.Instance["Library_ReverseSearchFailed"], err2);
            }
            else
            {
                _revSearchCache[id] = "";
                RevSearchStatus = LocalizationService.Instance["Library_NoMatchSaucenao"];
            }
        }
        catch (Exception ex)
        {
            RevSearchStatus = string.Format(LocalizationService.Instance["Library_ReverseSearchFailed"], ex.Message);
        }
    }

    private async Task OpenWithArtworkIdAsync(LibraryItemViewModel vm, string artworkId, Pikura.Core.Services.ImageLookupResult? hit = null)
    {
        vm.File.ArtworkId = artworkId;
        if (hit != null && vm.File.Title == null) vm.File.Title = hit.SourceTitle;
        // Best-effort persist — the row is updated via the repo's metadata path.
        try
        {
            if (vm.File.Id > 0)
                await _library.Repository.SetArtworkIdAsync(vm.File.Id, artworkId, hit?.SourceTitle, hit?.SourceUrl);
        }
        catch { }
        await OpenInViewer(vm);
    }

    /// <summary>Context menu: fetch this one file's pixiv details now.</summary>
    public async Task FetchInfoAsync(LibraryItemViewModel item)
    {
        try
        {
            NoticeLine = await _library.EnrichFileAsync(item.File) ? null : LocalizationService.Instance["Viewer_ArtistUnknown"];
            item.NotifyMetadataChanged();
        }
        catch (Exception ex) { NoticeLine = ex.Message; }
    }

    // ── Viewer action-bar helpers (Library tab) ─────────────────────────────
    /// <summary>Looks up a library file: loaded items first, then the repository.</summary>
    public async Task<LibraryFile?> GetFileAsync(long id)
    {
        var cached = Items.FirstOrDefault(i => i.File.Id == id)?.File;
        return cached ?? await _library.Repository.GetByIdAsync(id);
    }

    private async Task<LibraryItemViewModel?> ItemForAsync(long id)
    {
        var item = Items.FirstOrDefault(i => i.File.Id == id);
        if (item != null) return item;
        var f = await _library.Repository.GetByIdAsync(id);
        return f == null ? null : new LibraryItemViewModel(f);
    }

    public async Task ViewerReverseSearchAsync(long fileId)
    { if (await ItemForAsync(fileId) is { } i) await ReverseSearchAsync(i); }

    public async Task ViewerTagWithAiAsync(long fileId)
    { if (await ItemForAsync(fileId) is { } i) await TagWithAi(i); }

    /// <summary>Deletes after the existing confirmation; closes the viewer tab it came from.</summary>
    public async Task ViewerDeleteAsync(long fileId)
    {
        var item = await ItemForAsync(fileId);
        if (item == null) return;
        var ok = await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_DeleteTitle"],
            string.Format(LocalizationService.Instance["Library_DeleteConfirm"], 1));
        if (!ok) return;
        await _library.DeleteFilesAsync([item.File.Id], true);
        if (Items.FirstOrDefault(i => i.File.Id == fileId) is { } loaded) Items.Remove(loaded);
        var tab = GalleryVm.ViewerTabs.FirstOrDefault(t => t.LibraryFileId == fileId);
        if (tab != null) GalleryVm.ViewerTabs.Remove(tab);
    }

    /// <summary>Context-menu: open the artist in the Gallery tab.</summary>
    [RelayCommand]
    private async Task GoToArtist(LibraryItemViewModel? vm)
    {
        if (vm == null) return;
        try
        {
            if (string.IsNullOrEmpty(vm.File.ArtistId) && vm.File.ArtworkId != null)
                await _library.EnrichFileAsync(vm.File);
            var userId = vm.File.ArtistId;
            if (string.IsNullOrEmpty(userId))
            {
                NoticeLine = LocalizationService.Instance["Viewer_ArtistUnknown"];
                return;
            }
            if (global::Avalonia.Application.Current?.ApplicationLifetime is
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                && d.MainWindow is Views.MainWindow main)
            {
                main.LoadGalleryView();
                await GalleryVm.LoadArtistByIdCommand.ExecuteAsync(userId);
            }
        }
        catch (Exception ex) { NoticeLine = LocalizationService.Instance["Viewer_ArtistUnknown"] + " " + ex.Message; }
    }

    /// <summary>Context-menu submenu: copy file to clipboard, open upload page.</summary>
    [RelayCommand]
    private async Task SearchInBrowserAsync(string? site)
    {
        var vm = SelectedItems.Count == 1 ? SelectedItems[0] : Items.FirstOrDefault(i => i.IsSelected);
        if (vm == null || site == null) return;
        var url = site switch
        {
            "ascii2d" => "https://ascii2d.net/",
            "lens"    => "https://lens.google.com/",
            _         => "https://saucenao.com/",
        };
        // Copy the actual file so it pastes as an image upload on the site.
        var top = global::Avalonia.Application.Current?.ApplicationLifetime is
            global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d2
            ? d2.MainWindow : null;
        var provider = top?.StorageProvider;
        if (top?.Clipboard != null && provider != null)
        {
            var file = await provider.TryGetFileFromPathAsync(new System.Uri(vm.File.Path));
            var dt = new global::Avalonia.Input.DataTransfer();
            if (file != null)
                dt.Add(global::Avalonia.Input.DataTransferItem.Create(
                    global::Avalonia.Input.DataFormat.File, file));
            else
                dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(vm.File.Path));
            await top.Clipboard.SetDataAsync(dt);
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        RevSearchStatus = LocalizationService.Instance["Library_ImageCopiedPaste"];
    }

    // ── Selection ───────────────────────────────────────────────────────────
    // Called from the view's pointer handler with the pressed card + modifiers.
    public void ApplyItemClick(LibraryItemViewModel vm, bool ctrl, bool shift)
    {
        if (shift && _lastSelectedIndex >= 0)
        {
            var a = Items.IndexOf(vm);
            var b = _lastSelectedIndex;
            var lo = Math.Min(a, b); var hi = Math.Max(a, b);
            for (var i = lo; i <= hi; i++) Items[i].IsSelected = true;
        }
        else if (ctrl)
        {
            vm.IsSelected = !vm.IsSelected;
            _lastSelectedIndex = Items.IndexOf(vm);
        }
        else
        {
            foreach (var i in Items) i.IsSelected = ReferenceEquals(i, vm);
            _lastSelectedIndex = Items.IndexOf(vm);
        }
        SelectionChanged();
    }

    public void SelectAll() { foreach (var i in Items) i.IsSelected = true; SelectionChanged(); }
    public void ClearSelection() { foreach (var i in Items) i.IsSelected = false; SelectionChanged(); }

    private void SelectionChanged()
    {
        var sel = SelectedItems;
        SelectedCount = sel.Count;
        OnPropertyChanged(nameof(DetailsSingle));
        SelectionSummary = sel.Count switch
        {
            0 => LocalizationService.Instance["Library_SelectHint"],
            1 => sel[0].DisplayName,
            _ => string.Format(LocalizationService.Instance["Library_SelectedCount"], sel.Count),
        };
        _ = LoadDetailsPreviewAsync(sel.Count == 1 ? sel[0] : null);
    }

    private async Task LoadDetailsPreviewAsync(LibraryItemViewModel? vm)
    {
        if (vm == null) { DetailsPreview = null; return; }
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var sk = SkiaSharp.SKBitmap.Decode(vm.File.Path);
                if (sk == null) return (Bitmap?)null;
                var scale = Math.Min(1.0, 800.0 / Math.Max(sk.Width, sk.Height));
                var w = Math.Max(1, (int)(sk.Width * scale)); var h = Math.Max(1, (int)(sk.Height * scale));
                using var resized = sk.Resize(new SkiaSharp.SKImageInfo(w, h),
                    new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.None));
                if (resized == null) return (Bitmap?)null;
                using var img = SkiaSharp.SKImage.FromBitmap(resized);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                return (Bitmap?)new Bitmap(new System.IO.MemoryStream(data.ToArray()));
            });
            if (DetailsSingle == vm) DetailsPreview = bmp;
        }
        catch { DetailsPreview = null; }
    }

    [RelayCommand] private async Task OpenSelected()
    {
        var sel = SelectedItems;
        if (sel.Count > 10 && !await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_OpenFilesTitle"],
            string.Format(LocalizationService.Instance["Library_OpenFilesConfirm"], sel.Count)))
            return;
        if (!OperatingSystem.IsWindows()) return;
        foreach (var vm in sel)
            Process.Start(new ProcessStartInfo(vm.File.Path) { UseShellExecute = true });
    }

    [RelayCommand] private void OpenSingleFolder(LibraryItemViewModel? vm)
    {
        var dir = vm == null ? null : System.IO.Path.GetDirectoryName(vm.File.Path);
        if (dir != null && OperatingSystem.IsWindows() && System.IO.Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    [RelayCommand] private async Task ShowSelectedInFolders()
    {
        var dirs = SelectedItems.Select(v => System.IO.Path.GetDirectoryName(v.File.Path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(d => d != null).ToList();
        if (dirs.Count > 10 && !await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_OpenFoldersTitle"],
            string.Format(LocalizationService.Instance["Library_OpenFoldersConfirm"], dirs.Count)))
            return;
        if (OperatingSystem.IsWindows())
            foreach (var d in dirs)
                Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
    }

    [RelayCommand] private async Task CopySelectedPaths()
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        var top = global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow : null;
        if (top?.Clipboard == null) return;
        var dt = new global::Avalonia.Input.DataTransfer();
        dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(string.Join(Environment.NewLine, sel.Select(v => v.File.Path))));
        await top.Clipboard.SetDataAsync(dt);
    }

    [RelayCommand] private async Task DeleteSelected()
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        var ok = await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_DeleteTitle"],
            string.Format(LocalizationService.Instance["Library_DeleteConfirm"], sel.Count));
        if (!ok) return;
        await _library.DeleteFilesAsync(sel.Select(v => v.File.Id).ToArray(), true);
        foreach (var v in sel) Items.Remove(v);
        ClearSelection();
    }

    // ── Status bar actions ───────────────────────────────────────────────────

    private async Task RunWorkAsync(string phase, Func<IProgress<LibraryScanProgress>, CancellationToken, Task> work)
    {
        if (_workCts != null) return;
        _workCts = new CancellationTokenSource();
        IsBusy = true;
        NoticeLine = null;
        var finished = false; // Progress<T> callbacks posted after completion must not overwrite the clear
        try
        {
            var prog = new Progress<LibraryScanProgress>(p =>
            {
                if (finished) return;
                ProgressLine = string.Format(LocalizationService.Instance["Library_WorkProgress"],
                    phase, p.Done, p.Total, System.IO.Path.GetFileName(p.CurrentPath ?? ""));
            });
            await work(prog, _workCts.Token);
            finished = true;
            ProgressLine = null;
            LastScanText = string.Format(LocalizationService.Instance["Library_LastScanFmt"], DateTime.Now.ToString("g"));
            await RefreshAsync();
            // Without this an empty result looks like the button did nothing.
            // (A missing folder is already explained by the empty-state panel.)
            if (TotalFiles == 0 && AnyFolderExists)
                NoticeLine = LocalizationService.Instance["Library_ScanFoundNothing"];
        }
        catch (OperationCanceledException) { finished = true; ProgressLine = null; }
        catch (Exception ex)
        {
            // Show it instead of letting it escape the command (that became a crash report).
            finished = true; ProgressLine = null;
            NoticeLine = ex.Message;
        }
        finally { IsBusy = false; _workCts = null; }
    }

    [RelayCommand] private async Task RescanAsync() =>
        await RunWorkAsync(LocalizationService.Instance["Library_Scanning"], (p, ct) => _library.ScanAsync(p, ct));

    [RelayCommand] private async Task FetchMissingInfoAsync() =>
        await RunWorkAsync(LocalizationService.Instance["Library_Enriching"], (p, ct) => _library.EnrichMissingMetadataAsync(p, ct));

    [RelayCommand]
    private async Task TagUntaggedAsync()
    {
        if (!_library.IsTaggerModelInstalled(out _))
        {
            var ok = await _dialog.ShowConfirmationAsync(
                LocalizationService.Instance["Library_ModelRequired"],
                LocalizationService.Instance["Library_ModelRequiredMessage"]);
            if (!ok) return;
            await _library.EnsureTaggerModelAsync(null, CancellationToken.None);
            if (!_library.IsTaggerModelInstalled(out _)) return;
        }
        await RunWorkAsync(LocalizationService.Instance["Library_Tagging"], (p, ct) => _library.TagUntaggedAsync(p, ct));
    }

    [RelayCommand] private void CancelWork() => _workCts?.Cancel();

    // ── Duplicates tab ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        DupGroups.Clear();
        try
        {
            var threshold = (int)Math.Round(DupThreshold);
            var exact = await Task.Run(() => _library.Repository.GetExactDuplicateGroupsAsync());
            var near = await Task.Run(() => _library.Repository.GetNearDuplicateGroupsAsync(threshold));
            var pending = await Task.Run(() => _library.Repository.GetPendingRehashCountAsync());
            PendingRehashText = pending > 0
                ? string.Format(LocalizationService.Instance["Library_Refingerprinting"], pending)
                : null;
            foreach (var g in exact) { var gv = new DuplicateGroupViewModel(g); _ = LoadDupThumbs(gv); DupGroups.Add(gv); }
            foreach (var g in near) { var gv = new DuplicateGroupViewModel(g); _ = LoadDupThumbs(gv); DupGroups.Add(gv); }
            DupScanDone = true;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadDupThumbs(DuplicateGroupViewModel gv)
    {
        foreach (var r in gv.Rows)
        {
            var ivm = new LibraryItemViewModel(r.File) { };
            await LoadThumbAsync(ivm);
            r.Thumbnail = ivm.Thumbnail;
        }
    }

    [RelayCommand] private void KeepBest(DuplicateGroupViewModel? g) => g?.KeepBest();

    [RelayCommand]
    private async Task DeleteMarkedAsync()
    {
        var ids = DupGroups.SelectMany(g => g.Rows).Where(r => r.IsMarked).Select(r => r.File.Id).ToList();
        if (ids.Count == 0) return;
        var ok = await _dialog.ShowConfirmationAsync(
            LocalizationService.Instance["Library_DeleteTitle"],
            string.Format(LocalizationService.Instance["Library_DeleteConfirmRecycle"], ids.Count));
        if (!ok) return;
        var deleted = await _library.DeleteFilesAsync(ids, true);
        foreach (var g in DupGroups.ToList())
        {
            foreach (var r in g.Rows.Where(r => r.IsMarked && ids.Contains(r.File.Id)).ToList())
                g.Rows.Remove(r);
        }
        for (var i = DupGroups.Count - 1; i >= 0; i--)
            if (DupGroups[i].Rows.Count < 2) DupGroups.RemoveAt(i);
    }

    /// <summary>Called when the browse grid scrolls near the bottom.</summary>
    public void OnScrolledToEnd() => _ = LoadMoreAsync();
}

