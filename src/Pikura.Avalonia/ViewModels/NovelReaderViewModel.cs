using System;
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
    }

    public event EventHandler? CloseRequested;

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
}
