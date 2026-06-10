using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using System.Data;
using System.Text.Json;

namespace Pikura.Core.Data;

/// <summary>
/// SQLite-backed repository for download jobs.
/// Provides persistence for download queue state and history.
/// </summary>
public sealed class DownloadJobRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<DownloadJobRepository> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _activeUserId;

    public DownloadJobRepository(string dbPath, ILogger<DownloadJobRepository> logger)
    {
        _logger = logger;
        _connectionString = $"Data Source={dbPath};Foreign Keys=True;";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        EnsureDatabaseCreated();
    }

    private void EnsureDatabaseCreated()
    {
        using var connection = CreateConnection();
        connection.Open();

        var createJobsTable = @"
            CREATE TABLE IF NOT EXISTS download_jobs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type INTEGER NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                settings_json TEXT,
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                last_retried_at TEXT,
                error_message TEXT,
                retry_count INTEGER DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_jobs_status ON download_jobs(status);
            CREATE INDEX IF NOT EXISTS idx_jobs_created ON download_jobs(created_at);
        ";

        var createTargetsTable = @"
            CREATE TABLE IF NOT EXISTS download_targets (
                id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                name TEXT,
                type INTEGER NOT NULL,
                page_range TEXT,
                custom_settings_json TEXT,
                status INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                found_items INTEGER DEFAULT 0,
                downloaded_items INTEGER DEFAULT 0,
                processed_at TEXT,
                FOREIGN KEY (job_id) REFERENCES download_jobs(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_targets_job ON download_targets(job_id);
            CREATE INDEX IF NOT EXISTS idx_targets_status ON download_targets(status);
        ";

        using (var cmd = new SqliteCommand(createJobsTable, connection))
        {
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new SqliteCommand(createTargetsTable, connection))
        {
            cmd.ExecuteNonQuery();
        }

        // Migrations: add columns introduced after initial schema
        var migrations = new[]
        {
            "ALTER TABLE download_jobs ADD COLUMN last_retried_at TEXT",
            "ALTER TABLE download_jobs ADD COLUMN retry_count INTEGER DEFAULT 0",
            "ALTER TABLE download_jobs ADD COLUMN output_folder TEXT",
            "ALTER TABLE download_targets ADD COLUMN thumbnail_url TEXT",
            "ALTER TABLE download_targets ADD COLUMN user_name TEXT",
            "ALTER TABLE download_targets ADD COLUMN user_id TEXT",
            "ALTER TABLE download_jobs ADD COLUMN owner_user_id TEXT",
            "ALTER TABLE download_jobs ADD COLUMN sort_order INTEGER DEFAULT 0",
            "ALTER TABLE download_targets ADD COLUMN completed_artwork_ids TEXT",
            "ALTER TABLE download_targets ADD COLUMN downloaded_bytes INTEGER DEFAULT 0",
        };
        foreach (var migration in migrations)
        {
            try
            {
                using var cmd = new SqliteCommand(migration, connection);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists or other non-fatal schema error
            }
        }

        _logger.LogDebug("Database initialized at {Path}", _connectionString);
    }

    /// <summary>Restricts all subsequent queries to the given Pixiv user ID.</summary>
    public void SetActiveUser(string? userId) => _activeUserId = userId;

    private SqliteConnection CreateConnection() => new(_connectionString);

    #region Job CRUD

    public async Task<DownloadJob?> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, name, type, status, settings_json, created_at, started_at, completed_at, last_retried_at, error_message, retry_count, output_folder, sort_order
            FROM download_jobs
            WHERE id = @id";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var job = ReadJobFromReader(reader);
        job.Targets = await GetTargetsForJobAsync(id, connection, ct);
        return job;
    }

    /// <summary>Returns all running/pending/paused jobs regardless of which account owns them.</summary>
    public async Task<List<DownloadJob>> GetAllActiveJobsAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, name, type, status, settings_json, created_at, started_at, completed_at, last_retried_at, error_message, retry_count, output_folder, sort_order
            FROM download_jobs
            WHERE status IN (@pending, @running, @paused)
            ORDER BY sort_order ASC, created_at DESC";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@pending",  (int)JobStatus.Pending);
        cmd.Parameters.AddWithValue("@running",  (int)JobStatus.Running);
        cmd.Parameters.AddWithValue("@paused",   (int)JobStatus.Paused);

        var jobs = new List<DownloadJob>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            jobs.Add(ReadJobFromReader(reader));

        foreach (var job in jobs)
            job.Targets = await GetTargetsForJobAsync(job.Id, connection, ct);

        return jobs;
    }

    public async Task<List<DownloadJob>> GetJobsAsync(
        JobStatus? status = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = @"
            SELECT id, name, type, status, settings_json, created_at, started_at, completed_at, last_retried_at, error_message, retry_count, output_folder, sort_order
            FROM download_jobs
            WHERE (owner_user_id = @uid OR owner_user_id IS NULL)";

        if (status.HasValue)
            sql += " AND status = @status";

        sql += " ORDER BY sort_order ASC, created_at DESC";

        if (limit.HasValue)
            sql += " LIMIT @limit";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@uid", _activeUserId ?? (object)DBNull.Value);

        if (status.HasValue)
            cmd.Parameters.AddWithValue("@status", (int)status.Value);

        if (limit.HasValue)
            cmd.Parameters.AddWithValue("@limit", limit.Value);

        var jobs = new List<DownloadJob>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            jobs.Add(ReadJobFromReader(reader));
        }

        // Load targets for each job
        foreach (var job in jobs)
        {
            job.Targets = await GetTargetsForJobAsync(job.Id, connection, ct);
        }

        return jobs;
    }

    public async Task SaveJobAsync(DownloadJob job, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            // Upsert job
            // sort_order is set on INSERT only. It is deliberately omitted from the
            // ON CONFLICT UPDATE so that drag/Move reorders (persisted directly via
            // UpdateSortOrderAsync) are never clobbered by an in-flight SaveJobAsync
            // call (e.g. when the output folder is captured mid-download).
            var upsertJob = @"
                INSERT INTO download_jobs (id, name, type, status, settings_json, created_at, started_at, completed_at, last_retried_at, error_message, retry_count, output_folder, owner_user_id, sort_order)
                VALUES (@id, @name, @type, @status, @settings, @createdAt, @startedAt, @completedAt, @lastRetriedAt, @error, @retryCount, @outputFolder, @ownerUserId, @sortOrder)
                ON CONFLICT(id) DO UPDATE SET
                    name = @name,
                    type = @type,
                    status = @status,
                    settings_json = @settings,
                    started_at = @startedAt,
                    completed_at = @completedAt,
                    last_retried_at = @lastRetriedAt,
                    error_message = @error,
                    retry_count = @retryCount,
                    output_folder = @outputFolder";

            using (var cmd = new SqliteCommand(upsertJob, connection, (SqliteTransaction)transaction))
            {
                cmd.Parameters.AddWithValue("@id", job.Id.ToString());
                cmd.Parameters.AddWithValue("@name", job.Name);
                cmd.Parameters.AddWithValue("@type", (int)job.Type);
                cmd.Parameters.AddWithValue("@status", (int)job.Status);
                cmd.Parameters.AddWithValue("@settings", JsonSerializer.Serialize(job.Settings, _jsonOptions));
                cmd.Parameters.AddWithValue("@createdAt", job.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@startedAt", job.StartedAt?.ToString("O") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@completedAt", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@lastRetriedAt", job.LastRetriedAt?.ToString("O") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@error", job.ErrorMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@retryCount", job.RetryCount);
                cmd.Parameters.AddWithValue("@outputFolder", job.OutputFolder ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ownerUserId", _activeUserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@sortOrder", job.SortOrder);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete existing targets and insert new ones
            using (var deleteCmd = new SqliteCommand("DELETE FROM download_targets WHERE job_id = @jobId", connection, (SqliteTransaction)transaction))
            {
                deleteCmd.Parameters.AddWithValue("@jobId", job.Id.ToString());
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            // Insert targets
            foreach (var target in job.Targets)
            {
                var insertTarget = @"
                    INSERT INTO download_targets
                    (id, job_id, target_id, name, thumbnail_url, user_name, user_id, type, page_range, custom_settings_json, status, error_message, found_items, downloaded_items, downloaded_bytes, processed_at)
                    VALUES
                    (@id, @jobId, @targetId, @name, @thumbnailUrl, @userName, @userId, @type, @pageRange, @customSettings, @status, @error, @foundItems, @downloadedItems, @downloadedBytes, @processedAt)";

                using var cmd = new SqliteCommand(insertTarget, connection, (SqliteTransaction)transaction);
                cmd.Parameters.AddWithValue("@id", target.Id.ToString());
                cmd.Parameters.AddWithValue("@jobId", job.Id.ToString());
                cmd.Parameters.AddWithValue("@targetId", target.TargetId);
                cmd.Parameters.AddWithValue("@name", target.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@thumbnailUrl", target.ThumbnailUrl ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@userName", target.UserName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@userId", target.UserId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@type", (int)target.Type);
                cmd.Parameters.AddWithValue("@pageRange", target.PageRange ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@customSettings", target.CustomSettings != null ? JsonSerializer.Serialize(target.CustomSettings, _jsonOptions) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@status", (int)target.Status);
                cmd.Parameters.AddWithValue("@error", target.ErrorMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@foundItems", target.FoundItems);
                cmd.Parameters.AddWithValue("@downloadedItems", target.DownloadedItems);
                cmd.Parameters.AddWithValue("@downloadedBytes", target.DownloadedBytes);
                cmd.Parameters.AddWithValue("@processedAt", target.ProcessedAt?.ToString("O") ?? (object)DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            _logger.LogDebug("Saved job {JobId} with {TargetCount} targets", job.Id, job.Targets.Count);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = new SqliteCommand("DELETE FROM download_jobs WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Deleted job {JobId}", id);
    }

    public async Task UpdateJobStatusAsync(Guid id, JobStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        string sql;
        if (status == JobStatus.Running)
        {
            sql = "UPDATE download_jobs SET status = @status, started_at = @now, error_message = @error WHERE id = @id";
        }
        else if (status == JobStatus.Completed || status == JobStatus.Failed || status == JobStatus.Cancelled)
        {
            sql = "UPDATE download_jobs SET status = @status, completed_at = @now, error_message = @error WHERE id = @id";
        }
        else
        {
            sql = "UPDATE download_jobs SET status = @status, error_message = @error WHERE id = @id";
        }

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@error", errorMessage ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Marks all jobs that were left in Running state from a previous app session
    /// as Cancelled. Only Running jobs are cancelled since they were actively executing.
    /// Pending and Paused jobs are left intact so they can start/resume on next launch.
    /// Should be called once on startup.
    /// </summary>
    public async Task<int> MarkOrphanedJobsAsCancelledAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = @"
            UPDATE download_jobs
            SET status = @cancelled,
                completed_at = @now,
                error_message = COALESCE(error_message, 'Abandoned: app restarted while running')
            WHERE status = @running";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@cancelled", (int)JobStatus.Cancelled);
        cmd.Parameters.AddWithValue("@running",   (int)JobStatus.Running);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            _logger.LogInformation("Marked {Count} orphaned Running jobs as Cancelled on startup", rows);
        }
        return rows;
    }

    /// <summary>
    /// Recovers jobs left in Running state from a previous session by converting them to
    /// Paused (so progress is preserved and they can be resumed), and resets any Running
    /// targets back to Pending. Pending jobs are left untouched so they remain in the queue.
    /// Should be called once on startup.
    /// </summary>
    public async Task<int> RecoverInterruptedJobsAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            int rows;
            using (var cmd = new SqliteCommand(
                "UPDATE download_jobs SET status = @paused WHERE status = @running",
                connection, transaction))
            {
                cmd.Parameters.AddWithValue("@paused", (int)JobStatus.Paused);
                cmd.Parameters.AddWithValue("@running", (int)JobStatus.Running);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }
            using (var cmd = new SqliteCommand(
                "UPDATE download_targets SET status = @pending WHERE status = @running",
                connection, transaction))
            {
                cmd.Parameters.AddWithValue("@pending", (int)TargetStatus.Pending);
                cmd.Parameters.AddWithValue("@running", (int)TargetStatus.Running);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            if (rows > 0)
                _logger.LogInformation("Recovered {Count} interrupted Running job(s) as Paused on startup", rows);
            return rows;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpdateSortOrderAsync(Guid id, int sortOrder, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = new SqliteCommand("UPDATE download_jobs SET sort_order = @order WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("@order", sortOrder);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(Guid Id, int SortOrder)>> GetPendingJobSortOrdersAsync(CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var sql = "SELECT id, sort_order FROM download_jobs WHERE status IN (@pending, @paused, @running) ORDER BY sort_order ASC, created_at DESC";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@pending", (int)JobStatus.Pending);
        cmd.Parameters.AddWithValue("@paused", (int)JobStatus.Paused);
        cmd.Parameters.AddWithValue("@running", (int)JobStatus.Running);
        var result = new List<(Guid, int)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1)));
        return result;
    }

    #endregion

    #region Target Operations

    /// <summary>Atomically adds bytes to the running downloaded_bytes total for a target.</summary>
    public async Task AddBytesAsync(Guid targetId, long bytes, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var sql = "UPDATE download_targets SET downloaded_bytes = downloaded_bytes + @bytes WHERE id = @id";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@bytes", bytes);
        cmd.Parameters.AddWithValue("@id", targetId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Persists the total found-items count without changing any other target field.</summary>
    public async Task UpdateFoundItemsAsync(Guid targetId, int foundItems, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var sql = "UPDATE download_targets SET found_items = @found WHERE id = @id";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@found", foundItems);
        cmd.Parameters.AddWithValue("@id", targetId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateTargetStatusAsync(
        Guid targetId,
        TargetStatus status,
        int foundItems = 0,
        int downloadedItems = 0,
        long addBytes = 0,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        var sql = @"
            UPDATE download_targets
            SET status = @status,
                found_items = @foundItems,
                downloaded_items = @downloadedItems,
                downloaded_bytes = downloaded_bytes + @addBytes,
                error_message = @error,
                processed_at = @now
            WHERE id = @id";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", targetId.ToString());
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@foundItems", foundItems);
        cmd.Parameters.AddWithValue("@downloadedItems", downloadedItems);
        cmd.Parameters.AddWithValue("@addBytes", addBytes);
        cmd.Parameters.AddWithValue("@error", errorMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Appends an artwork ID to the target's completed-artwork checkpoint list.
    /// Used by DownloadArtistAsync so pause/resume doesn't re-download already-finished artworks.
    /// </summary>
    public async Task AppendCompletedArtworkIdAsync(Guid targetId, string artworkId, CancellationToken ct = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        // Read current list, append, write back
        string? current = null;
        using (var readCmd = new SqliteCommand("SELECT completed_artwork_ids FROM download_targets WHERE id = @id", connection))
        {
            readCmd.Parameters.AddWithValue("@id", targetId.ToString());
            var result = await readCmd.ExecuteScalarAsync(ct);
            current = result as string;
        }

        var ids = string.IsNullOrEmpty(current)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(current) ?? new List<string>();
        if (!ids.Contains(artworkId))
            ids.Add(artworkId);

        using var writeCmd = new SqliteCommand(
            "UPDATE download_targets SET completed_artwork_ids = @ids WHERE id = @id", connection);
        writeCmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(ids));
        writeCmd.Parameters.AddWithValue("@id", targetId.ToString());
        await writeCmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region Helpers

    private static DownloadJob ReadJobFromReader(SqliteDataReader reader)
    {
        var settingsJson = reader.GetString("settings_json");
        var settings = string.IsNullOrEmpty(settingsJson)
            ? new SettingsOverride()
            : JsonSerializer.Deserialize<SettingsOverride>(settingsJson)!;

        return new DownloadJob
        {
            Id = Guid.Parse(reader.GetString("id")),
            Name = reader.GetString("name"),
            Type = (DownloadJobType)reader.GetInt32("type"),
            Status = (JobStatus)reader.GetInt32("status"),
            Settings = settings,
            CreatedAt = DateTime.Parse(reader.GetString("created_at")),
            StartedAt = reader.IsDBNull("started_at") ? null : DateTime.Parse(reader.GetString("started_at")),
            CompletedAt = reader.IsDBNull("completed_at") ? null : DateTime.Parse(reader.GetString("completed_at")),
            LastRetriedAt = reader.IsDBNull("last_retried_at") ? null : DateTime.Parse(reader.GetString("last_retried_at")),
            ErrorMessage = reader.IsDBNull("error_message") ? null : reader.GetString("error_message"),
            RetryCount = reader.GetInt32("retry_count"),
            OutputFolder = reader.IsDBNull("output_folder") ? null : reader.GetString("output_folder"),
            SortOrder = reader.IsDBNull("sort_order") ? 0 : reader.GetInt32("sort_order"),
        };
    }

    private async Task<List<DownloadTarget>> GetTargetsForJobAsync(Guid jobId, SqliteConnection connection, CancellationToken ct)
    {
        var sql = @"
            SELECT id, job_id, target_id, name, thumbnail_url, user_name, user_id, type, page_range, custom_settings_json, status, error_message, found_items, downloaded_items, downloaded_bytes, processed_at, completed_artwork_ids
            FROM download_targets
            WHERE job_id = @jobId
            ORDER BY id";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@jobId", jobId.ToString());

        var targets = new List<DownloadTarget>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var customSettingsJson = reader.IsDBNull("custom_settings_json") ? null : reader.GetString("custom_settings_json");
            SettingsOverride? customSettings = null;

            if (!string.IsNullOrEmpty(customSettingsJson))
            {
                customSettings = JsonSerializer.Deserialize<SettingsOverride>(customSettingsJson);
            }

            var completedIdsRaw = reader.IsDBNull("completed_artwork_ids") ? null : reader.GetString("completed_artwork_ids");
            var completedIds = string.IsNullOrEmpty(completedIdsRaw)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(completedIdsRaw) ?? new List<string>();

            targets.Add(new DownloadTarget
            {
                Id = Guid.Parse(reader.GetString("id")),
                JobId = Guid.Parse(reader.GetString("job_id")),
                TargetId = reader.GetString("target_id"),
                Name = reader.IsDBNull("name") ? string.Empty : reader.GetString("name"),
                ThumbnailUrl = reader.IsDBNull("thumbnail_url") ? null : reader.GetString("thumbnail_url"),
                UserName = reader.IsDBNull("user_name") ? null : reader.GetString("user_name"),
                UserId = reader.IsDBNull("user_id") ? null : reader.GetString("user_id"),
                Type = (TargetType)reader.GetInt32("type"),
                PageRange = reader.IsDBNull("page_range") ? null : reader.GetString("page_range"),
                CustomSettings = customSettings,
                Status = (TargetStatus)reader.GetInt32("status"),
                ErrorMessage = reader.IsDBNull("error_message") ? null : reader.GetString("error_message"),
                FoundItems = reader.GetInt32("found_items"),
                DownloadedItems = reader.GetInt32("downloaded_items"),
                DownloadedBytes = reader.IsDBNull("downloaded_bytes") ? 0 : reader.GetInt64("downloaded_bytes"),
                ProcessedAt = reader.IsDBNull("processed_at") ? null : DateTime.Parse(reader.GetString("processed_at")),
                CompletedArtworkIds = completedIds,
            });
        }

        return targets;
    }

    #endregion

    public void Dispose()
    {
        // Connection pooling handles cleanup
    }
}
