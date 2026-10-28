using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace Pikura.Core.Data;

/// <summary>One row of the local library index — a downloaded file on disk.</summary>
public sealed class LibraryFile
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Root { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Mtime { get; set; }
    public string? Sha256 { get; set; }
    public long? Phash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>'image' | 'video' | 'animation'</summary>
    public string? MediaType { get; set; }
    public string? ArtworkId { get; set; }
    public int? PageIndex { get; set; }
    public string? Title { get; set; }
    public string? ArtistId { get; set; }
    public string? ArtistName { get; set; }
    /// <summary>Space/newline-joined Pixiv tags (original case).</summary>
    public string? Tags { get; set; }
    /// <summary>Space-joined AI tags (denormalized from library_ai_tags for FTS).</summary>
    public string? AiTagsText { get; set; }
    public string? Description { get; set; }
    public int XRestrict { get; set; }
    public int AiType { get; set; }
    public string? CreateDate { get; set; }
    public string AddedAt { get; set; } = string.Empty;
    /// <summary>'download' | 'filename' | 'pixiv' | null</summary>
    public string? MetadataSource { get; set; }
    public string? AiTaggedAt { get; set; }
    /// <summary>Page the file came from (e.g. https://www.pixiv.net/artworks/123).</summary>
    public string? SourceUrl { get; set; }
    /// <summary>Exact image URL that was downloaded.</summary>
    public string? OriginalUrl { get; set; }
    /// <summary>Where the file came from: "pixiv" | "reverse-search" | null.</summary>
    public string? SourceSite { get; set; }
    /// <summary>ISO "O" — set only when Pikura downloaded the file itself.</summary>
    public string? DownloadedAt { get; set; }
    public bool HasSource => SourceUrl != null || ArtworkId != null;
    public bool HasOriginalUrl => OriginalUrl != null;
    /// <summary>phash algorithm version (0/1 = legacy full-decode, 2 = scaled decode).</summary>
    public int PhashVersion { get; set; }

    public bool IsR18 => XRestrict >= 1;
}

public sealed class LibraryAiTag
{
    public string Tag { get; set; } = string.Empty;
    /// <summary>'general' | 'character' | 'copyright' | 'artist' | 'meta'</summary>
    public string Category { get; set; } = "general";
    public double Score { get; set; }
}

public enum LibrarySort { AddedDesc, CreatedDesc, Title, Artist, DateDesc, DateAsc, SizeDesc, NameAsc }

public enum LibraryOrientation { Any, Portrait, Landscape, Square }

public sealed class LibraryQuery
{
    public string? Text { get; set; }
    public string? ArtistId { get; set; }
    /// <summary>Broader artist match: id, name, or folder-segment in the file path.</summary>
    public string? Artist { get; set; }
    public int? Year { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public LibraryOrientation Orientation { get; set; } = LibraryOrientation.Any;
    /// <summary>All must match (space-separated token inside tags column).</summary>
    public List<string> PixivTags { get; set; } = [];
    /// <summary>Filter to files tagged with this exact character name.</summary>
    public string? CharacterTag { get; set; }
    /// <summary>Filter to files tagged with this exact series (copyright) name.</summary>
    public string? SeriesTag { get; set; }
    /// <summary>All must match against library_ai_tags.</summary>
    public List<string> AiTags { get; set; } = [];
    public string? MediaType { get; set; }
    public bool IncludeR18 { get; set; }
    public LibrarySort Sort { get; set; } = LibrarySort.AddedDesc;
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class LibraryStats
{
    public long Files { get; set; }
    public long WithSha256 { get; set; }
    public long WithPhash { get; set; }
    public long MissingMetadata { get; set; }
    public long MissingAiTags { get; set; }
}

/// <summary>A group of duplicate files (exact sha256 or perceptually similar).</summary>
public sealed class DuplicateGroup
{
    public bool IsExact { get; set; }
    /// <summary>Max pairwise distance for near-dup groups (0 for exact).</summary>
    public int MaxDistance { get; set; }
    public List<LibraryFile> Files { get; set; } = [];
}

/// <summary>
/// SQLite repository for the local download library: files on disk, hashes,
/// AI tags, and FTS5 full-text search (unicode61 + trigram for CJK).
/// Lives in the same database file as the other Pikura repositories.
/// </summary>
public sealed class LibraryRepository : IDisposable
{
    private static readonly Regex CjkRegex = new(
        "[\\u3040-\\u30ff\\u3400-\\u9fff\\uf900-\\ufaff\\uac00-\\ud7af\\u4e00-\\u9fff]",
        RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly ILogger<LibraryRepository> _logger;

    /// <summary>True when this SQLite build supports the fts5 trigram tokenizer (verified at startup).</summary>
    public bool TrigramAvailable { get; }

    public LibraryRepository(string dbPath, ILogger<LibraryRepository> logger)
    {
        _logger = logger;
        _connectionString = $"Data Source={dbPath};Foreign Keys=True;Default Timeout=5;";
        EnsureDatabaseCreated();
        TrigramAvailable = ProbeTrigram();
    }

    private void EnsureDatabaseCreated()
    {
        using var connection = CreateConnection();
        connection.Open();
        // Shared downloads.db: WAL so library hashing/scans don't lock out UI reads (persists in the file).
        using (var wal = new SqliteCommand("PRAGMA journal_mode=WAL;", connection)) wal.ExecuteNonQuery();
        var sql = @"
            CREATE TABLE IF NOT EXISTS library_files (
                id INTEGER PRIMARY KEY,
                path TEXT UNIQUE NOT NULL,
                root TEXT NOT NULL,
                size INTEGER,
                mtime TEXT,
                sha256 TEXT,
                phash INTEGER,
                width INTEGER,
                height INTEGER,
                media_type TEXT,
                artwork_id TEXT,
                page_index INTEGER,
                title TEXT,
                artist_id TEXT,
                artist_name TEXT,
                tags TEXT,
                ai_tags_text TEXT,
                description TEXT,
                x_restrict INTEGER DEFAULT 0,
                ai_type INTEGER DEFAULT 0,
                create_date TEXT,
                added_at TEXT NOT NULL,
                metadata_source TEXT,
                ai_tagged_at TEXT,
                phash_version INTEGER NOT NULL DEFAULT 0,
                source_url TEXT,
                original_url TEXT,
                source_site TEXT,
                downloaded_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_library_sha256 ON library_files(sha256);
            CREATE INDEX IF NOT EXISTS idx_library_artwork ON library_files(artwork_id);
            CREATE INDEX IF NOT EXISTS idx_library_artist ON library_files(artist_id);
            CREATE INDEX IF NOT EXISTS idx_library_phash ON library_files(phash);

            CREATE TABLE IF NOT EXISTS library_phash_bands (
                file_id INTEGER NOT NULL,
                band INTEGER NOT NULL,
                value INTEGER NOT NULL,
                PRIMARY KEY(band, value, file_id)
            );

            CREATE TABLE IF NOT EXISTS library_ai_tags (
                file_id INTEGER NOT NULL,
                tag TEXT NOT NULL,
                category TEXT NOT NULL DEFAULT 'general',
                score REAL NOT NULL DEFAULT 0,
                PRIMARY KEY(file_id, tag)
            );
            CREATE INDEX IF NOT EXISTS idx_library_ai_tags_tag ON library_ai_tags(tag);

            CREATE VIRTUAL TABLE IF NOT EXISTS library_fts USING fts5(
                title, artist_name, tags, ai_tags, description, path,
                content='', tokenize='unicode61 remove_diacritics 2');
        ";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
        // Migration: older databases lack phash_version (their rows are version 1 —
        // they were hashed with the legacy full-decode algorithm).
        using (var info = new SqliteCommand("PRAGMA table_info(library_files)", connection))
        {
            var hasCol = false;
            using (var r = info.ExecuteReader())
                while (r.Read())
                    if (r.GetString(1) == "phash_version") { hasCol = true; break; }
            if (!hasCol)
            using (var alter = new SqliteCommand(
                "ALTER TABLE library_files ADD COLUMN phash_version INTEGER NOT NULL DEFAULT 1", connection))
                alter.ExecuteNonQuery();
            var needed = new[] { "source_url", "original_url", "source_site", "downloaded_at" };
            var existing = new System.Collections.Generic.HashSet<string>();
            using (var info2 = new SqliteCommand("PRAGMA table_info(library_files)", connection))
            {
                using var r2 = info2.ExecuteReader();
                while (r2.Read()) existing.Add(r2.GetString(1));
            }
            foreach (var col in needed)
                if (!existing.Contains(col))
                    using (var alter = new SqliteCommand(
                        $"ALTER TABLE library_files ADD COLUMN {col} TEXT", connection))
                        alter.ExecuteNonQuery();
        }
    }

    private bool ProbeTrigram()
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            var sql = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS library_fts_tri USING fts5(
                    title, artist_name, tags, ai_tags, description, path,
                    content='', tokenize='trigram');
            ";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS5 trigram tokenizer unavailable — CJK search will use LIKE fallback");
            return false;
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    // ── Row mapping ──────────────────────────────────────────────────────────

    private static LibraryFile MapRow(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new LibraryFile
        {
            Id = r.GetInt64(0),
            Path = r.GetString(1),
            Root = r.GetString(2),
            Size = r.IsDBNull(3) ? 0 : r.GetInt64(3),
            Mtime = S(4),
            Sha256 = S(5),
            Phash = r.IsDBNull(6) ? null : r.GetInt64(6),
            Width = r.IsDBNull(7) ? 0 : r.GetInt32(7),
            Height = r.IsDBNull(8) ? 0 : r.GetInt32(8),
            MediaType = S(9),
            ArtworkId = S(10),
            PageIndex = r.IsDBNull(11) ? null : r.GetInt32(11),
            Title = S(12),
            ArtistId = S(13),
            ArtistName = S(14),
            Tags = S(15),
            AiTagsText = S(16),
            Description = S(17),
            XRestrict = r.IsDBNull(18) ? 0 : r.GetInt32(18),
            AiType = r.IsDBNull(19) ? 0 : r.GetInt32(19),
            CreateDate = S(20),
            AddedAt = r.GetString(21),
            MetadataSource = S(22),
            AiTaggedAt = S(23),
            PhashVersion = r.IsDBNull(24) ? 1 : r.GetInt32(24),
            SourceUrl = S(25),
            OriginalUrl = S(26),
            SourceSite = S(27),
            DownloadedAt = S(28),
        };
    }

    private const string SelectColumns =
        "id, path, root, size, mtime, sha256, phash, width, height, media_type, artwork_id, page_index," +
        " title, artist_id, artist_name, tags, ai_tags_text, description, x_restrict, ai_type, create_date," +
        " added_at, metadata_source, ai_tagged_at, phash_version," +
        " source_url, original_url, source_site, downloaded_at";

    private static void BindFile(SqliteCommand cmd, LibraryFile f)
    {
        object? N(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : s;
        cmd.Parameters.AddWithValue("@path", f.Path);
        cmd.Parameters.AddWithValue("@root", f.Root);
        cmd.Parameters.AddWithValue("@size", f.Size);
        cmd.Parameters.AddWithValue("@mtime", (object?)f.Mtime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sha256", N(f.Sha256));
        cmd.Parameters.AddWithValue("@phash", (object?)f.Phash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@width", f.Width);
        cmd.Parameters.AddWithValue("@height", f.Height);
        cmd.Parameters.AddWithValue("@media_type", N(f.MediaType));
        cmd.Parameters.AddWithValue("@artwork_id", N(f.ArtworkId));
        cmd.Parameters.AddWithValue("@page_index", (object?)f.PageIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", N(f.Title));
        cmd.Parameters.AddWithValue("@artist_id", N(f.ArtistId));
        cmd.Parameters.AddWithValue("@artist_name", N(f.ArtistName));
        cmd.Parameters.AddWithValue("@tags", N(f.Tags));
        cmd.Parameters.AddWithValue("@ai_tags_text", N(f.AiTagsText));
        cmd.Parameters.AddWithValue("@description", N(f.Description));
        cmd.Parameters.AddWithValue("@x_restrict", f.XRestrict);
        cmd.Parameters.AddWithValue("@ai_type", f.AiType);
        cmd.Parameters.AddWithValue("@create_date", N(f.CreateDate));
        cmd.Parameters.AddWithValue("@added_at", f.AddedAt);
        cmd.Parameters.AddWithValue("@metadata_source", N(f.MetadataSource));
        cmd.Parameters.AddWithValue("@ai_tagged_at", N(f.AiTaggedAt));
        cmd.Parameters.AddWithValue("@phash_version", f.PhashVersion);
        cmd.Parameters.AddWithValue("@source_url", N(f.SourceUrl));
        cmd.Parameters.AddWithValue("@original_url", N(f.OriginalUrl));
        cmd.Parameters.AddWithValue("@source_site", N(f.SourceSite));
        cmd.Parameters.AddWithValue("@downloaded_at", N(f.DownloadedAt));
    }

    // ── FTS sync (contentless tables need explicit delete with old values) ────

    private void FtsDelete(SqliteConnection c, SqliteTransaction tx, string table, long id, LibraryFile old)
    {
        using var cmd = new SqliteCommand(
            $"INSERT INTO {table}({table}, rowid, title, artist_name, tags, ai_tags, description, path)" +
            " VALUES('delete', @id, @t, @a, @g, @ai, @d, @p)", c, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@t", (object?)old.Title ?? "");
        cmd.Parameters.AddWithValue("@a", (object?)old.ArtistName ?? "");
        cmd.Parameters.AddWithValue("@g", (object?)old.Tags ?? "");
        cmd.Parameters.AddWithValue("@ai", (object?)old.AiTagsText ?? "");
        cmd.Parameters.AddWithValue("@d", (object?)old.Description ?? "");
        cmd.Parameters.AddWithValue("@p", old.Path);
        cmd.ExecuteNonQuery();
    }

    private void FtsInsert(SqliteConnection c, SqliteTransaction tx, string table, long id, LibraryFile f)
    {
        using var cmd = new SqliteCommand(
            $"INSERT INTO {table}(rowid, title, artist_name, tags, ai_tags, description, path)" +
            " VALUES(@id, @t, @a, @g, @ai, @d, @p)", c, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@t", (object?)f.Title ?? "");
        cmd.Parameters.AddWithValue("@a", (object?)f.ArtistName ?? "");
        cmd.Parameters.AddWithValue("@g", (object?)f.Tags ?? "");
        cmd.Parameters.AddWithValue("@ai", (object?)f.AiTagsText ?? "");
        cmd.Parameters.AddWithValue("@d", (object?)f.Description ?? "");
        cmd.Parameters.AddWithValue("@p", f.Path);
        cmd.ExecuteNonQuery();
    }

    private void SyncFts(SqliteConnection c, SqliteTransaction tx, long id, LibraryFile? old, LibraryFile? next)
    {
        var tables = TrigramAvailable ? new[] { "library_fts", "library_fts_tri" } : new[] { "library_fts" };
        foreach (var t in tables)
        {
            if (old != null) FtsDelete(c, tx, t, id, old);
            if (next != null) FtsInsert(c, tx, t, id, next);
        }
    }

    private static void SyncPhashBands(SqliteConnection c, SqliteTransaction tx, long fileId, long? phash)
    {
        using var del = new SqliteCommand("DELETE FROM library_phash_bands WHERE file_id=@id", c, tx);
        del.Parameters.AddWithValue("@id", fileId);
        del.ExecuteNonQuery();
        if (phash is not { } h) return;
        var u = unchecked((ulong)h);
        for (var band = 0; band < 8; band++)
        {
            using var ins = new SqliteCommand(
                "INSERT OR IGNORE INTO library_phash_bands(file_id,band,value) VALUES(@id,@b,@v)", c, tx);
            ins.Parameters.AddWithValue("@id", fileId);
            ins.Parameters.AddWithValue("@b", band);
            ins.Parameters.AddWithValue("@v", unchecked((long)((u >> (band * 8)) & 0xFF)));
            ins.ExecuteNonQuery();
        }
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    /// <summary>Inserts or updates a file row (keyed on path) and keeps FTS/bands in sync. Returns the row id.</summary>
    public async Task<long> UpsertFileAsync(LibraryFile file, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            var existing = await GetByPathAsync(c, tx, file.Path, ct);
            long id;
            if (existing != null)
            {
                using var cmd = new SqliteCommand(@"
                    UPDATE library_files SET root=@root, size=@size, mtime=@mtime, sha256=@sha256, phash=@phash,
                        width=@width, height=@height, media_type=@media_type, artwork_id=@artwork_id,
                        page_index=@page_index, title=@title, artist_id=@artist_id, artist_name=@artist_name,
                        tags=@tags, ai_tags_text=@ai_tags_text, description=@description, x_restrict=@x_restrict,
                        ai_type=@ai_type, create_date=@create_date, metadata_source=@metadata_source,
                        ai_tagged_at=@ai_tagged_at, phash_version=@phash_version,
                        source_url=COALESCE(@source_url, source_url), original_url=COALESCE(@original_url, original_url),
                        source_site=COALESCE(@source_site, source_site), downloaded_at=COALESCE(@downloaded_at, downloaded_at)
                    WHERE id=@id", c, tx);
                cmd.Parameters.AddWithValue("@id", existing.Id);
                BindFile(cmd, file);
                await cmd.ExecuteNonQueryAsync(ct);
                id = existing.Id;
            }
            else
            {
                using var cmd = new SqliteCommand(@"
                    INSERT INTO library_files (path, root, size, mtime, sha256, phash, width, height, media_type,
                        artwork_id, page_index, title, artist_id, artist_name, tags, ai_tags_text, description,
                        x_restrict, ai_type, create_date, added_at, metadata_source, ai_tagged_at, phash_version,
                        source_url, original_url, source_site, downloaded_at)
                    VALUES (@path, @root, @size, @mtime, @sha256, @phash, @width, @height, @media_type,
                        @artwork_id, @page_index, @title, @artist_id, @artist_name, @tags, @ai_tags_text, @description,
                        @x_restrict, @ai_type, @create_date, @added_at, @metadata_source, @ai_tagged_at, @phash_version,
                        @source_url, @original_url, @source_site, @downloaded_at);
                    SELECT last_insert_rowid();", c, tx);
                BindFile(cmd, file);
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            SyncFts(c, tx, id, existing, file);
            SyncPhashBands(c, tx, id, file.Phash);
            tx.Commit();
            return id;
        }
        catch { tx.Rollback(); throw; }
    }

    /// <summary>Batch upsert in one transaction — returns ids aligned to input order.</summary>
    public async Task<List<long>> UpsertFilesAsync(IReadOnlyList<LibraryFile> files, CancellationToken ct = default)
    {
        var ids = new List<long>(files.Count);
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            foreach (var file in files)
            {
                var existing = await GetByPathAsync(c, tx, file.Path, ct);
                long id;
                if (existing != null)
                {
                    using var cmd = new SqliteCommand(@"
                        UPDATE library_files SET root=@root, size=@size, mtime=@mtime, sha256=@sha256, phash=@phash,
                            width=@width, height=@height, media_type=@media_type, artwork_id=@artwork_id,
                            page_index=@page_index, title=@title, artist_id=@artist_id, artist_name=@artist_name,
                            tags=@tags, ai_tags_text=@ai_tags_text, description=@description, x_restrict=@x_restrict,
                            ai_type=@ai_type, create_date=@create_date, metadata_source=@metadata_source,
                            ai_tagged_at=@ai_tagged_at, phash_version=@phash_version,
                            source_url=COALESCE(@source_url, source_url), original_url=COALESCE(@original_url, original_url),
                            source_site=COALESCE(@source_site, source_site), downloaded_at=COALESCE(@downloaded_at, downloaded_at)
                        WHERE id=@id", c, tx);
                    cmd.Parameters.AddWithValue("@id", existing.Id);
                    BindFile(cmd, file);
                    await cmd.ExecuteNonQueryAsync(ct);
                    id = existing.Id;
                }
                else
                {
                    using var cmd = new SqliteCommand(@"
                        INSERT INTO library_files (path, root, size, mtime, sha256, phash, width, height, media_type,
                            artwork_id, page_index, title, artist_id, artist_name, tags, ai_tags_text, description,
                            x_restrict, ai_type, create_date, added_at, metadata_source, ai_tagged_at, phash_version,
                            source_url, original_url, source_site, downloaded_at)
                        VALUES (@path, @root, @size, @mtime, @sha256, @phash, @width, @height, @media_type,
                            @artwork_id, @page_index, @title, @artist_id, @artist_name, @tags, @ai_tags_text, @description,
                            @x_restrict, @ai_type, @create_date, @added_at, @metadata_source, @ai_tagged_at, @phash_version,
                            @source_url, @original_url, @source_site, @downloaded_at);
                        SELECT last_insert_rowid();", c, tx);
                    BindFile(cmd, file);
                    id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                }
                SyncFts(c, tx, id, existing, file);
                SyncPhashBands(c, tx, id, file.Phash);
                file.Id = id;
                ids.Add(id);
            }
            tx.Commit();
            return ids;
        }
        catch { tx.Rollback(); throw; }
    }

    public async Task DeleteByPathAsync(string path, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            var existing = await GetByPathAsync(c, tx, path, ct);
            if (existing != null)
            {
                SyncFts(c, tx, existing.Id, existing, null);
                SyncPhashBands(c, tx, existing.Id, null);
                using var cmd = new SqliteCommand("DELETE FROM library_files WHERE id=@id", c, tx);
                cmd.Parameters.AddWithValue("@id", existing.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    /// <summary>Deletes every row whose <c>root</c> matches (case-insensitive), keeping
    /// FTS and phash bands in sync inside one transaction. Used when a library folder
    /// is removed from the configured list.</summary>
    public async Task<int> DeleteRowsUnderRootAsync(string root, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            var ids = new List<(long Id, LibraryFile F)>();
            using (var sel = new SqliteCommand("SELECT id FROM library_files WHERE root = @root COLLATE NOCASE", c, tx))
            {
                sel.Parameters.AddWithValue("@root", root);
                var raw = new List<long>();
                await using var rd = await sel.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) raw.Add(rd.GetInt64(0));
                rd.Close();
                foreach (var id in raw)
                {
                    var f = await GetByIdAsync(id, ct);
                    if (f != null) ids.Add((id, f));
                }
            }
            foreach (var (id, f) in ids)
            {
                SyncFts(c, tx, id, f, null);
                SyncPhashBands(c, tx, id, null);
            }
            using var del = new SqliteCommand("DELETE FROM library_files WHERE root = @root COLLATE NOCASE", c, tx);
            del.Parameters.AddWithValue("@root", root);
            var n = await del.ExecuteNonQueryAsync(ct);
            tx.Commit();
            return n;
        }
        catch { tx.Rollback(); throw; }
    }

    public async Task DeleteByIdAsync(long id, CancellationToken ct = default)
    {
        var f = await GetByIdAsync(id, ct);
        if (f != null) await DeleteByPathAsync(f.Path, ct);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    private static async Task<LibraryFile?> GetByPathAsync(SqliteConnection c, SqliteTransaction tx, string path, CancellationToken ct)
    {
        using var cmd = new SqliteCommand($"SELECT {SelectColumns} FROM library_files WHERE path=@p COLLATE NOCASE", c, tx);
        cmd.Parameters.AddWithValue("@p", path);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapRow(r) : null;
    }

    /// <summary>Path of a local file for a pixiv artwork (lowest page first), or null.</summary>
    public async Task<string?> GetPathForArtworkAsync(string artworkId, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT path FROM library_files WHERE artwork_id=@a ORDER BY COALESCE(page_index,0) LIMIT 1", c);
        cmd.Parameters.AddWithValue("@a", artworkId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<LibraryFile?> GetByPathAsync(string path, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        return await GetByPathAsync(c, null!, path, ct);
    }

    public async Task<LibraryFile?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand($"SELECT {SelectColumns} FROM library_files WHERE id=@id", c);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapRow(r) : null;
    }

    /// <summary>path → (size,mtime) for incremental scanning of one root.</summary>
    public async Task<Dictionary<string, (long Size, string? Mtime)>> GetAllPathsWithStampAsync(string root, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (long, string?)>(StringComparer.OrdinalIgnoreCase);
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand("SELECT path, size, mtime FROM library_files WHERE root=@r", c);
        cmd.Parameters.AddWithValue("@r", root);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            map[rd.GetString(0)] = (rd.IsDBNull(1) ? 0 : rd.GetInt64(1), rd.IsDBNull(2) ? null : rd.GetString(2));
        return map;
    }

    // ── Search ──────────────────────────────────────────────────────────────

    private static string FtsEscape(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    private void BuildWhere(LibraryQuery q, List<string> clauses, List<SqliteParameter> prms)
    {
        var i = prms.Count;
        // Tags catch rows whose pixiv R-18/R-18G tag arrived without x_restrict (older imports).
        if (!q.IncludeR18) clauses.Add("x_restrict < 1 AND COALESCE(tags,'') NOT LIKE '%R-18%'");
        if (!string.IsNullOrEmpty(q.ArtistId)) { clauses.Add($"artist_id=@p{i}"); prms.Add(new($"@p{i}", q.ArtistId)); i++; }
        if (!string.IsNullOrEmpty(q.Artist))
        {
            // id OR name OR a path folder-segment (files whose metadata is null still
            // match the artist folder they were downloaded into).
            // ESCAPE '!' (not the file separator '\') so segment backslashes stay literal.
            var seg = q.Artist.Replace("!", "").Replace("%", "!%").Replace("_", "!_");
            clauses.Add($"(artist_id=@p{i} OR artist_name LIKE @p{i + 1} ESCAPE '!' OR " +
                        $"path LIKE @p{i + 2} ESCAPE '!' OR path LIKE @p{i + 3} ESCAPE '!')");
            prms.Add(new($"@p{i}", q.Artist));
            prms.Add(new($"@p{i + 1}", "%" + seg + "%"));
            prms.Add(new($"@p{i + 2}", "%\\" + seg + "\\%")); // ...\artist\...
            prms.Add(new($"@p{i + 3}", "%\\" + seg));           // root itself is the artist folder
            i += 4;
        }
        if (q.Year is { } year)
        {
            clauses.Add($"strftime('%Y', COALESCE(create_date, mtime))=@p{i}");
            prms.Add(new($"@p{i}", year.ToString()));
            i++;
        }
        if (q.DateFrom is { } df) { clauses.Add($"date(COALESCE(create_date, mtime)) >= @p{i}"); prms.Add(new($"@p{i}", df.ToString("yyyy-MM-dd"))); i++; }
        if (q.DateTo is { } dt)   { clauses.Add($"date(COALESCE(create_date, mtime)) <= @p{i}"); prms.Add(new($"@p{i}", dt.ToString("yyyy-MM-dd"))); i++; }
        switch (q.Orientation)
        {
            case LibraryOrientation.Portrait:  clauses.Add("height > width"); break;
            case LibraryOrientation.Landscape: clauses.Add("width > height"); break;
            case LibraryOrientation.Square:    clauses.Add("ABS(width - height) <= 0.02 * MAX(width, height)"); break;
        }
        if (!string.IsNullOrEmpty(q.MediaType)) { clauses.Add($"media_type=@p{i}"); prms.Add(new($"@p{i}", q.MediaType)); i++; }
        foreach (var t in q.PixivTags)
        {
            clauses.Add($"(' ' || COALESCE(tags,'') || ' ') LIKE @p{i} ESCAPE '\\'");
            prms.Add(new($"@p{i}", "% " + EscapeLike(t) + " %"));
            i++;
        }
        foreach (var t in q.AiTags)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM library_ai_tags lt WHERE lt.file_id=library_files.id AND lt.tag=@p{i})");
            prms.Add(new($"@p{i}", t));
            i++;
        }
        if (!string.IsNullOrEmpty(q.CharacterTag))
        {
            clauses.Add($"EXISTS (SELECT 1 FROM library_ai_tags lc WHERE lc.file_id=library_files.id AND lc.tag=@p{i} AND lc.category='character')");
            prms.Add(new($"@p{i}", q.CharacterTag));
            i++;
        }
        if (!string.IsNullOrEmpty(q.SeriesTag))
        {
            clauses.Add($"EXISTS (SELECT 1 FROM library_ai_tags ls WHERE ls.file_id=library_files.id AND ls.tag=@p{i} AND ls.category='copyright')");
            prms.Add(new($"@p{i}", q.SeriesTag));
            i++;
        }

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var term = q.Text.Trim();
            if (CjkRegex.IsMatch(term))
            {
                if (TrigramAvailable && term.Length >= 3)
                {
                    clauses.Add($"id IN (SELECT rowid FROM library_fts_tri WHERE library_fts_tri MATCH @p{i})");
                    prms.Add(new($"@p{i}", FtsEscape(term)));
                }
                else
                {
                    // LIKE fallback across the searchable columns.
                    clauses.Add($"(title LIKE @p{i} OR artist_name LIKE @p{i} OR tags LIKE @p{i} OR ai_tags_text LIKE @p{i} OR path LIKE @p{i})");
                    prms.Add(new($"@p{i}", "%" + EscapeLike(term) + "%"));
                }
            }
            else
            {
                var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var match = string.Join(" AND ", words.Select(FtsEscape));
                // FTS OR filename LIKE — partial names ("12345", "_p0", "miku") aren't indexed terms.
                clauses.Add($"(id IN (SELECT rowid FROM library_fts WHERE library_fts MATCH @p{i}) OR path LIKE @p{i + 1} ESCAPE '\\')");
                prms.Add(new($"@p{i}", match));
                prms.Add(new($"@p{i + 1}", "%" + EscapeLike(term) + "%"));
            }
        }
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Top-N AI tags in a category ("character"/"copyright"/"general") by file count.</summary>
    public async Task<List<(string Tag, int Count)>> GetTopTagsByCategoryAsync(string category, int limit = 200, CancellationToken ct = default)
    {
        var list = new List<(string, int)>();
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT tag, COUNT(*) FROM library_ai_tags WHERE category=@c " +
            "GROUP BY tag ORDER BY COUNT(*) DESC LIMIT @l", c);
        cmd.Parameters.AddWithValue("@c", category);
        cmd.Parameters.AddWithValue("@l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), (int)r.GetInt64(1)));
        return list;
    }

    /// <summary>Runs a query; returns (page of items, total count).</summary>
    public async Task<(IReadOnlyList<LibraryFile> Items, long Total)> SearchAsync(LibraryQuery q, CancellationToken ct = default)
    {
        var clauses = new List<string>();
        var prms = new List<SqliteParameter>();
        BuildWhere(q, clauses, prms);
        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
        var order = q.Sort switch
        {
            LibrarySort.CreatedDesc => " ORDER BY create_date DESC NULLS LAST, id DESC",
            LibrarySort.Title => " ORDER BY title COLLATE NOCASE ASC, id",
            LibrarySort.Artist => " ORDER BY artist_name COLLATE NOCASE ASC, title COLLATE NOCASE, id",
            LibrarySort.DateDesc => " ORDER BY COALESCE(create_date, mtime) DESC, id DESC",
            LibrarySort.DateAsc => " ORDER BY COALESCE(create_date, mtime) ASC, id",
            LibrarySort.SizeDesc => " ORDER BY size DESC, id DESC",
            LibrarySort.NameAsc => " ORDER BY path COLLATE NOCASE ASC, id",
            _ => " ORDER BY added_at DESC, id DESC",
        };

        using var c = CreateConnection();
        await c.OpenAsync(ct);

        long total;
        using (var cmd = new SqliteCommand($"SELECT COUNT(*) FROM library_files{where}", c))
        {
            cmd.Parameters.AddRange(prms.ToArray());
            total = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        var items = new List<LibraryFile>();
        using (var cmd = new SqliteCommand($"SELECT {SelectColumns} FROM library_files{where}{order} LIMIT @lim OFFSET @off", c))
        {
            cmd.Parameters.AddRange(prms.ToArray());
            cmd.Parameters.AddWithValue("@lim", q.Limit);
            cmd.Parameters.AddWithValue("@off", q.Offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(MapRow(r));
        }
        return (items, total);
    }

    /// <summary>Rows whose age rating is unknown (no pixiv metadata yet), so the R-18 filter can't judge them.</summary>
    public async Task<long> CountUnratedAsync(CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM library_files WHERE COALESCE(metadata_source,'') NOT IN ('pixiv','download')", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Distinct years present in the index (COALESCE(create_date, mtime)), DESC.</summary>
    public async Task<IReadOnlyList<int>> GetYearsAsync(CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT DISTINCT strftime('%Y', COALESCE(create_date, mtime)) y FROM library_files WHERE y IS NOT NULL ORDER BY y DESC", c);
        var years = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (int.TryParse(r.GetString(0), out var y)) years.Add(y);
        return years;
    }

    /// <summary>Fills in the artwork id (reverse-search hit) — updates the row and re-syncs FTS.</summary>
    public async Task SetArtworkIdAsync(long fileId, string artworkId, string? title = null,
        string? sourceUrl = null, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            var f = await GetByIdAsync(fileId, ct);
            if (f == null) { tx.Rollback(); return; }
            f.ArtworkId = artworkId;
            if (title != null && f.Title == null) f.Title = title;
            using var cmd = new SqliteCommand(
                "UPDATE library_files SET artwork_id=@a, title=COALESCE(@t, title), " +
                "source_url=COALESCE(source_url, @src), source_site=COALESCE(source_site, 'reverse-search') " +
                "WHERE id=@id", c, tx);
            cmd.Parameters.AddWithValue("@a", artworkId);
            cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@src", (object?)sourceUrl ?? $"https://www.pixiv.net/artworks/{artworkId}");
            cmd.Parameters.AddWithValue("@id", fileId);
            await cmd.ExecuteNonQueryAsync(ct);
            SyncFts(c, tx, fileId, null, f);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    private static SqliteParameter[] Clone(SqliteParameter[] p) =>
        p.Select(x => new SqliteParameter(x.ParameterName, x.Value)).ToArray();

    // ── AI tags ─────────────────────────────────────────────────────────────

    /// <summary>Replaces a file's AI tags; updates the denormalized ai_tags_text + FTS.</summary>
    public async Task SetAiTagsAsync(long fileId, IReadOnlyList<LibraryAiTag> tags, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            LibraryFile? f;
            using (var get = new SqliteCommand($"SELECT {SelectColumns} FROM library_files WHERE id=@id", c, tx))
            {
                get.Parameters.AddWithValue("@id", fileId);
                await using var r = await get.ExecuteReaderAsync(ct);
                f = await r.ReadAsync(ct) ? MapRow(r) : null;
            }
            if (f == null) { tx.Rollback(); return; }

            var old = new LibraryFile { Title = f.Title, ArtistName = f.ArtistName, Tags = f.Tags,
                AiTagsText = f.AiTagsText, Description = f.Description, Path = f.Path };

            using (var del = new SqliteCommand("DELETE FROM library_ai_tags WHERE file_id=@id", c, tx))
            {
                del.Parameters.AddWithValue("@id", fileId);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var t in tags)
            {
                using var ins = new SqliteCommand(
                    "INSERT OR REPLACE INTO library_ai_tags(file_id,tag,category,score) VALUES(@id,@tag,@cat,@sc)", c, tx);
                ins.Parameters.AddWithValue("@id", fileId);
                ins.Parameters.AddWithValue("@tag", t.Tag);
                ins.Parameters.AddWithValue("@cat", t.Category);
                ins.Parameters.AddWithValue("@sc", t.Score);
                await ins.ExecuteNonQueryAsync(ct);
            }

            var joined = string.Join(' ', tags.Select(t => t.Tag));
            using (var upd = new SqliteCommand(
                "UPDATE library_files SET ai_tags_text=@txt, ai_tagged_at=@at WHERE id=@id", c, tx))
            {
                upd.Parameters.AddWithValue("@txt", joined);
                upd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
                upd.Parameters.AddWithValue("@id", fileId);
                await upd.ExecuteNonQueryAsync(ct);
            }

            f.AiTagsText = joined;
            SyncFts(c, tx, fileId, old, f);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public async Task<IReadOnlyList<(string Tag, long Count)>> GetTopAiTagsAsync(int limit, CancellationToken ct = default)
    {
        var list = new List<(string, long)>();
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT tag, COUNT(*) c FROM library_ai_tags GROUP BY tag ORDER BY c DESC, tag LIMIT @l", c);
        cmd.Parameters.AddWithValue("@l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    /// <summary>Most common Pixiv tag tokens across the index (split of the tags column).</summary>
    public async Task<IReadOnlyList<(string Tag, long Count)>> GetTopPixivTagsAsync(int limit, CancellationToken ct = default)
    {
        // SQLite has no string-split table function; pull tags columns and count in memory.
        // The index is local files only, so this stays cheap.
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand("SELECT tags FROM library_files WHERE tags IS NOT NULL AND tags<>''", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            foreach (var t in r.GetString(0).Split((char[]?)[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(limit).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ── Duplicates ──────────────────────────────────────────────────────────

    /// <summary>Groups of files sharing the same sha256 (≥2 files).</summary>
    public async Task<IReadOnlyList<DuplicateGroup>> GetExactDuplicateGroupsAsync(CancellationToken ct = default)
    {
        var groups = new List<DuplicateGroup>();
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand($@"
            SELECT {SelectColumns} FROM library_files
            WHERE sha256 IN (SELECT sha256 FROM library_files WHERE sha256 IS NOT NULL AND sha256<>''
                             GROUP BY sha256 HAVING COUNT(*)>1)
            ORDER BY sha256, added_at", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? current = null;
        DuplicateGroup? g = null;
        while (await r.ReadAsync(ct))
        {
            var f = MapRow(r);
            if (f.Sha256 != current) { current = f.Sha256; g = new DuplicateGroup { IsExact = true }; groups.Add(g); }
            g!.Files.Add(f);
        }
        return groups;
    }

    /// <summary>
    /// Near-duplicate groups via 8×8-bit band indexing + Hamming verify + union-find.
    /// Exact-sha256-only groups are excluded (they're reported as exact).
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> GetNearDuplicateGroupsAsync(int threshold, CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);

        // Load all phashed files.
        var files = new Dictionary<long, LibraryFile>();
        using (var cmd = new SqliteCommand(
            $"SELECT {SelectColumns} FROM library_files WHERE phash IS NOT NULL AND phash_version >= 2", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { var f = MapRow(r); files[f.Id] = f; }
        if (files.Count < 2) return [];

        // Candidate pairs via band collision.
        var pairCandidates = new Dictionary<long, HashSet<long>>();
        using (var cmd = new SqliteCommand(@"
            SELECT a.file_id, b.file_id FROM library_phash_bands a
            JOIN library_phash_bands b ON a.band=b.band AND a.value=b.value AND a.file_id < b.file_id", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var a = r.GetInt64(0); var b = r.GetInt64(1);
                (pairCandidates.TryGetValue(a, out var s) ? s : pairCandidates[a] = []).Add(b);
            }

        // Verify with real Hamming distance; skip same-sha256 pairs (exact dups).
        var parent = new UnionFind(files.Keys);
        var maxDist = new Dictionary<long, int>();
        foreach (var (a, bs) in pairCandidates)
            foreach (var b in bs)
            {
                var fa = files[a]; var fb = files[b];
                if (!string.IsNullOrEmpty(fa.Sha256) && fa.Sha256 == fb.Sha256) continue;
                var d = PerceptualHashDistance(fa.Phash!.Value, fb.Phash!.Value);
                if (d > threshold) continue;
                parent.Union(a, b);
                maxDist[a] = Math.Max(maxDist.GetValueOrDefault(a), d);
                maxDist[b] = Math.Max(maxDist.GetValueOrDefault(b), d);
            }

        var groups = new Dictionary<long, DuplicateGroup>();
        foreach (var id in files.Keys)
        {
            var root = parent.Find(id);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = new DuplicateGroup();
            g.Files.Add(files[id]);
            g.MaxDistance = Math.Max(g.MaxDistance, maxDist.GetValueOrDefault(id));
        }
        return groups.Values.Where(g => g.Files.Count >= 2)
            .Select(g => { g.Files.Sort((x, y) => string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase)); return g; })
            .ToList();
    }

    private static int PerceptualHashDistance(long a, long b) =>
        System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _p;
        public UnionFind(IEnumerable<long> keys) => _p = keys.ToDictionary(k => k, k => k);
        public long Find(long x) => _p[x] == x ? x : _p[x] = Find(_p[x]);
        public void Union(long a, long b) => _p[Find(a)] = Find(b);
    }

    // ── Maintenance queries ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<LibraryFile>> GetFilesMissingMetadataAsync(CancellationToken ct = default) =>
        await QueryAllAsync("WHERE artwork_id IS NOT NULL AND metadata_source='filename'", ct);

    public async Task<IReadOnlyList<LibraryFile>> GetFilesMissingAiTagsAsync(CancellationToken ct = default) =>
        await QueryAllAsync("WHERE media_type='image' AND ai_tagged_at IS NULL", ct);

    public async Task<IReadOnlyList<LibraryFile>> GetFilesWithoutHashAsync(CancellationToken ct = default) =>
        await QueryAllAsync("WHERE sha256 IS NULL", ct);

    /// <summary>Rows hashed by the legacy v1 algorithm — they get re-fingerprinted.</summary>
    public async Task<IReadOnlyList<LibraryFile>> GetFilesNeedingRehashAsync(CancellationToken ct = default) =>
        await QueryAllAsync("WHERE phash IS NOT NULL AND phash_version < 2", ct);

    /// <summary>Count of v1 rows still awaiting re-fingerprint (for the Duplicates tab note).</summary>
    public async Task<long> GetPendingRehashCountAsync(CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM library_files WHERE phash IS NOT NULL AND phash_version < 2", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFile>> GetAllAsync(CancellationToken ct = default) =>
        await QueryAllAsync("", ct);

    private async Task<IReadOnlyList<LibraryFile>> QueryAllAsync(string where, CancellationToken ct)
    {
        var list = new List<LibraryFile>();
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand($"SELECT {SelectColumns} FROM library_files {where}", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(MapRow(r));
        return list;
    }

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var c = CreateConnection();
        await c.OpenAsync(ct);
        var s = new LibraryStats();
        using var cmd = new SqliteCommand(@"
            SELECT COUNT(*),
                   SUM(CASE WHEN sha256 IS NOT NULL AND sha256<>'' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN phash IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN artwork_id IS NOT NULL AND metadata_source='filename' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN media_type='image' AND ai_tagged_at IS NULL THEN 1 ELSE 0 END)
            FROM library_files", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            s.Files = r.GetInt64(0);
            s.WithSha256 = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            s.WithPhash = r.IsDBNull(2) ? 0 : r.GetInt64(2);
            s.MissingMetadata = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            s.MissingAiTags = r.IsDBNull(4) ? 0 : r.GetInt64(4);
        }
        return s;
    }

    /// <summary>Deletes rows whose path lives under <paramref name="root"/> but isn't in <paramref name="keepPaths"/>.</summary>
    public async Task<int> DeleteRowsNotInAsync(string root, ISet<string> keepPaths, CancellationToken ct = default)
    {
        var existing = await GetAllPathsWithStampAsync(root, ct);
        var removed = 0;
        foreach (var path in existing.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (keepPaths.Contains(path)) continue;
            await DeleteByPathAsync(path, ct);
            removed++;
        }
        return removed;
    }

    /// <summary>Updates metadata columns for an enrichment row (keeps hash/phash/etc.).</summary>
    public async Task UpdateMetadataAsync(long id, string? title, string? artistId, string? artistName,
        string? tags, string? description, int xRestrict, int aiType, string? createDate,
        string metadataSource, CancellationToken ct = default)
    {
        var f = await GetByIdAsync(id, ct);
        if (f == null) return;
        f.Title = title; f.ArtistId = artistId; f.ArtistName = artistName;
        f.Tags = tags; f.Description = description;
        f.XRestrict = xRestrict; f.AiType = aiType; f.CreateDate = createDate;
        f.MetadataSource = metadataSource;
        await UpsertFileAsync(f, ct);
    }

    public void Dispose() { /* connections are per-call; nothing to release */ }
}
