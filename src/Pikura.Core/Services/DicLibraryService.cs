using System.Text.Json;

namespace Pikura.Core.Services;

public sealed record DicLibraryEntry(string Tag, string Title, string Lang, string Summary, DateTimeOffset Time, string? ImageUrl = null);

/// <summary>Saved pixiv Encyclopedia articles and article history, stored app-side in %APPDATA%\Pikura.</summary>
public sealed class DicLibraryService
{
    public const int MaxHistory = 300;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _gate = new();
    private Store _store = new();

    private sealed class Store
    {
        public List<DicLibraryEntry> Saved { get; set; } = [];
        public List<DicLibraryEntry> History { get; set; } = [];
    }

    public event EventHandler? Changed;

    public DicLibraryService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        Load();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "dic_library.json");
    }

    public IReadOnlyList<DicLibraryEntry> Saved { get { lock (_gate) return _store.Saved.OrderByDescending(e => e.Time).ToList(); } }
    public IReadOnlyList<DicLibraryEntry> History { get { lock (_gate) return _store.History.OrderByDescending(e => e.Time).ToList(); } }

    public bool IsSaved(string tag, string lang)
    {
        lock (_gate) return _store.Saved.Any(e => Same(e, tag, lang));
    }

    /// <summary>Saves or unsaves the article; returns whether it is now saved.</summary>
    public bool ToggleSaved(DicLibraryEntry entry)
    {
        bool saved;
        lock (_gate)
        {
            saved = _store.Saved.RemoveAll(e => Same(e, entry.Tag, entry.Lang)) == 0;
            if (saved) _store.Saved.Add(entry);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public void AddHistory(DicLibraryEntry entry)
    {
        lock (_gate)
        {
            _store.History.RemoveAll(e => Same(e, entry.Tag, entry.Lang));
            _store.History.Add(entry);
            if (_store.History.Count > MaxHistory)
                _store.History = _store.History.OrderByDescending(e => e.Time).Take(MaxHistory).ToList();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(DicLibraryEntry entry, bool fromHistory)
    {
        lock (_gate)
        {
            (fromHistory ? _store.History : _store.Saved).RemoveAll(e => Same(e, entry.Tag, entry.Lang));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        lock (_gate) { _store.History.Clear(); Save(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Same(DicLibraryEntry e, string tag, string lang) => e.Tag == tag && e.Lang == lang;

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
        catch { }
    }
}
