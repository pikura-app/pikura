using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Localization;
using Pikura.Avalonia.Services;
using Pikura.Core.Services;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.ViewModels;

/// <summary>An article entry with a thumbnail (lists, sidebar, related articles).</summary>
public partial class DicArticleCard : ObservableObject
{
    public string Title { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Meta { get; init; } = "";
    public IReadOnlyList<string> Related { get; init; } = [];
    public string? ImageUrl { get; init; }
    public DicLibraryEntry? Entry { get; init; }
    public bool HasEntry => Entry is not null;
    public bool HasSummary => Summary.Length > 0;
    public bool HasRelated => Related.Count > 0;
    [ObservableProperty] private Bitmap? _thumbnail;
    public bool HasThumbnail => Thumbnail is not null;
    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    public async Task LoadThumbnailAsync(PixivImageLoader loader)
    {
        // Only pixiv artwork thumbnails; the "no image" placeholder is an SVG on s.pximg.net.
        if (ImageUrl is null || !ImageUrl.Contains("i.pximg.net", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var sk = await loader.FetchBitmapAsync(ImageUrl, ThumbnailSize.Small);
            if (sk is null) return;
            Thumbnail = BitmapInterop.SkiaToAvalonia(sk);
        }
        catch { }
    }
}

public partial class DicCategory(string name, string path) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    [ObservableProperty] private bool _isSelected;
}

public partial class DicViewModel : ObservableObject
{
    private static readonly string[] EnCategories =
        ["Anime", "Manga", "Novel", "Game", "Figure", "Music", "Art", "Design", "General", "Person", "Character", "Quote", "Event", "Doujin"];
    private static readonly string[] JaCategories =
        ["アニメ", "マンガ", "ラノベ", "ゲーム", "フィギュア", "音楽", "アート", "デザイン", "一般", "人物", "キャラクター", "セリフ", "イベント", "同人サークル"];

    private readonly DicService _dic;
    private readonly PixivImageLoader _loader;
    private readonly SettingsService _settings;
    private readonly DicLibraryService _library;
    private readonly Stack<string> _history = new();
    private int _requestId;
    private bool _sidebarLoaded;

    public DicViewModel(DicService dic, PixivImageLoader loader, SettingsService settings, DicLibraryService library)
    {
        _library = library;
        _dic = dic;
        _loader = loader;
        _settings = settings;
        RebuildCategories();
    }

    public DicService Service => _dic;

    [ObservableProperty] private string _language = "en";
    [ObservableProperty] private string _topic = "popular";
    [ObservableProperty] private string? _category;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showMonthly;
    [ObservableProperty] private bool _isArticleOpen;
    [ObservableProperty] private string _articleTag = "";
    /// <summary>Language of the article actually shown (an English request may fall back to Japanese).</summary>
    [ObservableProperty] private string _articleLang = "en";
    [ObservableProperty] private string _articleNote = "";
    [ObservableProperty] private bool _isArticleSaved;
    public bool IsLocalTopic => Topic is "saved" or "history";
    public bool IsHistoryTopic => Topic == "history";
    partial void OnTopicChanged(string value) { OnPropertyChanged(nameof(IsLocalTopic)); OnPropertyChanged(nameof(IsHistoryTopic)); OnPropertyChanged(nameof(CanNextPage)); }

    /// <summary>The last <c>/get_article</c> body; the view renders its node tree.</summary>
    [ObservableProperty] private JsonElement? _article;
    [ObservableProperty] private JsonElement? _articleInfo;

    public ObservableCollection<DicCategory> Categories { get; } = [];
    public ObservableCollection<string> Attentions { get; } = [];
    public ObservableCollection<DicArticleCard> Articles { get; } = [];
    public ObservableCollection<DicArticleCard> WeeklyRanking { get; } = [];
    public ObservableCollection<DicArticleCard> MonthlyRanking { get; } = [];
    public ObservableCollection<DicArticleCard> UpdatedArticles { get; } = [];
    public ObservableCollection<DicArticleCard> NewArticles { get; } = [];

    public bool IsEnglish => Language == "en";
    public bool IsJapanese => Language == "ja";
    public bool CanGoBack => IsArticleOpen;
    public bool CanPrevPage => Page > 1 && !IsArticleOpen;
    public bool CanNextPage => Articles.Count > 0 && !IsArticleOpen && Topic != "updates" && !IsLocalTopic;
    public bool IsListOpen => !IsArticleOpen;
    public bool HasStatus => Status.Length > 0;
    public bool BlurSensitive => _settings.Current.BlurR18Content;
    private string Prefix => IsEnglish ? "/en" : "";

    partial void OnLanguageChanged(string value)
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsJapanese));
        RebuildCategories();
        _sidebarLoaded = false;
    }

    partial void OnIsArticleOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsListOpen));
        OnPropertyChanged(nameof(CanPrevPage));
        OnPropertyChanged(nameof(CanNextPage));
    }

    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(CanPrevPage));
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnCategoryChanged(string? value)
    {
        foreach (var c in Categories) c.IsSelected = c.Path == value;
    }

    private void RebuildCategories()
    {
        Categories.Clear();
        foreach (var name in IsEnglish ? EnCategories : JaCategories)
            Categories.Add(new DicCategory(name, name) { IsSelected = name == Category });
    }

    /// <summary>First load when the tab opens.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Articles.Count == 0 && !IsLoading && !IsArticleOpen) await LoadListAsync();
    }

    [RelayCommand]
    private async Task SelectTopicAsync(string topic)
    {
        Topic = topic;
        Category = null;
        Page = 1;
        IsArticleOpen = false;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task HomeAsync()
    {
        Topic = "popular";
        Category = null;
        SearchText = "";
        Page = 1;
        _history.Clear();
        IsArticleOpen = false;
        Article = null;
        ArticleInfo = null;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task SelectCategoryAsync(DicCategory category)
    {
        Topic = "category";
        Category = category.Path;
        Page = 1;
        IsArticleOpen = false;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = SearchText.Trim();
        if (q.Length == 0) return;
        Topic = "search";
        Category = null;
        Page = 1;
        IsArticleOpen = false;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        Page++;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadListAsync();
    }

    [RelayCommand]
    private async Task SetLanguageAsync(string lang)
    {
        if (Language == lang) return;
        Language = lang;
        if (Topic == "category") { Topic = "popular"; Category = null; }
        Page = 1;
        if (IsArticleOpen && ArticleTag.Length > 0) await LoadArticleAsync(ArticleTag, pushHistory: false);
        else await LoadListAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _sidebarLoaded = false;
        if (IsArticleOpen) await LoadArticleAsync(ArticleTag, pushHistory: false);
        else await LoadListAsync();
    }

    [RelayCommand]
    private void ToggleRankingPeriod(string period) => ShowMonthly = period == "monthly";

    [RelayCommand]
    public async Task OpenArticleAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        await LoadArticleAsync(tag, pushHistory: IsArticleOpen && ArticleTag.Length > 0);
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_history.Count > 0)
        {
            await LoadArticleAsync(_history.Pop(), pushHistory: false);
            return;
        }
        IsArticleOpen = false;
        Article = null;
        ArticleInfo = null;
        Status = "";
        if (Articles.Count == 0) await LoadListAsync();
    }

    public string CurrentUrl => IsArticleOpen
        ? $"{DicService.Origin}{(ArticleLang == "en" ? "/en" : "")}/a/{Uri.EscapeDataString(ArticleTag)}"
        : DicService.Origin + ListPath();

    /// <summary>The Japanese tag for pixiv searches (English articles carry it as translatedTagName).</summary>
    public string? PixivTag
    {
        get
        {
            if (Article is not { } a) return null;
            if (ArticleLang == "en" && a.TryGetProperty("translatedTagName", out var t) && t.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(t.GetString()))
                return t.GetString();
            return a.TryGetProperty("tagName", out var n) ? n.GetString() : ArticleTag;
        }
    }

    private string ListPath()
    {
        var page = Page > 1 ? Page : 0;
        string WithPage(string path) => page == 0 ? path : path + (path.Contains('?') ? "&" : "?") + "page=" + page;
        return Topic switch
        {
            "updates" => WithPage(Prefix + "/newupdates"),
            "new" => WithPage(Prefix + "/newarticles"),
            "category" => WithPage($"{Prefix}/category/{Uri.EscapeDataString(Category ?? "")}"),
            "search" => WithPage($"{Prefix}/search?query={Uri.EscapeDataString(SearchText.Trim())}"),
            _ => WithPage(Prefix + "/"),
        };
    }

    private void LoadLocalList()
    {
        ++_requestId;
        IsLoading = false;
        Status = "";
        Articles.Clear();
        var history = IsHistoryTopic;
        Heading = LocalizationService.Instance[history ? "Dic_History" : "Dic_Saved"];
        var entries = history ? _library.History : _library.Saved;
        Info = entries.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        foreach (var e in entries)
        {
            var card = new DicArticleCard
            {
                Title = e.Title.Length > 0 ? e.Title : e.Tag,
                Tag = e.Tag,
                Summary = e.Summary,
                Meta = $"{(e.Lang == "en" ? "EN" : "日本語")}  ·  {e.Time.ToLocalTime():g}",
                Entry = e,
                ImageUrl = e.ImageUrl,
            };
            Articles.Add(card);
            if (card.ImageUrl is { Length: > 0 }) _ = card.LoadThumbnailAsync(_loader);
        }
        if (Articles.Count == 0) Status = LocalizationService.Instance[history ? "Dic_HistoryEmpty" : "Dic_SavedEmpty"];
        OnPropertyChanged(nameof(CanNextPage));
    }

    [RelayCommand]
    private void ToggleSave()
    {
        if (!IsArticleOpen || ArticleTag.Length == 0) return;
        IsArticleSaved = _library.ToggleSaved(CurrentEntry());
        if (IsLocalTopic) LoadLocalList();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _library.ClearHistory();
        if (IsHistoryTopic) LoadLocalList();
    }

    [RelayCommand]
    private void RemoveEntry(DicArticleCard card)
    {
        if (card.Entry is not { } e) return;
        _library.Remove(e, IsHistoryTopic);
        LoadLocalList();
    }

    private DicLibraryEntry CurrentEntry()
    {
        string Str(string name) => Article is { } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var title = ArticleLang == "en" ? Str("translatedTagName") is { Length: > 0 } t ? $"{ArticleTag} ({t})" : ArticleTag : Str("tagName");
        var summary = Str("abstract");
        string? image = Article is { } art && art.TryGetProperty("mainIllust", out var mi) && mi.ValueKind == JsonValueKind.Object
            && mi.TryGetProperty("imageUrl", out var iu) && iu.ValueKind == JsonValueKind.String ? iu.GetString() : null;
        return new DicLibraryEntry(ArticleTag, title.Length > 0 ? title : ArticleTag, ArticleLang, summary, DateTimeOffset.Now, image);
    }

    private async Task LoadListAsync()
    {
        if (IsLocalTopic) { LoadLocalList(); return; }
        var id = ++_requestId;
        IsLoading = true;
        Status = "";
        _ = LoadSidebarAsync();
        try
        {
            var (doc, status) = await _dic.GetListAsync(ListPath());
            if (id != _requestId) { doc?.Dispose(); return; }
            Articles.Clear();
            if (doc is null)
            {
                Status = status == 0
                    ? LocalizationService.Instance["Dic_Unavailable"]
                    : string.Format(LocalizationService.Instance["Dic_LoadFailed"], status);
                return;
            }
            using (doc)
            {
                var root = doc.RootElement;
                Heading = root.GetProperty("heading").GetString() ?? "";
                Info = root.GetProperty("info").GetString() ?? "";
                foreach (var item in root.GetProperty("items").EnumerateArray())
                {
                    var card = new DicArticleCard
                    {
                        Title = item.GetProperty("title").GetString() ?? "",
                        Tag = TagFromHref(item.GetProperty("href").GetString()),
                        Summary = item.GetProperty("summary").GetString() ?? "",
                        Meta = string.Join("  ·  ", item.GetProperty("data").EnumerateArray().Select(d => d.GetString())),
                        Related = item.GetProperty("related").EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList(),
                        ImageUrl = item.GetProperty("image").GetString(),
                    };
                    Articles.Add(card);
                    _ = card.LoadThumbnailAsync(_loader);
                }
                if (Articles.Count == 0) Status = LocalizationService.Instance["Dic_NoArticles"];
            }
        }
        finally
        {
            if (id == _requestId) IsLoading = false;
            OnPropertyChanged(nameof(CanNextPage));
        }
    }

    private async Task LoadSidebarAsync()
    {
        if (_sidebarLoaded) return;
        _sidebarLoaded = true;
        var lang = Language;
        var (doc, _) = await _dic.GetApiAsync($"/get_sidebar?lang={lang}");
        if (doc is null) { _sidebarLoaded = false; return; }
        using (doc)
        {
            Fill(WeeklyRanking, doc.RootElement, "weeklyRanking");
            Fill(MonthlyRanking, doc.RootElement, "monthlyRanking");
            Fill(UpdatedArticles, doc.RootElement, "updatedArticles");
            Fill(NewArticles, doc.RootElement, "newArticles");
        }
        var (att, _) = await _dic.GetApiAsync($"/get_attentions?lang={lang}");
        if (att is null) return;
        using (att)
        {
            Attentions.Clear();
            foreach (var a in att.RootElement.EnumerateArray())
                if (a.TryGetProperty("tagName", out var t) && t.GetString() is { Length: > 0 } s) Attentions.Add(s);
        }
    }

    private void Fill(ObservableCollection<DicArticleCard> target, JsonElement root, string name)
    {
        target.Clear();
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var a in arr.EnumerateArray())
        {
            var tag = a.TryGetProperty("tagName", out var t) ? t.GetString() ?? "" : "";
            var card = new DicArticleCard
            {
                Title = tag,
                Tag = tag,
                ImageUrl = a.TryGetProperty("imageUrl", out var i) ? i.GetString() : null,
                Meta = a.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "",
            };
            target.Add(card);
            _ = card.LoadThumbnailAsync(_loader);
        }
    }

    private async Task LoadArticleAsync(string tag, bool pushHistory)
    {
        var id = ++_requestId;
        if (pushHistory && ArticleTag.Length > 0 && ArticleTag != tag) _history.Push(ArticleTag);
        if (!IsArticleOpen) _history.Clear();
        IsLoading = true;
        Status = "";
        _ = LoadSidebarAsync();
        try
        {
            var path = Uri.EscapeDataString(tag);
            // English: the community article, then pixiv's auto-translation, then the Japanese original.
            var attempts = IsEnglish
                ? new[] { ("en", "ugc", ""), ("en", "translation", "Dic_AutoTranslated"), ("ja", "ugc", "Dic_JapaneseOriginal") }
                : new[] { ("ja", "ugc", "") };
            JsonDocument? doc = null;
            var status = 0;
            var (lang, view, note) = attempts[0];
            foreach (var attempt in attempts)
            {
                (lang, view, note) = attempt;
                (doc, status) = await _dic.GetApiAsync($"/get_article/{path}?lang={lang}&viewType={view}");
                if (id != _requestId) { doc?.Dispose(); return; }
                if (doc is not null || status != 404) break;
            }
            ArticleTag = tag;
            ArticleLang = lang;
            ArticleNote = note.Length > 0 ? LocalizationService.Instance[note] : "";
            IsArticleOpen = true;
            if (doc is null)
            {
                Article = null;
                ArticleInfo = null;
                Status = status == 404
                    ? string.Format(LocalizationService.Instance["Dic_NoArticleFor"], tag)
                    : status == 0 ? LocalizationService.Instance["Dic_Unavailable"]
                    : string.Format(LocalizationService.Instance["Dic_LoadFailed"], status);
                return;
            }
            using (doc) Article = doc.RootElement.Clone();
            OnPropertyChanged(nameof(PixivTag));
            _library.AddHistory(CurrentEntry());
            IsArticleSaved = _library.IsSaved(tag, lang);
            var (info, _) = await _dic.GetApiAsync($"/get_article_info/{path}?lang={lang}&viewType={view}");
            if (id != _requestId) { info?.Dispose(); return; }
            using (info) ArticleInfo = info?.RootElement.Clone();
        }
        finally
        {
            if (id == _requestId) IsLoading = false;
        }
    }

    public static string TagFromHref(string? href)
    {
        if (string.IsNullOrEmpty(href)) return "";
        var i = href.IndexOf("/a/", StringComparison.Ordinal);
        return i < 0 ? href : Uri.UnescapeDataString(href[(i + 3)..]);
    }
}
