using System.Text.Json;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>
/// Persists a local "favorites" list of artworks to a JSON file in %APPDATA%\Pikura.
/// This is entirely app-side — no Pixiv API involved.
/// </summary>
public sealed class LocalFavoritesService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private string _activePath;
    private readonly Lock _gate = new();
    private List<LocalFavoriteEntry> _entries = [];

    public event EventHandler? Changed;

    public LocalFavoritesService(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
        _activePath = _path;
        Load();
    }

    public static string DefaultPath() => PathForUser(null);

    public static string PathForUser(string? userId)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura");
        Directory.CreateDirectory(dir);
        var file = string.IsNullOrWhiteSpace(userId)
            ? "local_favorites.json"
            : $"local_favorites_{userId}.json";
        return System.IO.Path.Combine(dir, file);
    }

    /// <summary>Reload favorites from the per-user file for the given account.</summary>
    public void SwitchUser(string? userId)
    {
        var newPath = PathForUser(userId);
        // _path is readonly — use the field directly via reflection-free workaround:
        // instead, reload into _entries from the new path
        lock (_gate)
        {
            if (!File.Exists(newPath) && !string.IsNullOrWhiteSpace(userId))
            {
                // Migrate the legacy shared file only when no per-user files exist yet,
                // meaning this is the very first account to be migrated.
                var legacyPath = PathForUser(null);
                var dir = System.IO.Path.GetDirectoryName(newPath)!;
                bool anyPerUserFileExists = Directory.EnumerateFiles(dir, "local_favorites_*.json").Any();
                if (File.Exists(legacyPath) && !anyPerUserFileExists)
                {
                    try { File.Copy(legacyPath, newPath, overwrite: false); } catch { }
                }
            }

            if (!File.Exists(newPath)) { _entries = []; }
            else
            {
                try
                {
                    using var fs = File.OpenRead(newPath);
                    _entries = JsonSerializer.Deserialize<List<LocalFavoriteEntry>>(fs, JsonOpts) ?? [];
                }
                catch { _entries = []; }
            }
            _activePath = newPath;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<LocalFavoriteEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public string? GetFolder(string artworkId)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.Id == artworkId)?.FolderName;
    }

    public void SetFolder(string artworkId, string? folder)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == artworkId);
            if (entry == null) return;
            entry.FolderName = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> GetAllFolders()
    {
        lock (_gate)
            return _entries
                .Where(e => !string.IsNullOrWhiteSpace(e.FolderName))
                .Select(e => e.FolderName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
    }

    public bool IsFavorite(string artworkId)
    {
        lock (_gate) return _entries.Any(e => e.Id == artworkId);
    }

    public void Add(ArtworkPreview artwork)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Id == artwork.Id)) return;
            _entries.Add(LocalFavoriteEntry.FromPreview(artwork));
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Backfills a favorite's thumbnail URL after the fact — used to repair entries that were
    /// saved with a null ThumbnailUrl (e.g. artworks added via the single-artwork detail endpoint
    /// before its "urls" field was mapped correctly). No-op if the entry no longer exists or
    /// already has a thumbnail.
    /// </summary>
    public void UpdateThumbnailUrl(string artworkId, string thumbnailUrl)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == artworkId);
            if (entry == null || !string.IsNullOrEmpty(entry.ThumbnailUrl)) return;
            entry.ThumbnailUrl = thumbnailUrl;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string artworkId)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(e => e.Id == artworkId);
            if (removed > 0) Save();
            else return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle(ArtworkPreview artwork)
    {
        if (IsFavorite(artwork.Id)) Remove(artwork.Id);
        else Add(artwork);
    }

    private void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) { _entries = []; return; }
            try
            {
                using var fs = File.OpenRead(_path);
                _entries = JsonSerializer.Deserialize<List<LocalFavoriteEntry>>(fs, JsonOpts) ?? [];
            }
            catch { _entries = []; }
        }
    }

    private void Save()
    {
        using var fs = File.Create(_activePath);
        JsonSerializer.Serialize(fs, _entries, JsonOpts);
    }
}

/// <summary>Minimal snapshot of an artwork stored as a local favorite.</summary>
public sealed class LocalFavoriteEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public int PageCount { get; set; } = 1;
    public int XRestrict { get; set; }
    public int IllustType { get; set; }
    public int AiType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FolderName { get; set; }

    public ArtworkPreview ToArtworkPreview() => new()
    {
        Id = Id,
        Title = Title,
        UserId = UserId,
        UserName = UserName,
        ThumbnailUrl = ThumbnailUrl,
        PageCount = PageCount,
        XRestrict = XRestrict,
        IllustType = IllustType,
        AiType = AiType,
        Width = Width,
        Height = Height,
        Tags = Tags,
    };

    public static LocalFavoriteEntry FromPreview(ArtworkPreview p) => new()
    {
        Id = p.Id,
        Title = p.Title,
        UserId = p.UserId,
        UserName = p.UserName,
        ThumbnailUrl = p.ThumbnailUrl,
        PageCount = p.PageCount,
        XRestrict = p.XRestrict,
        IllustType = p.IllustType,
        AiType = p.AiType,
        Width = p.Width,
        Height = p.Height,
        Tags = p.Tags,
        AddedAt = DateTimeOffset.UtcNow,
    };
}
