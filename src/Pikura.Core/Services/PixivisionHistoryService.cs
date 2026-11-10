using System.Text.Json;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>
/// Persists the pixivision articles the user has opened to a JSON file in %APPDATA%\Pikura,
/// newest first and capped at <see cref="MaxEntries"/>. Same storage pattern as
/// <see cref="PixivisionSavedArticlesService"/>.
/// </summary>
public sealed class PixivisionHistoryService
{
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<ViewedArticleEntry> _entries = [];

    public event EventHandler? Changed;

    public PixivisionHistoryService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        Load();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pixivision_history.json");
    }

    public IReadOnlyList<ViewedArticleEntry> GetAll()
    {
        lock (_gate) return _entries.OrderByDescending(e => e.ViewedAt).ToList();
    }

    /// <summary>Adds the article, or moves it to the top with a fresh timestamp if already present.</summary>
    public void Record(PixivisionArticleSummary article)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == article.Id);
            var entry = ViewedArticleEntry.FromSummary(article);
            if (_entries.Count > 0 && _entries.Max(e => e.ViewedAt) >= entry.ViewedAt)
                entry.ViewedAt = _entries.Max(e => e.ViewedAt).AddTicks(1);
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries = _entries.OrderByDescending(e => e.ViewedAt).Take(MaxEntries).ToList();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(long articleId)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.Id == articleId) == 0) return;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) { _entries = []; return; }
            try
            {
                using var fs = File.OpenRead(_path);
                _entries = JsonSerializer.Deserialize<List<ViewedArticleEntry>>(fs, JsonOpts) ?? [];
            }
            catch { _entries = []; }
        }
    }

    private void Save()
    {
        using var fs = File.Create(_path);
        JsonSerializer.Serialize(fs, _entries, JsonOpts);
    }
}

/// <summary>Snapshot of a viewed pixivision article.</summary>
public sealed class ViewedArticleEntry
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<PixivisionTagRef> TagRefs { get; set; } = [];
    public string? CategorySlug { get; set; }
    public DateTime? PublishedDate { get; set; }
    public DateTimeOffset ViewedAt { get; set; } = DateTimeOffset.UtcNow;

    public PixivisionArticleSummary ToSummary() => new()
    {
        Id = Id,
        Title = Title,
        ThumbnailUrl = ThumbnailUrl,
        Tags = Tags.ToList(),
        TagRefs = TagRefs.ToList(),
        CategorySlug = CategorySlug,
        PublishedDate = PublishedDate,
    };

    public static ViewedArticleEntry FromSummary(PixivisionArticleSummary s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        ThumbnailUrl = s.ThumbnailUrl,
        Tags = s.Tags.ToList(),
        TagRefs = s.TagRefs.ToList(),
        CategorySlug = s.CategorySlug,
        PublishedDate = s.PublishedDate,
        ViewedAt = DateTimeOffset.UtcNow,
    };
}
