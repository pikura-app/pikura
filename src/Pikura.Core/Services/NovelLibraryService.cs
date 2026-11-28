using System.Text.Json;

namespace Pikura.Core.Services;

public sealed record NovelLibraryEntry(string Id, string Title, string Author, string? CoverUrl, int XRestrict, DateTimeOffset Time, IReadOnlyList<string>? Tags = null);

/// <summary>Novels saved for later and novel reading history, stored app-side in %APPDATA%\Pikura.</summary>
public sealed class NovelLibraryService
{
    public const int MaxHistory = 300;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _gate = new();
    private Store _store = new();

    private sealed class Store
    {
        public List<NovelLibraryEntry> Saved { get; set; } = [];
        public List<NovelLibraryEntry> History { get; set; } = [];
        public List<NovelLibraryEntry> Favorites { get; set; } = [];
    }

    public event EventHandler? Changed;

    public NovelLibraryService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        Load();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "novel_library.json");
    }

    public IReadOnlyList<NovelLibraryEntry> Saved { get { lock (_gate) return _store.Saved.OrderByDescending(e => e.Time).ToList(); } }
    public IReadOnlyList<NovelLibraryEntry> History { get { lock (_gate) return _store.History.OrderByDescending(e => e.Time).ToList(); } }

    public bool IsSaved(string id) { lock (_gate) return _store.Saved.Any(e => e.Id == id); }
    public IReadOnlyList<NovelLibraryEntry> Favorites { get { lock (_gate) return _store.Favorites.OrderByDescending(e => e.Time).ToList(); } }
    public bool IsFavorite(string id) { lock (_gate) return _store.Favorites.Any(e => e.Id == id); }

    /// <summary>Favorites or unfavorites the novel; returns whether it is now a favorite.</summary>
    public bool ToggleFavorite(NovelLibraryEntry entry)
    {
        bool fav;
        lock (_gate)
        {
            fav = _store.Favorites.RemoveAll(e => e.Id == entry.Id) == 0;
            if (fav) _store.Favorites.Add(entry);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return fav;
    }

    /// <summary>Saves or unsaves the novel; returns whether it is now saved.</summary>
    public bool ToggleSaved(NovelLibraryEntry entry)
    {
        bool saved;
        lock (_gate)
        {
            saved = _store.Saved.RemoveAll(e => e.Id == entry.Id) == 0;
            if (saved) _store.Saved.Add(entry);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public void AddHistory(NovelLibraryEntry entry)
    {
        lock (_gate)
        {
            _store.History.RemoveAll(e => e.Id == entry.Id);
            _store.History.Add(entry);
            if (_store.History.Count > MaxHistory)
                _store.History = _store.History.OrderByDescending(e => e.Time).Take(MaxHistory).ToList();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path)) ?? new();
        }
        catch { _store = new(); }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
