using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Stores Hoshi conversation sessions on disk under <c>%AppData%/Pikura/hoshi_sessions/</c>.
/// Each session is one <c>{id}.json</c> file plus an optional <c>{id}.img</c> sidecar.
/// </summary>
public sealed class HoshiSessionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private string _dir;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>All sessions, sorted by UpdatedAt descending (most recent first).</summary>
    public ObservableCollection<HoshiSession> Sessions { get; } = new();

    /// <summary>
    /// Fired ONLY when the sessions directory itself swaps (account switch).
    /// Listeners use this to wipe the active chat — so we must NOT fire it for
    /// ordinary create/delete/duplicate operations or the active conversation
    /// gets cleared mid-stream. Sidebar updates are driven by
    /// <see cref="Sessions"/>.CollectionChanged instead.
    /// </summary>
    public event EventHandler? SessionsChanged;

    public HoshiSessionService()
    {
        _dir = DirForUser(null);
        Directory.CreateDirectory(_dir);
        LoadAll();
    }

    /// <summary>Returns the per-user sessions directory. Falls back to the legacy shared dir when userId is null/empty.</summary>
    public static string DirForUser(string? userId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pikura");
        return string.IsNullOrWhiteSpace(userId)
            ? Path.Combine(baseDir, "hoshi_sessions")
            : Path.Combine(baseDir, "hoshi_sessions", userId);
    }

    /// <summary>Switch the active user. Reloads sessions from the per-user directory.</summary>
    public void SwitchUser(string? userId)
    {
        _dir = DirForUser(userId);
        Directory.CreateDirectory(_dir);
        LoadAll();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadAll()
    {
        Sessions.Clear();
        try
        {
            var jsons = Directory.EnumerateFiles(_dir, "*.json").ToList();
            var loaded = new System.Collections.Generic.List<HoshiSession>();
            foreach (var path in jsons)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var session = JsonSerializer.Deserialize<HoshiSession>(json, JsonOpts);
                    if (session == null) continue;

                    // Sidecar image
                    var imgPath = Path.ChangeExtension(path, ".img");
                    if (File.Exists(imgPath))
                    {
                        try { session.ImageBytes = File.ReadAllBytes(imgPath); }
                        catch { /* corrupt sidecar — skip image */ }
                    }
                    loaded.Add(session);
                }
                catch { /* skip corrupt session file */ }
            }
            foreach (var s in loaded.OrderByDescending(x => x.UpdatedAt))
                Sessions.Add(s);
        }
        catch { /* directory inaccessible — start empty */ }
    }

    /// <summary>Creates a new empty session, persists it, and returns it.</summary>
    public HoshiSession CreateNew(string? title = null)
    {
        var s = new HoshiSession { Title = title ?? "New chat" };
        Sessions.Insert(0, s);
        _ = SaveAsync(s);
        // Do NOT raise SessionsChanged here — that event is reserved for
        // account switches (it wipes the active chat). Sessions.CollectionChanged
        // already notifies the sidebar that a new entry was inserted.
        return s;
    }

    /// <summary>Persists a session to disk (JSON + optional image sidecar).</summary>
    public async Task SaveAsync(HoshiSession session)
    {
        await _ioLock.WaitAsync();
        try
        {
            session.UpdatedAt = DateTime.UtcNow;
            var jsonPath = Path.Combine(_dir, session.Id + ".json");
            var imgPath  = Path.Combine(_dir, session.Id + ".img");

            var json = JsonSerializer.Serialize(session, JsonOpts);
            await File.WriteAllTextAsync(jsonPath, json);

            if (session.ImageBytes is { Length: > 0 } bytes)
            {
                await File.WriteAllBytesAsync(imgPath, bytes);
            }
            else if (File.Exists(imgPath))
            {
                try { File.Delete(imgPath); } catch { /* ignore */ }
            }
        }
        catch { /* persist failure is non-fatal */ }
        finally { _ioLock.Release(); }
    }

    /// <summary>Deletes a session from disk and the collection.</summary>
    public async Task DeleteAsync(string id)
    {
        var existing = Sessions.FirstOrDefault(s => s.Id == id);
        if (existing != null) Sessions.Remove(existing);

        await _ioLock.WaitAsync();
        try
        {
            var jsonPath = Path.Combine(_dir, id + ".json");
            var imgPath  = Path.Combine(_dir, id + ".img");
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
            if (File.Exists(imgPath))  File.Delete(imgPath);
        }
        catch { /* non-fatal */ }
        finally { _ioLock.Release(); }
        // Sidebar is refreshed via Sessions.CollectionChanged on the Remove above.
    }

    /// <summary>Creates a copy of a session with a new id (image + messages preserved).</summary>
    public HoshiSession Duplicate(HoshiSession source)
    {
        var copy = new HoshiSession
        {
            Title = source.Title + " (copy)",
            ImageSource = source.ImageSource,
            PixivArtworkId = source.PixivArtworkId,
            ImageBytes  = source.ImageBytes?.ToArray(),
            Messages    = source.Messages.Select(m => new PersistedMessage
            {
                Role = m.Role,
                Content = m.Content,
                At = m.At,
                ImageBase64 = m.ImageBase64,
                ArtworkId = m.ArtworkId,
                ArtistId = m.ArtistId
            }).ToList(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Sessions.Insert(0, copy);
        _ = SaveAsync(copy);
        // Sidebar is refreshed via Sessions.CollectionChanged on the Insert above.
        return copy;
    }

    /// <summary>Moves a session to the top of the list (call after updates).</summary>
    public void Touch(HoshiSession session)
    {
        var idx = Sessions.IndexOf(session);
        if (idx > 0) Sessions.Move(idx, 0);
    }
    
    /// <summary>Gets sessions grouped by date for display in the sidebar.</summary>
    public List<SessionGroupViewModel> GetGroupedSessions()
    {
        return Sessions
            .GroupBy(s => s.UpdatedAt.ToLocalTime().Date)
            .Select(g => new SessionGroupViewModel(g.Key, g))
            .OrderByDescending(g => g.Date)
            .ToList();
    }
}
