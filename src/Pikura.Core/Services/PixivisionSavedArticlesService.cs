using System.Text.Json;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>
/// Persists a "read later" list of pixivision.net articles to a JSON file in %APPDATA%\Pikura.
/// Mirrors <see cref="LocalFavoritesService"/>'s storage pattern — entirely app-side, no pixivision
/// account/API involved (pixivision has no such feature of its own).
/// </summary>
public sealed class PixivisionSavedArticlesService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<SavedArticleEntry> _entries = [];

    public event EventHandler? Changed;

    public PixivisionSavedArticlesService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        Load();
    }

    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura");
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, "pixivision_saved_articles.json");
    }

    public IReadOnlyList<SavedArticleEntry> GetAll()
    {
        lock (_gate) return _entries.OrderByDescending(e => e.SavedAt).ToList();
    }

    public bool IsSaved(long articleId)
    {
        lock (_gate) return _entries.Any(e => e.Id == articleId);
    }

    public void Add(PixivisionArticleSummary article)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Id == article.Id)) return;
            _entries.Add(SavedArticleEntry.FromSummary(article));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(long articleId)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(e => e.Id == articleId);
            if (removed > 0) Save();
            else return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle(PixivisionArticleSummary article)
    {
        if (IsSaved(article.Id)) Remove(article.Id);
        else Add(article);
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) { _entries = []; return; }
            try
            {
                using var fs = File.OpenRead(_path);
                _entries = JsonSerializer.Deserialize<List<SavedArticleEntry>>(fs, JsonOpts) ?? [];
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

/// <summary>Minimal snapshot of a pixivision article stored for reading later.</summary>
public sealed class SavedArticleEntry
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    public PixivisionArticleSummary ToSummary() => new()
    {
        Id = Id,
        Title = Title,
        ThumbnailUrl = ThumbnailUrl,
        Tags = Tags,
    };

    public static SavedArticleEntry FromSummary(PixivisionArticleSummary s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        ThumbnailUrl = s.ThumbnailUrl,
        Tags = s.Tags.ToList(),
        SavedAt = DateTimeOffset.UtcNow,
    };
}
