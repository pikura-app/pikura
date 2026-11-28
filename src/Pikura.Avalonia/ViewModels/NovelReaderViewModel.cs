using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>Reads a pixiv novel inside Pikura (Rankings side panel and the novel window).</summary>
public partial class NovelSeriesItem(NovelSeriesEntry entry) : ObservableObject
{
    public NovelSeriesEntry Entry { get; } = entry;
    public string Label => $"#{Entry.Order}  {Entry.Title}";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCurrent;
}

public partial class NovelReaderViewModel : ObservableObject
{
    private static double s_fontSize = 16;
    private readonly PixivClient _client;
    private readonly PixivImageLoader _loader;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _cts;

    public NovelReaderViewModel(PixivClient client, PixivImageLoader loader, SettingsService settings)
    {
        _client = client;
        _loader = loader;
        _settings = settings;
        Comments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CommentsLabel));
    }

    partial void OnCanLoadMoreCommentsChanged(bool value) => OnPropertyChanged(nameof(CommentsLabel));

    public event EventHandler? CloseRequested;
    public event EventHandler? ExpandRequested;
    public event EventHandler? FullscreenRequested;

    public ObservableCollection<PixivComment> Comments { get; } = [];
    public ObservableCollection<NovelSeriesItem> SeriesItems { get; } = [];
    public string[] ExportFormats { get; } = ["TXT", "EPUB", "PDF", "DOCX"];
    [ObservableProperty] private bool _showComments;
    [ObservableProperty] private bool _showSeries;
    [ObservableProperty] private bool _canLoadMoreComments;
    [ObservableProperty] private int _exportFormatIndex = 1;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string? _lastExportPath;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _canExpand = true;
    [ObservableProperty] private bool _isActiveTab;
    public string CommentsLabel => Comments.Count > 0
        ? $"{Pikura.Avalonia.Localization.LocalizationService.Instance["Novel_Comments"]} ({Comments.Count}{(CanLoadMoreComments ? "+" : "")})"
        : Pikura.Avalonia.Localization.LocalizationService.Instance["Novel_Comments"];
    public bool ShowText => !ShowComments && !ShowSeries;
    public bool HasDownloadStatus => DownloadStatus.Length > 0;
    public bool HasLastExport => LastExportPath is not null;
    private string? _seriesLoadedFor;

    [ObservableProperty] private NovelText? _novel;
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<NovelBlock>> _pages = [];
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private double _fontSize = s_fontSize;

    public PixivClient Client => _client;
    public PixivImageLoader Loader => _loader;
    public string Title => Novel?.Title ?? "";
    public string Author => Novel?.UserName ?? "";
    public string Series => Novel?.SeriesTitle ?? "";
    public bool HasSeries => !string.IsNullOrEmpty(Novel?.SeriesTitle);
    public IReadOnlyList<string> Tags => Novel?.Tags ?? [];
    public bool IsR18 => Novel?.XRestrict > 0;
    public bool BlurCover => IsR18 && _settings.Current.BlurR18Content;
    public string Meta => Novel is not { } n ? "" : string.Join("  ·  ", new[]
    {
        n.CharacterCount > 0 ? string.Format(LocalizationService.Instance["Novel_Characters"], n.CharacterCount) : null,
        n.ReadingTimeSeconds > 0 ? string.Format(LocalizationService.Instance["Novel_ReadingTime"], (int)Math.Ceiling(n.ReadingTimeSeconds / 60.0)) : null,
        $"♥ {n.LikeCount:N0}", $"🔖 {n.BookmarkCount:N0}", $"👁 {n.ViewCount:N0}",
        n.CreateDate.Length >= 10 ? n.CreateDate[..10] : null,
    }.Where(s => s is not null));
    public string PageLabel => Pages.Count <= 1 ? "" : string.Format(LocalizationService.Instance["Novel_Page"], PageIndex + 1, Pages.Count);
    public bool HasPages => Pages.Count > 1;
    public bool CanPrevPage => PageIndex > 0;
    public bool CanNextPage => PageIndex < Pages.Count - 1;
    public bool HasPrevEpisode => Novel?.PrevId is not null;
    public bool HasNextEpisode => Novel?.NextId is not null;
    public bool HasStatus => Status.Length > 0;
    public string Url => Novel is { Id.Length: > 0 } n ? $"https://www.pixiv.net/novel/show.php?id={n.Id}" : _url;
    private string _url = "";

    partial void OnNovelChanged(NovelText? value)
    {
        foreach (var p in new[] { nameof(Title), nameof(Author), nameof(Series), nameof(HasSeries), nameof(Tags), nameof(IsR18),
                     nameof(BlurCover), nameof(Meta), nameof(HasPrevEpisode), nameof(HasNextEpisode), nameof(Url) })
            OnPropertyChanged(p);
    }

    partial void OnPagesChanged(IReadOnlyList<IReadOnlyList<NovelBlock>> value) => NotifyPaging();
    partial void OnPageIndexChanged(int value) => NotifyPaging();
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnDownloadStatusChanged(string value) => OnPropertyChanged(nameof(HasDownloadStatus));
    partial void OnLastExportPathChanged(string? value) => OnPropertyChanged(nameof(HasLastExport));
    partial void OnShowCommentsChanged(bool value) { if (value) ShowSeries = false; OnPropertyChanged(nameof(ShowText)); if (value && Comments.Count == 0) _ = LoadCommentsAsync(false); }
    partial void OnShowSeriesChanged(bool value) { if (value) ShowComments = false; OnPropertyChanged(nameof(ShowText)); if (value) _ = LoadSeriesAsync(); }
    partial void OnFontSizeChanged(double value) => s_fontSize = value;

    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(CanPrevPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    public async Task LoadAsync(string novelId)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _url = $"https://www.pixiv.net/novel/show.php?id={novelId}";
        IsLoading = true;
        Status = "";
        Cover = null;
        try
        {
            var novel = await _client.GetNovelTextAsync(novelId, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (novel is null)
            {
                Novel = null;
                Pages = [];
                Status = LocalizationService.Instance["Novel_LoadFailed"];
                return;
            }
            if (novel.XRestrict > 0 && _settings.Current.R18Mode == R18Mode.Off)
            {
                Novel = novel with { Content = "" };
                Pages = [];
                Status = LocalizationService.Instance["Novel_R18Hidden"];
                return;
            }
            Novel = novel;
            Pages = NovelMarkup.Parse(novel.Content);
            PageIndex = 0;
            Comments.Clear();
            CanLoadMoreComments = false;
            if (ShowComments) _ = LoadCommentsAsync(false);
            foreach (var item in SeriesItems) item.IsCurrent = item.Entry.Id == novel.Id;
            if (novel.CoverUrl is { } c)
            {
                using var sk = await _loader.FetchBitmapAsync(c, ThumbnailSize.Medium, cts.Token);
                if (sk is not null && !cts.IsCancellationRequested) Cover = BitmapInterop.SkiaToAvalonia(sk);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cts.IsCancellationRequested) Status = LocalizationService.Instance["Novel_LoadFailed"];
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
    }

    [RelayCommand] private void PrevPage() { if (CanPrevPage) PageIndex--; }
    [RelayCommand] private void NextPage() { if (CanNextPage) PageIndex++; }
    public void GoToPage(int oneBased) { if (oneBased >= 1 && oneBased <= Pages.Count) PageIndex = oneBased - 1; }
    [RelayCommand] private void FontSmaller() => FontSize = Math.Max(11, FontSize - 1);
    [RelayCommand] private void FontLarger() => FontSize = Math.Min(30, FontSize + 1);
    [RelayCommand] private Task PrevEpisodeAsync() => Novel?.PrevId is { } id ? LoadAsync(id) : Task.CompletedTask;
    [RelayCommand] private Task NextEpisodeAsync() => Novel?.NextId is { } id ? LoadAsync(id) : Task.CompletedTask;
    [RelayCommand] private void Close() { _cts?.Cancel(); CloseRequested?.Invoke(this, EventArgs.Empty); }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (Url.Length == 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand] private void Expand() => ExpandRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Fullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleComments() => ShowComments = !ShowComments;
    [RelayCommand] private void ToggleSeries() => ShowSeries = !ShowSeries;
    [RelayCommand] private Task LoadMoreCommentsAsync() => LoadCommentsAsync(true);

    private async Task LoadCommentsAsync(bool more)
    {
        if (Novel is not { } n) return;
        try
        {
            var r = await _client.GetNovelCommentsAsync(n.Id, more ? Comments.Count : 0, 30);
            if (Novel?.Id != n.Id || r is null) return;
            if (!more) Comments.Clear();
            foreach (var c in r.Comments) Comments.Add(c);
            CanLoadMoreComments = r.HasNext;
        }
        catch { }
    }

    private async Task LoadSeriesAsync()
    {
        if (Novel?.SeriesId is not { } sid || _seriesLoadedFor == sid) return;
        _seriesLoadedFor = sid;
        SeriesItems.Clear();
        try
        {
            foreach (var e in await _client.GetNovelSeriesContentAsync(sid))
                SeriesItems.Add(new NovelSeriesItem(e) { IsCurrent = e.Id == Novel?.Id });
        }
        catch { _seriesLoadedFor = null; }
    }

    [RelayCommand] private Task OpenSeriesItemAsync(NovelSeriesItem item) { ShowSeries = false; return LoadAsync(item.Entry.Id); }
    [RelayCommand] private void SelectAllSeries() { var all = SeriesItems.All(i => i.IsSelected); foreach (var i in SeriesItems) i.IsSelected = !all; }

    [RelayCommand] private Task DownloadChapterAsync() => Novel is { } n ? ExportAsync([n.Id], n.Title) : Task.CompletedTask;
    [RelayCommand] private Task DownloadSelectedAsync() =>
        ExportAsync(SeriesItems.Where(i => i.IsSelected).Select(i => i.Entry.Id).ToList(), Novel?.SeriesTitle ?? Title);

    [RelayCommand]
    private async Task DownloadSeriesAsync()
    {
        if (Novel?.SeriesId is null) { await DownloadChapterAsync(); return; }
        await LoadSeriesAsync();
        await ExportAsync(SeriesItems.Select(i => i.Entry.Id).ToList(), Novel?.SeriesTitle ?? Title);
    }

    /// <summary>Queues the chapters as a novel job in the normal download queue (Jobs).</summary>
    private async Task ExportAsync(IReadOnlyList<string> ids, string title)
    {
        if (ids.Count == 0 || IsDownloading) return;
        IsDownloading = true;
        LastExportPath = null;
        try
        {
            var coordinator = AppServices.Get<DownloadCoordinator>();
            var format = (NovelExportFormat)ExportFormatIndex;
            var name = ids.Count == 1 && Novel is { } n && ids[0] == n.Id ? n.Title : title;
            var target = new DownloadTarget
            {
                TargetId = DownloadCoordinator.NovelTargetId(format, ids),
                Name = name,
                UserName = Novel?.UserName,
                UserId = Novel?.UserId,
                ThumbnailUrl = Novel?.CoverUrl,
                Type = TargetType.Novel,
            };
            var job = await coordinator.CreateJobAsync(DownloadJobType.ImageId,
                $"{name} ({ExportFormats[ExportFormatIndex]})", [target],
                new SettingsOverride { UseGlobalSettings = true }, startImmediately: true);
            DownloadStatus = LocalizationService.F("Novel_Queued", ids.Count);
            coordinator.SubscribeToProgress(job.Id, new Progress<JobProgress>(p =>
            {
                if (p.Status == JobStatus.Completed && job.OutputFolder is { } folder)
                {
                    LastExportPath = folder;
                    DownloadStatus = string.Format(LocalizationService.Instance["Novel_Saved"], folder);
                }
                else if (p.Status == JobStatus.Failed)
                    DownloadStatus = p.Message ?? LocalizationService.Instance["Novel_LoadFailed"];
            }));
        }
        catch (Exception ex)
        {
            DownloadStatus = ex.Message;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ShowExport()
    {
        if (LastExportPath is not { } p) return;
        try
        {
            if (Directory.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
            else if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(p)!) { UseShellExecute = true });
        }
        catch { }
    }
}
