using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pikura.Core.Services.Hoshi;

/// <summary>Lightweight session row (headers only — no messages, no image bytes).</summary>
public sealed class HoshiSessionHeader
{
    public string Id { get; set; } = "";
    public string? UserId { get; set; }
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public string? ImageSha { get; set; }
    public string? ImageSource { get; set; }
    public string? PixivArtworkId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}

public sealed class HoshiMessageRow
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public int Seq { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTime At { get; set; }
    public string? ImageSha { get; set; }
    public string? ArtworkId { get; set; }
    public string? ArtistId { get; set; }
}

public sealed class HoshiSearchHit
{
    public string SessionId { get; set; } = "";
    public string Snippet { get; set; } = "";
}

public sealed record HoshiStorageStats(long Sessions, long DbBytes, long ImageBytes, long ImageFiles, long LegacyBytes);

/// <summary>
/// SQLite-backed store for Hoshi chat sessions + a content-addressed image
/// store. Lives in %LocalAppData%\Pikura\hoshi (non-roaming — images are big).
/// Honor the PIKURA_HOSHI_DATA_DIR env var for tests/dev smoke runs.
/// </summary>
public sealed class HoshiStore : IDisposable
{
    private static readonly Regex CjkRegex = new(
        "[\\u3040-\\u30ff\\u3400-\\u9fff\\uf900-\\ufaff\\uac00-\\ud7af\\u4e00-\\u9fff]",
        RegexOptions.Compiled);

    private readonly string _cs;
    private readonly string _imageDir;
    private readonly ILogger<HoshiStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool TrigramAvailable { get; }
    public string ImageDir => _imageDir;
    /// <summary>Exposed for the migrator/tests only — not for general use.</summary>
    public string ConnectionStringForTests => _cs;

    /// <summary>Root dir for the store (hoshi.db + images/). Overridable for tests.</summary>
    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("PIKURA_HOSHI_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pikura", "hoshi");

    /// <summary>Legacy roaming sessions dir; honors PIKURA_HOSHI_DATA_DIR/../legacy for tests.</summary>
    public static string LegacyDataDir()
    {
        var env = Environment.GetEnvironmentVariable("PIKURA_HOSHI_DATA_DIR");
        if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "legacy_hoshi_sessions");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura", "hoshi_sessions");
    }

    public HoshiStore(string? dataDir, ILogger<HoshiStore> logger)
    {
        _logger = logger;
        var dir = dataDir ?? DefaultDataDir();
        Directory.CreateDirectory(dir);
        _imageDir = Path.Combine(dir, "images");
        Directory.CreateDirectory(_imageDir);
        _cs = $"Data Source={Path.Combine(dir, "hoshi.db")};Foreign Keys=True;Default Timeout=5;";
        EnsureSchema();
        TrigramAvailable = ProbeTrigram();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using (var cmd = new SqliteCommand("PRAGMA journal_mode=WAL;", c)) cmd.ExecuteNonQuery();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        var sql = @"
            CREATE TABLE IF NOT EXISTS sessions(
                id TEXT PRIMARY KEY, user_id TEXT, title TEXT, pinned INTEGER DEFAULT 0,
                image_sha TEXT, image_source TEXT, pixiv_artwork_id TEXT,
                created_at TEXT, updated_at TEXT, message_count INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS idx_hoshi_sessions ON sessions(user_id, pinned, updated_at DESC);
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                seq INTEGER, role TEXT, content TEXT, at TEXT, image_sha TEXT, artwork_id TEXT, artist_id TEXT);
            CREATE INDEX IF NOT EXISTS idx_hoshi_messages ON messages(session_id, seq);
            CREATE TABLE IF NOT EXISTS images(
                sha TEXT PRIMARY KEY, size INTEGER, ext TEXT, last_used_at TEXT);
            CREATE TABLE IF NOT EXISTS migrations(
                session_id TEXT PRIMARY KEY, migrated_at TEXT);
            CREATE VIRTUAL TABLE IF NOT EXISTS hoshi_fts USING fts5(
                title, content, tokenize='unicode61 remove_diacritics 2');
        ";
        using var cmd = new SqliteCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private bool ProbeTrigram()
    {
        try
        {
            using var c = Open();
            using var cmd = new SqliteCommand(
                "CREATE VIRTUAL TABLE IF NOT EXISTS hoshi_fts_tri USING fts5(title, content, tokenize='trigram')", c);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "trigram unavailable — CJK hoshi search falls back to LIKE");
            return false;
        }
    }

    // ── images (content-addressed) ───────────────────────────────────────────

    private static string ExtFor(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 ? "jpg" :
        b.Length >= 4 && b[0] == 0x89 && b[1] == 'P' ? "png" :
        b.Length >= 3 && b[0] == 'G' && b[1] == 'I' ? "gif" :
        b.Length >= 4 && b[0] == 'R' && b[1] == 'I' ? "webp" : "bin";

    private string PathFor(string sha, string ext) =>
        Path.Combine(_imageDir, sha[..2], sha + "." + ext);

    /// <summary>Stores bytes content-addressed. Returns the sha256 hex.</summary>
    public async Task<string> PutImageAsync(byte[] bytes, CancellationToken ct = default)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var ext = ExtFor(bytes);
        var path = PathFor(sha, ext);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        using var c = Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO images(sha,size,ext,last_used_at) VALUES(@s,@z,@e,@t) " +
            "ON CONFLICT(sha) DO UPDATE SET last_used_at=@t", c);
        cmd.Parameters.AddWithValue("@s", sha);
        cmd.Parameters.AddWithValue("@z", bytes.Length);
        cmd.Parameters.AddWithValue("@e", ext);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return sha;
    }

    public async Task<byte[]?> OpenImageAsync(string sha, CancellationToken ct = default)
    {
        using var c = Open();
        string? ext = null;
        using (var cmd = new SqliteCommand("SELECT ext FROM images WHERE sha=@s", c))
        {
            cmd.Parameters.AddWithValue("@s", sha);
            ext = await cmd.ExecuteScalarAsync(ct) as string;
        }
        var path = PathFor(sha, ext ?? "bin");
        if (!File.Exists(path))
        {
            // Fallback: ext might differ — glob the bucket dir.
            var dir = Path.Combine(_imageDir, sha[..2]);
            path = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, sha + ".*").FirstOrDefault()! : path;
            if (path == null || !File.Exists(path)) return null;
        }
        return await File.ReadAllBytesAsync(path, ct);
    }

    public string? ImagePathFor(string sha)
    {
        var dir = Path.Combine(_imageDir, sha[..2]);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, sha + ".*").FirstOrDefault() : null;
    }

    // ── sessions ─────────────────────────────────────────────────────────────

    private static HoshiSessionHeader MapHeader(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), UserId = r.IsDBNull(1) ? null : r.GetString(1),
        Title = r.GetString(2), Pinned = r.GetInt64(3) != 0,
        ImageSha = r.IsDBNull(4) ? null : r.GetString(4),
        ImageSource = r.IsDBNull(5) ? null : r.GetString(5),
        PixivArtworkId = r.IsDBNull(6) ? null : r.GetString(6),
        CreatedAt = DateTime.Parse(r.GetString(7)).ToUniversalTime(),
        UpdatedAt = DateTime.Parse(r.GetString(8)).ToUniversalTime(),
        MessageCount = (int)r.GetInt64(9),
    };

    private const string HeaderCols =
        "id, user_id, title, pinned, image_sha, image_source, pixiv_artwork_id, created_at, updated_at, message_count";

    public async Task<List<HoshiSessionHeader>> GetSessionHeadersAsync(string? userId, CancellationToken ct = default)
    {
        var list = new List<HoshiSessionHeader>();
        using var c = Open();
        using var cmd = new SqliteCommand(
            $"SELECT {HeaderCols} FROM sessions WHERE " +
            (userId == null ? "user_id IS NULL" : "user_id=@u") +
            " ORDER BY pinned DESC, updated_at DESC", c);
        if (userId != null) cmd.Parameters.AddWithValue("@u", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(MapHeader(r));
        return list;
    }

    public async Task<List<HoshiMessageRow>> GetSessionMessagesAsync(string id, CancellationToken ct = default)
    {
        var list = new List<HoshiMessageRow>();
        using var c = Open();
        using var cmd = new SqliteCommand(
            "SELECT id, session_id, seq, role, content, at, image_sha, artwork_id, artist_id" +
            " FROM messages WHERE session_id=@s ORDER BY seq", c);
        cmd.Parameters.AddWithValue("@s", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HoshiMessageRow
            {
                Id = r.GetInt64(0), SessionId = r.GetString(1), Seq = r.GetInt32(2),
                Role = r.GetString(3), Content = r.GetString(4),
                At = r.IsDBNull(5) ? DateTime.MinValue : DateTime.Parse(r.GetString(5)).ToUniversalTime(),
                ImageSha = r.IsDBNull(6) ? null : r.GetString(6),
                ArtworkId = r.IsDBNull(7) ? null : r.GetString(7),
                ArtistId = r.IsDBNull(8) ? null : r.GetString(8),
            });
        return list;
    }

    /// <summary>Transactional save: replaces messages, refreshes FTS + count.</summary>
    public async Task SaveSessionAsync(HoshiSessionHeader h, IReadOnlyList<HoshiMessageRow> messages, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var cmd = new SqliteCommand(
                "INSERT INTO sessions(id,user_id,title,pinned,image_sha,image_source,pixiv_artwork_id,created_at,updated_at,message_count)" +
                " VALUES(@id,@u,@t,@p,@is,@src,@aid,@ca,@ua,@mc)" +
                " ON CONFLICT(id) DO UPDATE SET title=@t,pinned=@p,image_sha=@is,image_source=@src," +
                " pixiv_artwork_id=@aid,updated_at=@ua,message_count=@mc", c, tx))
            {
                cmd.Parameters.AddWithValue("@id", h.Id);
                cmd.Parameters.AddWithValue("@u", (object?)h.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@t", h.Title);
                cmd.Parameters.AddWithValue("@p", h.Pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("@is", (object?)h.ImageSha ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@src", (object?)h.ImageSource ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@aid", (object?)h.PixivArtworkId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ca", h.CreatedAt.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("@ua", h.UpdatedAt.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("@mc", messages.Count);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            using (var del = new SqliteCommand("DELETE FROM messages WHERE session_id=@s", c, tx))
            {
                del.Parameters.AddWithValue("@s", h.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                using var ins = new SqliteCommand(
                    "INSERT INTO messages(session_id,seq,role,content,at,image_sha,artwork_id,artist_id)" +
                    " VALUES(@s,@q,@r,@c,@at,@is,@aid,@arid)", c, tx);
                ins.Parameters.AddWithValue("@s", h.Id);
                ins.Parameters.AddWithValue("@q", i);
                ins.Parameters.AddWithValue("@r", m.Role);
                ins.Parameters.AddWithValue("@c", m.Content);
                ins.Parameters.AddWithValue("@at", m.At.ToUniversalTime().ToString("O"));
                ins.Parameters.AddWithValue("@is", (object?)m.ImageSha ?? DBNull.Value);
                ins.Parameters.AddWithValue("@aid", (object?)m.ArtworkId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@arid", (object?)m.ArtistId ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }
            long rid;
            using (var q = new SqliteCommand("SELECT rowid FROM sessions WHERE id=@i", c, tx))
            {
                q.Parameters.AddWithValue("@i", h.Id);
                rid = (long)(await q.ExecuteScalarAsync(ct))!;
            }
            SyncFts(c, tx, rid, h, messages);
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    private void SyncFts(SqliteConnection c, SqliteTransaction tx, long rid, HoshiSessionHeader h, IReadOnlyList<HoshiMessageRow> messages)
    {
        var joined = string.Join(' ', messages.Select(m => m.Content));
        var tables = TrigramAvailable ? new[] { "hoshi_fts", "hoshi_fts_tri" } : new[] { "hoshi_fts" };
        foreach (var t in tables)
        {
            using (var del = new SqliteCommand($"DELETE FROM {t} WHERE rowid=@r", c, tx))
            {
                del.Parameters.AddWithValue("@r", rid);
                del.ExecuteNonQuery();
            }
            using var ins = new SqliteCommand(
                $"INSERT INTO {t}(rowid, title, content) VALUES(@r,@t,@c)", c, tx);
            ins.Parameters.AddWithValue("@r", rid);
            ins.Parameters.AddWithValue("@t", h.Title);
            ins.Parameters.AddWithValue("@c", joined);
            ins.ExecuteNonQuery();
        }
    }

    public async Task DeleteSessionsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var tables = TrigramAvailable ? new[] { "hoshi_fts", "hoshi_fts_tri" } : new[] { "hoshi_fts" };
            foreach (var id in ids)
            {
                long? rid;
                using (var q = new SqliteCommand("SELECT rowid FROM sessions WHERE id=@i", c, tx))
                {
                    q.Parameters.AddWithValue("@i", id);
                    rid = await q.ExecuteScalarAsync(ct) as long?;
                }
                if (rid.HasValue)
                    foreach (var t in tables)
                    {
                        using var del = new SqliteCommand($"DELETE FROM {t} WHERE rowid=@r", c, tx);
                        del.Parameters.AddWithValue("@r", rid.Value);
                        await del.ExecuteNonQueryAsync(ct);
                    }
                using var d = new SqliteCommand("DELETE FROM sessions WHERE id=@i", c, tx);
                d.Parameters.AddWithValue("@i", id);
                await d.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally { _writeLock.Release(); }
        _ = PruneOrphanImagesAsync(ct);
    }

    public async Task SetPinnedAsync(string id, bool pinned, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = new SqliteCommand("UPDATE sessions SET pinned=@p WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RenameAsync(string id, string title, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = new SqliteCommand("UPDATE sessions SET title=@t WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>FTS search: title + message content. Returns matching session ids.</summary>
    public async Task<List<HoshiSearchHit>> SearchAsync(string? userId, string text, int limit = 50, CancellationToken ct = default)
    {
        var hits = new List<HoshiSearchHit>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        var term = text.Trim();
        using var c = Open();
        bool cjk = CjkRegex.IsMatch(term);
        if (cjk && !(TrigramAvailable && term.Length >= 3))
        {
            using var cmd = new SqliteCommand(
                $"SELECT {HeaderCols} FROM sessions WHERE " +
                (userId == null ? "user_id IS NULL" : "user_id=@u") +
                " AND (title LIKE @q ESCAPE '\\' OR id IN (SELECT session_id FROM messages WHERE content LIKE @q ESCAPE '\\'))" +
                " ORDER BY pinned DESC, updated_at DESC LIMIT @l", c);
            if (userId != null) cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@q", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            cmd.Parameters.AddWithValue("@l", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                hits.Add(new HoshiSearchHit { SessionId = r.GetString(0), Snippet = "" });
            return hits;
        }

        var table = cjk ? "hoshi_fts_tri" : "hoshi_fts";
        var match = cjk
            ? "\"" + term.Replace("\"", "\"\"") + "\""
            : term.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Select(w => "\"" + w.Replace("\"", "\"\"") + "\"")
                  .Aggregate((a, b) => a + " AND " + b);

        // rowid = FNV-1a hash of the session id → two-step lookup.
        var rowids = new List<long>();
        using (var cmd = new SqliteCommand($"SELECT rowid FROM {table} WHERE {table} MATCH @m LIMIT @l", c))
        {
            cmd.Parameters.AddWithValue("@m", match);
            cmd.Parameters.AddWithValue("@l", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rowids.Add(r.GetInt64(0));
        }
        if (rowids.Count == 0) return hits;
        var ridSet = new HashSet<long>(rowids);
        using (var cmd = new SqliteCommand(
            $"SELECT {HeaderCols}, rowid FROM sessions WHERE " + (userId == null ? "user_id IS NULL" : "user_id=@u"), c))
        {
            if (userId != null) cmd.Parameters.AddWithValue("@u", userId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (ridSet.Contains(r.GetInt64(10)))
                    hits.Add(new HoshiSearchHit { SessionId = r.GetString(0) });
        }
        // Snippet: first matching line of message content.
        foreach (var hit in hits)
        {
            using var cmd = new SqliteCommand(
                "SELECT content FROM messages WHERE session_id=@s AND content LIKE @q LIMIT 1", c);
            cmd.Parameters.AddWithValue("@s", hit.SessionId);
            cmd.Parameters.AddWithValue("@q", "%" + term + "%");
            hit.Snippet = await cmd.ExecuteScalarAsync(ct) as string ?? "";
            if (hit.Snippet.Length > 120) hit.Snippet = hit.Snippet[..120];
        }
        return hits;
    }

    // ── stats + cleanup ──────────────────────────────────────────────────────

    public async Task<HoshiStorageStats> GetStorageStatsAsync(CancellationToken ct = default)
    {
        using var c = Open();
        long sessions, dbBytes = new FileInfo(c.DataSource).Length;
        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM sessions", c))
            sessions = (long)(await cmd.ExecuteScalarAsync(ct))!;
        long imgBytes = 0, imgFiles = 0;
        if (Directory.Exists(_imageDir))
            foreach (var f in Directory.EnumerateFiles(_imageDir, "*", SearchOption.AllDirectories))
            { imgFiles++; imgBytes += new FileInfo(f).Length; }
        var legacy = LegacyDataDir() + ".migrated";
        long legacyBytes = 0;
        if (Directory.Exists(legacy))
            foreach (var f in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
                legacyBytes += new FileInfo(f).Length;
        return new HoshiStorageStats(sessions, dbBytes, imgBytes, imgFiles, legacyBytes);
    }

    /// <summary>Deletes image files no longer referenced by any session/message row.</summary>
    public async Task<int> PruneOrphanImagesAsync(CancellationToken ct = default)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        using (var cmd = new SqliteCommand("SELECT image_sha FROM sessions WHERE image_sha IS NOT NULL", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) referenced.Add(r.GetString(0));
        using (var cmd = new SqliteCommand("SELECT DISTINCT image_sha FROM messages WHERE image_sha IS NOT NULL", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) referenced.Add(r.GetString(0));

        var removed = 0;
        var known = new List<string>();
        using (var cmd = new SqliteCommand("SELECT sha FROM images", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) known.Add(r.GetString(0));
        foreach (var sha in known.Where(s => !referenced.Contains(s)))
        {
            var p = ImagePathFor(sha);
            if (p != null) { try { File.Delete(p); } catch { } }
            using var del = new SqliteCommand("DELETE FROM images WHERE sha=@s", c);
            del.Parameters.AddWithValue("@s", sha);
            await del.ExecuteNonQueryAsync(ct);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Retention cleanup: deletes sessions older than maxAgeDays (null = off) and
    /// enforces maxBytes of image storage by deleting oldest unpinned first.
    /// Pinned sessions are always exempt. Returns deleted session ids.
    /// </summary>
    public async Task<List<string>> CleanupAsync(int? maxAgeDays, long? maxImageBytes, CancellationToken ct = default)
    {
        var deleted = new List<string>();
        if (maxAgeDays is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays.Value).ToString("O");
            var ids = new List<string>();
            using (var c = Open())
            using (var cmd = new SqliteCommand("SELECT id FROM sessions WHERE pinned=0 AND updated_at < @c", c))
            {
                cmd.Parameters.AddWithValue("@c", cutoff);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
            }
            await DeleteSessionsAsync(ids, ct);
            deleted.AddRange(ids);
        }
        if (maxImageBytes is > 0)
        {
            // Delete oldest unpinned sessions until image bytes under limit.
            while (!ct.IsCancellationRequested)
            {
                var stats = await GetStorageStatsAsync(ct);
                if (stats.ImageBytes <= maxImageBytes.Value) break;
                string? oldest = null;
                using (var c = Open())
                using (var cmd = new SqliteCommand(
                    "SELECT id FROM sessions WHERE pinned=0 ORDER BY updated_at ASC LIMIT 1", c))
                    oldest = await cmd.ExecuteScalarAsync(ct) as string;
                if (oldest == null) break;
                await DeleteSessionsAsync(new[] { oldest }, ct);
                deleted.Add(oldest);
            }
        }
        await PruneOrphanImagesAsync(ct);
        return deleted;
    }

    public void Dispose() { /* per-call connections */ }
}
