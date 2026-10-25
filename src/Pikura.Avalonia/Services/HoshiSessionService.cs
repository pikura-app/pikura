using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pikura.Avalonia.ViewModels;
using Pikura.Core.Services.Hoshi;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Store-backed Hoshi session service. Public surface kept compatible with the
/// pre-SQLite implementation: <see cref="Sessions"/> holds lightweight headers
/// (Messages empty until the session is opened via <see cref="LoadMessagesAsync"/>,
/// image bytes loaded via <see cref="LoadImageAsync"/>).
/// </summary>
public sealed class HoshiSessionService
{
    private readonly HoshiStore _store;
    private readonly HoshiMigration _migration;
    private string? _userId;

    /// <summary>Session headers, ordered pinned-then-UpdatedAt descending.</summary>
    public ObservableCollection<HoshiSession> Sessions { get; } = new();

    /// <summary>Legacy migration progress text for the sidebar (null when idle).</summary>
    public string? MigrationStatus { get; private set; }
    public event EventHandler? MigrationStatusChanged;

    /// <summary>Fired ONLY on account switch (wipes active chat).</summary>
    public event EventHandler? SessionsChanged;

    public HoshiStore Store => _store;

    public HoshiSessionService(HoshiStore store, ILogger<HoshiMigration> migrationLogger)
    {
        _store = store;
        _migration = new HoshiMigration(store, migrationLogger);
        _ = Task.Run(RefreshHeadersAsync);
        _ = Task.Run(MigrateInBackgroundAsync);
        _ = Task.Run(DailyCleanupAsync);
    }

    /// <summary>Legacy roaming dir — kept for migration + tests.</summary>
    public static string DirForUser(string? userId)
    {
        var root = HoshiStore.LegacyDataDir();
        return string.IsNullOrWhiteSpace(userId) ? root : Path.Combine(root, userId);
    }

    /// <summary>Switch the active user — headers only, no full reload.</summary>
    public void SwitchUser(string? userId)
    {
        _userId = userId;
        _ = Task.Run(async () =>
        {
            await RefreshHeadersAsync();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task RefreshHeadersAsync()
    {
        var headers = await _store.GetSessionHeadersAsync(_userId);
        var mapped = headers.Select(ToSession).ToList();
        // Replace on the UI thread when possible.
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Sessions.Clear();
            foreach (var s in mapped) Sessions.Add(s);
        });
    }

    private static HoshiSession ToSession(HoshiSessionHeader h) => new()
    {
        Id = h.Id, Title = h.Title, Pinned = h.Pinned,
        ImageSha = h.ImageSha, ImageSource = h.ImageSource,
        PixivArtworkId = h.PixivArtworkId,
        CreatedAt = h.CreatedAt, UpdatedAt = h.UpdatedAt,
        MessageCount = h.MessageCount,
    };

    /// <summary>Loads a session's messages + image sha (off UI thread).</summary>
    public async Task LoadMessagesAsync(HoshiSession session)
    {
        var rows = await _store.GetSessionMessagesAsync(session.Id);
        session.Messages = rows.Select(m => new PersistedMessage
        {
            Role = m.Role, Content = m.Content, At = m.At,
            ImageSha = m.ImageSha, ArtworkId = m.ArtworkId, ArtistId = m.ArtistId,
        }).ToList();
    }

    /// <summary>Loads image bytes for a sha (session image or message thumbnail).</summary>
    public Task<byte[]?> LoadImageAsync(string? sha) =>
        sha == null ? Task.FromResult<byte[]?>(null) : _store.OpenImageAsync(sha);

    // Bounded in-memory thumbnail cache: 200 entries keyed by sha, display-size decodes.
    private const int ThumbCacheCap = 200;
    private readonly LinkedList<KeyValuePair<string, global::Avalonia.Media.Imaging.Bitmap>> _thumbLru = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, global::Avalonia.Media.Imaging.Bitmap>>> _thumbMap = new();

    /// <summary>Display-size thumbnail for a message image sha (bounded LRU, off-UI decode).</summary>
    public async Task<global::Avalonia.Media.Imaging.Bitmap?> GetThumbnailAsync(string? sha, int width = 200)
    {
        if (sha == null) return null;
        lock (_thumbMap)
        {
            if (_thumbMap.TryGetValue(sha, out var node))
            {
                _thumbLru.Remove(node); _thumbLru.AddFirst(node);
                return node.Value.Value;
            }
        }
        var bytes = await Task.Run(() => _store.OpenImageAsync(sha));
        if (bytes == null) return null;
        var bmp = await Task.Run(() =>
        {
            try { using var ms = new MemoryStream(bytes); return global::Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, width); }
            catch { return null!; }
        });
        if (bmp == null) return null;
        lock (_thumbMap)
        {
            if (_thumbMap.TryGetValue(sha, out var node))
            {
                _thumbLru.Remove(node); _thumbLru.AddFirst(node);
                bmp.Dispose();
                return node.Value.Value;
            }
            var nn = new LinkedListNode<KeyValuePair<string, global::Avalonia.Media.Imaging.Bitmap>>(
                new KeyValuePair<string, global::Avalonia.Media.Imaging.Bitmap>(sha, bmp));
            _thumbLru.AddFirst(nn); _thumbMap[sha] = nn;
            while (_thumbLru.Count > ThumbCacheCap)
            {
                _thumbMap.Remove(_thumbLru.Last!.Value.Key);
                _thumbLru.Last!.Value.Value.Dispose();
                _thumbLru.RemoveLast();
            }
        }
        return bmp;
    }

    /// <summary>Creates a new empty session and persists it.</summary>
    public HoshiSession CreateNew(string? title = null)
    {
        var s = new HoshiSession
        {
            Title = title ?? LocalizationService.Instance["Hoshi_NewChat"],
            UserId = _userId,
        };
        Sessions.Insert(0, s);
        _ = SaveAsync(s);
        return s;
    }

    /// <summary>Persists a session (debounced caller-side; saves go through the store).</summary>
    public async Task SaveAsync(HoshiSession session)
    {
        try
        {
            session.UpdatedAt = DateTime.UtcNow;
            if (session.ImageBytes is { Length: > 0 } b)
                session.ImageSha = await _store.PutImageAsync(b);
            var msgs = new List<HoshiMessageRow>();
            foreach (var m in session.Messages)
            {
                string? sha = m.ImageSha;
                if (sha == null && m.ImageBase64 != null)
                {
                    try { sha = await _store.PutImageAsync(Convert.FromBase64String(m.ImageBase64)); }
                    catch { }
                }
                msgs.Add(new HoshiMessageRow
                {
                    SessionId = session.Id, Role = m.Role, Content = m.Content, At = m.At,
                    ImageSha = sha, ArtworkId = m.ArtworkId, ArtistId = m.ArtistId,
                });
            }
            await _store.SaveSessionAsync(ToHeader(session), msgs);
            session.MessageCount = msgs.Count;
        }
        catch { /* persist failure is non-fatal */ }
    }

    private HoshiSessionHeader ToHeader(HoshiSession s) => new()
    {
        Id = s.Id, UserId = s.UserId ?? _userId, Title = s.Title, Pinned = s.Pinned,
        ImageSha = s.ImageSha, ImageSource = s.ImageSource, PixivArtworkId = s.PixivArtworkId,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt, MessageCount = s.MessageCount,
    };

    public async Task DeleteAsync(string id)
    {
        var existing = Sessions.FirstOrDefault(s => s.Id == id);
        if (existing != null) Sessions.Remove(existing);
        await _store.DeleteSessionsAsync(new[] { id });
    }

    public async Task DeleteManyAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            var existing = Sessions.FirstOrDefault(s => s.Id == id);
            if (existing != null) Sessions.Remove(existing);
        }
        await _store.DeleteSessionsAsync(ids);
    }

    public async Task SetPinnedAsync(HoshiSession s, bool pinned)
    {
        s.Pinned = pinned;
        await _store.SetPinnedAsync(s.Id, pinned);
        await RefreshHeadersAsync();
    }

    public async Task RenameAsync(HoshiSession s, string title)
    {
        s.Title = title;
        await _store.RenameAsync(s.Id, title);
    }

    /// <summary>FTS search → matching session ids (null = show all).</summary>
    public Task<List<HoshiSearchHit>> SearchAsync(string text) =>
        _store.SearchAsync(_userId, text);

    /// <summary>Creates a copy of a session with a new id.</summary>
    public HoshiSession Duplicate(HoshiSession source)
    {
        var copy = new HoshiSession
        {
            Title = source.Title + LocalizationService.Instance["Hoshi_Copy"],
            UserId = source.UserId ?? _userId,
            ImageSource = source.ImageSource,
            PixivArtworkId = source.PixivArtworkId,
            ImageSha = source.ImageSha,
            ImageBytes = source.ImageBytes?.ToArray(),
            Messages = source.Messages.Select(m => new PersistedMessage
            {
                Role = m.Role, Content = m.Content, At = m.At,
                ImageSha = m.ImageSha, ImageBase64 = m.ImageBase64,
                ArtworkId = m.ArtworkId, ArtistId = m.ArtistId
            }).ToList(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        Sessions.Insert(0, copy);
        _ = SaveAsync(copy);
        return copy;
    }

    public void Touch(HoshiSession session)
    {
        var idx = Sessions.IndexOf(session);
        if (idx > 0) Sessions.Move(idx, 0);
    }

    /// <summary>Sessions grouped: Pinned first, then date groups.</summary>
    public List<SessionGroupViewModel> GetGroupedSessions()
    {
        var pinned = Sessions.Where(s => s.Pinned).ToList();
        var groups = Sessions.Where(s => !s.Pinned)
            .GroupBy(s => s.UpdatedAt.ToLocalTime().Date)
            .Select(g => new SessionGroupViewModel(g.Key, g))
            .OrderByDescending(g => g.Date)
            .ToList();
        if (pinned.Count > 0)
            groups.Insert(0, new SessionGroupViewModel(DateTime.MinValue, pinned) { IsPinnedGroup = true });
        return groups;
    }

    /// <summary>Retention/size cleanup — runs after migration, at most once a day.</summary>
    public async Task DailyCleanupAsync()
    {
        var s = Pikura.Avalonia.Services.AppServices.Get<Pikura.Core.Settings.SettingsService>().Current;
        var maxAge = s.HoshiChatRetentionDays;
        var maxMb = s.HoshiMaxImageStoreMb;
        if ((maxAge <= 0 && maxMb <= 0)
            || (s.HoshiLastCleanupAt is DateTime last && (DateTime.UtcNow - last).TotalDays < 1))
            return;
        try
        {
            await _store.CleanupAsync(maxAge > 0 ? maxAge : null, maxMb > 0 ? (long)maxMb * 1024 * 1024 : null);
            Pikura.Avalonia.Services.AppServices.Get<Pikura.Core.Settings.SettingsService>()
                .Update(x => x.HoshiLastCleanupAt = DateTime.UtcNow);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Background one-time legacy import (safe: verifies, records, renames last).</summary>
    private async Task MigrateInBackgroundAsync()
    {
        var legacy = HoshiStore.LegacyDataDir();
        if (!Directory.Exists(legacy)) return;
        try
        {
            var loc = LocalizationService.Instance;
            var total = Directory.EnumerateFiles(legacy, "*.json", SearchOption.AllDirectories).Count();
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                MigrationStatus = string.Format(loc["Hoshi_MigratingChats"], p.Done, p.Total);
                MigrationStatusChanged?.Invoke(this, EventArgs.Empty);
            });
            var result = await _migration.MigrateAsync(legacy, progress);
            MigrationStatus = null;
            MigrationStatusChanged?.Invoke(this, EventArgs.Empty);
            await RefreshHeadersAsync();
        }
        catch { /* retry next launch */ }
    }
}
