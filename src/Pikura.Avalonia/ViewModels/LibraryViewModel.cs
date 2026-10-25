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

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _artistFilter = "";
    [ObservableProperty] private bool _includeR18;
    [ObservableProperty] private int _mediaTypeIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string? _progressLine;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMore = true;
    [ObservableProperty] private long _totalFiles;
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private double _dupThreshold;
    [ObservableProperty] private bool _dupScanDone;

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
    private readonly Dictionary<long, Bitmap> _thumbCache = new();
    private readonly LinkedList<long> _lru = new();
    private const int ThumbCacheCap = 500;

    public LibraryViewModel(LibraryIndexService library, SettingsService settings, DialogService dialog)
    {
        _library = library; _settings = settings; _dialog = dialog;
        _includeR18 = settings.Current.R18Mode == Pikura.Core.Settings.R18Mode.Show;
        _dupThreshold = settings.Current.LibraryDuplicateThreshold;
        _ = RefreshAsync();
        _ = LoadChipsAsync();
    }

    private async Task RefreshAsync()
    {
        _offset = 0;
        Items.Clear();
        HasMore = true;
        await LoadMoreAsync();
        var st = await _library.Repository.GetStatsAsync();
        StatusLine = string.Format(LocalizationService.Instance["Library_FilesIndexed"], st.Files);
        TotalFiles = st.Files;
    }

    private LibraryQuery BuildQuery() => new()
    {
        Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        ArtistId = string.IsNullOrWhiteSpace(ArtistFilter) ? null : ArtistFilter.Trim(),
        PixivTags = [.. _reqPixivTags],
        AiTags = [.. _reqAiTags],
        MediaType = MediaTypeIndex switch { 1 => "image", 2 => "video", 3 => "animation", _ => null },
        IncludeR18 = IncludeR18,
        Sort = SortIndex switch { 1 => LibrarySort.CreatedDesc, 2 => LibrarySort.Title, 3 => LibrarySort.Artist, _ => LibrarySort.AddedDesc },
        Offset = _offset,
        Limit = 200,
    };

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || !HasMore) return;
        IsBusy = true;
        try
        {
            var (items, total) = await _library.Repository.SearchAsync(BuildQuery());
            foreach (var f in items)
            {
                var vm = new LibraryItemViewModel(f);
                Items.Add(vm);
                _ = LoadThumbAsync(vm);
            }
            _offset += items.Count;
            HasMore = items.Count > 0 && _offset < total;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadThumbAsync(LibraryItemViewModel vm)
    {
        try
        {
            if (_thumbCache.TryGetValue(vm.File.Id, out var cached)) { vm.Thumbnail = cached; return; }
            var bmp = await Task.Run(() =>
            {
                using var codec = SkiaSharp.SKCodec.Create(vm.File.Path);
                if (codec == null) return (Bitmap?)null;
                var scale = codec.Info.Height > codec.Info.Width
                    ? 256.0 / codec.Info.Height : 256.0 / codec.Info.Width;
                using var full = new SkiaSharp.SKBitmap(codec.Info);
                if (codec.GetPixels(full.Info, full.GetPixels()) != SkiaSharp.SKCodecResult.Success) return (Bitmap?)null;
                var w = Math.Max(1, (int)(codec.Info.Width * scale));
                var h = Math.Max(1, (int)(codec.Info.Height * scale));
                using var small = full.Resize(new SkiaSharp.SKImageInfo(w, h), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
                return BitmapInterop.SkiaToAvalonia(small!);
            });
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

    // ── Status bar actions ───────────────────────────────────────────────────

    private async Task RunWorkAsync(string phase, Func<IProgress<LibraryScanProgress>, CancellationToken, Task> work)
    {
        if (_workCts != null) return;
        _workCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var prog = new Progress<LibraryScanProgress>(p =>
                ProgressLine = string.Format(LocalizationService.Instance["Library_WorkProgress"],
                    phase, p.Done, p.Total, System.IO.Path.GetFileName(p.CurrentPath ?? "")));
            await work(prog, _workCts.Token);
            ProgressLine = null;
            LastScanText = DateTime.Now.ToString("g");
            await RefreshAsync();
        }
        catch (OperationCanceledException) { ProgressLine = null; }
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
