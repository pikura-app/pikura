using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Data;

/// <summary>One entry in the locally-tracked "recently viewed" history.</summary>
public sealed class ViewedHistoryEntry
{
    public string ArtworkId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public int IllustType { get; set; }
    public int XRestrict { get; set; }
    public int PageCount { get; set; } = 1;
    public IReadOnlyList<string> Tags { get; set; } = [];
    public DateTime ViewedAt { get; set; }
}

/// <summary>
/// SQLite-backed local browsing history. Unrestricted retention (unlike Pixiv's own
/// history feature, which is capped for non-Premium accounts) — every artwork opened
/// in the inline viewer, anywhere in the app, is recorded here. Re-viewing an artwork
/// bumps its timestamp rather than creating a duplicate entry.
/// </summary>
public sealed class ViewedHistoryRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<ViewedHistoryRepository> _logger;

    public ViewedHistoryRepository(string dbPath, ILogger<ViewedHistoryRepository> logger)
    {
        _logger = logger;
        _connectionString = $"Data Source={dbPath};Foreign Keys=True;Default Timeout=5;";
        EnsureDatabaseCreated();
    }

    private void EnsureDatabaseCreated()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var cmd = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS viewed_history (
                artwork_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                user_id TEXT NOT NULL,
                user_name TEXT NOT NULL,
                thumbnail_url TEXT,
                illust_type INTEGER DEFAULT 0,
                x_restrict INTEGER DEFAULT 0,
                page_count INTEGER DEFAULT 1,
                tags TEXT NOT NULL DEFAULT '',
                viewed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_viewed_history_viewed_at ON viewed_history(viewed_at);
        ", connection);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>Records (or bumps) a view. Non-fatal on failure — history tracking should never break the viewer.</summary>
    public async Task RecordViewAsync(ViewedHistoryEntry entry, CancellationToken ct = default)
    {
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand(@"
                INSERT OR REPLACE INTO viewed_history
                    (artwork_id, title, user_id, user_name, thumbnail_url, illust_type, x_restrict, page_count, tags, viewed_at)
                VALUES
                    (@id, @title, @userId, @userName, @thumb, @illustType, @xRestrict, @pageCount, @tags, @viewedAt)", connection);

            cmd.Parameters.AddWithValue("@id", entry.ArtworkId);
            cmd.Parameters.AddWithValue("@title", entry.Title);
            cmd.Parameters.AddWithValue("@userId", entry.UserId);
            cmd.Parameters.AddWithValue("@userName", entry.UserName);
            cmd.Parameters.AddWithValue("@thumb", (object?)entry.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@illustType", entry.IllustType);
            cmd.Parameters.AddWithValue("@xRestrict", entry.XRestrict);
            cmd.Parameters.AddWithValue("@pageCount", entry.PageCount);
            cmd.Parameters.AddWithValue("@tags", string.Join(" ", entry.Tags));
            cmd.Parameters.AddWithValue("@viewedAt", entry.ViewedAt.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record view for {ArtworkId} (non-fatal)", entry.ArtworkId);
        }
    }

    public async Task<List<ViewedHistoryEntry>> GetRecentAsync(int offset, int limit, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var results = new List<ViewedHistoryEntry>();
        using var cmd = new SqliteCommand(@"
            SELECT artwork_id, title, user_id, user_name, thumbnail_url, illust_type, x_restrict, page_count, tags, viewed_at
            FROM viewed_history
            ORDER BY viewed_at DESC
            LIMIT @limit OFFSET @offset", connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new ViewedHistoryEntry
            {
                ArtworkId = reader.GetString(0),
                Title = reader.GetString(1),
                UserId = reader.GetString(2),
                UserName = reader.GetString(3),
                ThumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                IllustType = reader.GetInt32(5),
                XRestrict = reader.GetInt32(6),
                PageCount = reader.GetInt32(7),
                Tags = reader.GetString(8).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                ViewedAt = DateTime.Parse(reader.GetString(9)),
            });
        }
        return results;
    }

    /// <summary>Gets entries viewed on a specific local calendar date, newest first.</summary>
    public async Task<(List<ViewedHistoryEntry> Results, int Total)> GetByDateAsync(DateTime date, int offset, int limit, CancellationToken ct = default)
    {
        var dayStart = date.Date.ToUniversalTime();
        var dayEnd = dayStart.AddDays(1);

        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var total = 0;
        using (var countCmd = new SqliteCommand(
            "SELECT COUNT(*) FROM viewed_history WHERE viewed_at >= @start AND viewed_at < @end", connection))
        {
            countCmd.Parameters.AddWithValue("@start", dayStart.ToString("O"));
            countCmd.Parameters.AddWithValue("@end", dayEnd.ToString("O"));
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        var results = new List<ViewedHistoryEntry>();
        using (var cmd = new SqliteCommand(@"
            SELECT artwork_id, title, user_id, user_name, thumbnail_url, illust_type, x_restrict, page_count, tags, viewed_at
            FROM viewed_history
            WHERE viewed_at >= @start AND viewed_at < @end
            ORDER BY viewed_at DESC
            LIMIT @limit OFFSET @offset", connection))
        {
            cmd.Parameters.AddWithValue("@start", dayStart.ToString("O"));
            cmd.Parameters.AddWithValue("@end", dayEnd.ToString("O"));
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new ViewedHistoryEntry
                {
                    ArtworkId = reader.GetString(0),
                    Title = reader.GetString(1),
                    UserId = reader.GetString(2),
                    UserName = reader.GetString(3),
                    ThumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IllustType = reader.GetInt32(5),
                    XRestrict = reader.GetInt32(6),
                    PageCount = reader.GetInt32(7),
                    Tags = reader.GetString(8).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    ViewedAt = DateTime.Parse(reader.GetString(9)),
                });
            }
        }

        return (results, total);
    }

    /// <summary>
    /// Gets entries whose <see cref="ViewedHistoryEntry.ViewedAt"/> falls within
    /// <c>[startUtc, endUtc)</c>, newest first. Either bound may be null for an
    /// open-ended range (both null = everything, i.e. same result set as <see cref="GetRecentAsync"/>).
    /// Used for the "past day/week/month/year" and custom-range pickers.
    /// </summary>
    public async Task<(List<ViewedHistoryEntry> Results, int Total)> GetByRangeAsync(DateTime? startUtc, DateTime? endUtc, int offset, int limit, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var where = "WHERE (@start IS NULL OR viewed_at >= @start) AND (@end IS NULL OR viewed_at < @end)";

        var total = 0;
        using (var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM viewed_history {where}", connection))
        {
            countCmd.Parameters.AddWithValue("@start", (object?)startUtc?.ToString("O") ?? DBNull.Value);
            countCmd.Parameters.AddWithValue("@end", (object?)endUtc?.ToString("O") ?? DBNull.Value);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        var results = new List<ViewedHistoryEntry>();
        using (var cmd = new SqliteCommand($@"
            SELECT artwork_id, title, user_id, user_name, thumbnail_url, illust_type, x_restrict, page_count, tags, viewed_at
            FROM viewed_history
            {where}
            ORDER BY viewed_at DESC
            LIMIT @limit OFFSET @offset", connection))
        {
            cmd.Parameters.AddWithValue("@start", (object?)startUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@end", (object?)endUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new ViewedHistoryEntry
                {
                    ArtworkId = reader.GetString(0),
                    Title = reader.GetString(1),
                    UserId = reader.GetString(2),
                    UserName = reader.GetString(3),
                    ThumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IllustType = reader.GetInt32(5),
                    XRestrict = reader.GetInt32(6),
                    PageCount = reader.GetInt32(7),
                    Tags = reader.GetString(8).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    ViewedAt = DateTime.Parse(reader.GetString(9)),
                });
            }
        }

        return (results, total);
    }

    /// <summary>One row of the "Grouped" view: a local calendar date with its view count and the newest few thumbnails (for the collage).</summary>
    public sealed class DateGroup
    {
        public DateTime Date { get; set; }
        public int Count { get; set; }
        public List<string?> Thumbnails { get; set; } = [];
    }

    /// <summary>
    /// Groups entries within <c>[startUtc, endUtc)</c> by local calendar date, newest date
    /// first. Each group carries up to <paramref name="thumbnailsPerGroup"/> of its newest
    /// thumbnails for the collage preview. Loads the (id/thumbnail/timestamp) projection for
    /// the whole range into memory and groups client-side — acceptable for a local,
    /// single-user history table of this size.
    /// </summary>
    public async Task<List<DateGroup>> GetDateGroupsAsync(DateTime? startUtc, DateTime? endUtc, int thumbnailsPerGroup = 4, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = new SqliteCommand(@"
            SELECT thumbnail_url, viewed_at
            FROM viewed_history
            WHERE (@start IS NULL OR viewed_at >= @start) AND (@end IS NULL OR viewed_at < @end)
            ORDER BY viewed_at DESC", connection);
        cmd.Parameters.AddWithValue("@start", (object?)startUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@end", (object?)endUtc?.ToString("O") ?? DBNull.Value);

        var groups = new List<DateGroup>();
        DateGroup? current = null;

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var thumb = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!DateTime.TryParse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var viewedAt))
                continue;
            var localDate = viewedAt.ToLocalTime().Date;

            if (current == null || current.Date != localDate)
            {
                current = new DateGroup { Date = localDate };
                groups.Add(current);
            }
            current.Count++;
            if (current.Thumbnails.Count < thumbnailsPerGroup)
                current.Thumbnails.Add(thumb);
        }

        return groups;
    }

    /// <summary>Gets the distinct set of local calendar dates that have at least one recorded view — used to highlight days with activity in the calendar picker.</summary>
    public async Task<HashSet<DateTime>> GetActiveDatesAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("SELECT viewed_at FROM viewed_history", connection);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var dates = new HashSet<DateTime>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (DateTime.TryParse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                dates.Add(dt.ToLocalTime().Date);
        }
        return dates;
    }

    public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("SELECT COUNT(*) FROM viewed_history", connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task RemoveAsync(string artworkId, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("DELETE FROM viewed_history WHERE artwork_id = @id", connection);
        cmd.Parameters.AddWithValue("@id", artworkId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("DELETE FROM viewed_history", connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes entries viewed at or after the given UTC instant (e.g. "clear the past hour"). Returns the number of rows removed.</summary>
    public async Task<int> ClearSinceAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("DELETE FROM viewed_history WHERE viewed_at >= @cutoff", connection);
        cmd.Parameters.AddWithValue("@cutoff", cutoffUtc.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes entries viewed before the given UTC instant (retention-based auto-clear). Returns the number of rows removed.</summary>
    public async Task<int> ClearOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new SqliteCommand("DELETE FROM viewed_history WHERE viewed_at < @cutoff", connection);
        cmd.Parameters.AddWithValue("@cutoff", cutoffUtc.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() { /* connection is disposed per-operation */ }
}
