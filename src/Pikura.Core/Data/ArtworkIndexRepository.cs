using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Data;

/// <summary>
/// A single artwork indexed from a followed artist's catalogue, used for
/// local full-text/tag search without hitting Pixiv on every query.
/// </summary>
public sealed class IndexedArtwork
{
    public string ArtworkId { get; set; } = string.Empty;
    public string ArtistUserId { get; set; } = string.Empty;
    public string ArtistUserName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<string> Tags { get; set; } = [];
    public string? ThumbnailUrl { get; set; }
    public int IllustType { get; set; }
    public int XRestrict { get; set; }
    public int AiType { get; set; }
    public int PageCount { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset? CreateDate { get; set; }
}

/// <summary>Per-artist indexing state — when they were last fully crawled.</summary>
public sealed class ArtistIndexState
{
    public string ArtistUserId { get; set; } = string.Empty;
    public string ArtistUserName { get; set; } = string.Empty;
    public DateTime? LastIndexedAt { get; set; }
    public int TotalWorks { get; set; }
}

/// <summary>
/// SQLite repository for the followed-artists artwork search index.
/// Lives in the same database file as the other Pikura repositories.
/// </summary>
public sealed class ArtworkIndexRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ArtworkIndexRepository> _logger;

    public ArtworkIndexRepository(string dbPath, ILogger<ArtworkIndexRepository> logger)
    {
        _logger = logger;
        // Busy timeout so a concurrent writer (periodic crawl vs. an incremental
        // per-artist refresh triggered by ArtistMonitorService) retries for up to
        // 5s instead of immediately throwing "database is locked".
        _connectionString = $"Data Source={dbPath};Foreign Keys=True;Default Timeout=5;";
        EnsureDatabaseCreated();
    }

    private void EnsureDatabaseCreated()
    {
        using var connection = CreateConnection();
        connection.Open();

        var createTable = @"
            CREATE TABLE IF NOT EXISTS artwork_index (
                artwork_id TEXT PRIMARY KEY,
                artist_user_id TEXT NOT NULL,
                artist_user_name TEXT NOT NULL,
                title TEXT NOT NULL,
                tags TEXT NOT NULL,
                thumbnail_url TEXT,
                illust_type INTEGER DEFAULT 0,
                x_restrict INTEGER DEFAULT 0,
                ai_type INTEGER DEFAULT 0,
                page_count INTEGER DEFAULT 1,
                width INTEGER DEFAULT 0,
                height INTEGER DEFAULT 0,
                create_date TEXT,
                indexed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_artwork_index_artist ON artwork_index(artist_user_id);
            CREATE INDEX IF NOT EXISTS idx_artwork_index_title ON artwork_index(title);

            CREATE TABLE IF NOT EXISTS artwork_index_state (
                artist_user_id TEXT PRIMARY KEY,
                artist_user_name TEXT NOT NULL,
                last_indexed_at TEXT,
                total_works INTEGER DEFAULT 0
            );
        ";

        using var cmd = new SqliteCommand(createTable, connection);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    // ── Writes ──────────────────────────────────────────────────────────────

    /// <summary>Replaces the full indexed set for one artist (used after a full crawl of that artist).</summary>
    public async Task ReplaceArtistArtworksAsync(string artistUserId, string artistUserName, IReadOnlyList<IndexedArtwork> artworks, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var del = new SqliteCommand("DELETE FROM artwork_index WHERE artist_user_id = @id", connection, tx))
            {
                del.Parameters.AddWithValue("@id", artistUserId);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var a in artworks)
            {
                using var cmd = new SqliteCommand(@"
                    INSERT OR REPLACE INTO artwork_index
                        (artwork_id, artist_user_id, artist_user_name, title, tags, thumbnail_url,
                         illust_type, x_restrict, ai_type, page_count, width, height, create_date, indexed_at)
                    VALUES
                        (@artworkId, @artistUserId, @artistUserName, @title, @tags, @thumbnailUrl,
                         @illustType, @xRestrict, @aiType, @pageCount, @width, @height, @createDate, @indexedAt)",
                    connection, tx);

                cmd.Parameters.AddWithValue("@artworkId", a.ArtworkId);
                cmd.Parameters.AddWithValue("@artistUserId", artistUserId);
                cmd.Parameters.AddWithValue("@artistUserName", artistUserName);
                cmd.Parameters.AddWithValue("@title", a.Title ?? "");
                cmd.Parameters.AddWithValue("@tags", string.Join(" ", a.Tags).ToLowerInvariant());
                cmd.Parameters.AddWithValue("@thumbnailUrl", (object?)a.ThumbnailUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@illustType", a.IllustType);
                cmd.Parameters.AddWithValue("@xRestrict", a.XRestrict);
                cmd.Parameters.AddWithValue("@aiType", a.AiType);
                cmd.Parameters.AddWithValue("@pageCount", a.PageCount);
                cmd.Parameters.AddWithValue("@width", a.Width);
                cmd.Parameters.AddWithValue("@height", a.Height);
                cmd.Parameters.AddWithValue("@createDate", (object?)a.CreateDate?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@indexedAt", DateTime.UtcNow.ToString("O"));

                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var state = new SqliteCommand(@"
                INSERT OR REPLACE INTO artwork_index_state (artist_user_id, artist_user_name, last_indexed_at, total_works)
                VALUES (@id, @name, @now, @total)", connection, tx))
            {
                state.Parameters.AddWithValue("@id", artistUserId);
                state.Parameters.AddWithValue("@name", artistUserName);
                state.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                state.Parameters.AddWithValue("@total", artworks.Count);
                await state.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task RemoveArtistAsync(string artistUserId, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var del1 = new SqliteCommand("DELETE FROM artwork_index WHERE artist_user_id = @id", connection);
        del1.Parameters.AddWithValue("@id", artistUserId);
        await del1.ExecuteNonQueryAsync(ct);

        using var del2 = new SqliteCommand("DELETE FROM artwork_index_state WHERE artist_user_id = @id", connection);
        del2.Parameters.AddWithValue("@id", artistUserId);
        await del2.ExecuteNonQueryAsync(ct);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<List<ArtistIndexState>> GetAllArtistStatesAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var result = new List<ArtistIndexState>();
        using var cmd = new SqliteCommand(
            "SELECT artist_user_id, artist_user_name, last_indexed_at, total_works FROM artwork_index_state", connection);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ArtistIndexState
            {
                ArtistUserId = reader.GetString(0),
                ArtistUserName = reader.GetString(1),
                LastIndexedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                TotalWorks = reader.GetInt32(3),
            });
        }
        return result;
    }

    /// <summary>Total number of indexed artworks (for status display).</summary>
    public async Task<int> GetTotalIndexedCountAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM artwork_index", connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Searches the index by keyword (matched against title + tags) and optional
    /// include/exclude tag lists. R-18 filtering is applied by the caller against
    /// the returned XRestrict/AiType flags (kept here as raw ints to avoid a
    /// Pikura.Core.Models dependency in the data layer).
    /// </summary>
    public async Task<(List<IndexedArtwork> Results, int Total)> SearchAsync(
        string? keyword,
        IReadOnlyList<string>? includeTags,
        IReadOnlyList<string>? excludeTags,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var where = new List<string>();
        var cmdParams = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(title LIKE @kw OR tags LIKE @kw)");
            cmdParams.Add(("@kw", $"%{keyword.ToLowerInvariant()}%"));
        }

        if (includeTags is { Count: > 0 })
        {
            for (int i = 0; i < includeTags.Count; i++)
            {
                where.Add($"tags LIKE @inc{i}");
                cmdParams.Add(($"@inc{i}", $"%{includeTags[i].ToLowerInvariant()}%"));
            }
        }

        if (excludeTags is { Count: > 0 })
        {
            for (int i = 0; i < excludeTags.Count; i++)
            {
                where.Add($"tags NOT LIKE @exc{i}");
                cmdParams.Add(($"@exc{i}", $"%{excludeTags[i].ToLowerInvariant()}%"));
            }
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        var total = 0;
        using (var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM artwork_index {whereClause}", connection))
        {
            foreach (var (name, value) in cmdParams) countCmd.Parameters.AddWithValue(name, value);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var results = new List<IndexedArtwork>();
        using (var cmd = new SqliteCommand($@"
            SELECT artwork_id, artist_user_id, artist_user_name, title, tags, thumbnail_url,
                   illust_type, x_restrict, ai_type, page_count, width, height, create_date
            FROM artwork_index
            {whereClause}
            ORDER BY create_date DESC
            LIMIT @limit OFFSET @offset", connection))
        {
            foreach (var (name, value) in cmdParams) cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new IndexedArtwork
                {
                    ArtworkId = reader.GetString(0),
                    ArtistUserId = reader.GetString(1),
                    ArtistUserName = reader.GetString(2),
                    Title = reader.GetString(3),
                    Tags = reader.GetString(4).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    ThumbnailUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    IllustType = reader.GetInt32(6),
                    XRestrict = reader.GetInt32(7),
                    AiType = reader.GetInt32(8),
                    PageCount = reader.GetInt32(9),
                    Width = reader.GetInt32(10),
                    Height = reader.GetInt32(11),
                    CreateDate = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
                });
            }
        }

        return (results, total);
    }

    public void Dispose()
    {
        // Connection is disposed per-operation
    }
}
