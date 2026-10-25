using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Pikura.Core.Services.Hoshi;

/// <summary>
/// One-time import of legacy %AppData%\Pikura\hoshi_sessions JSON files into
/// HoshiStore. Verifies per session (message count + image sha), records each
/// success in the migrations table, and only renames the legacy folder to
/// hoshi_sessions.migrated when every session imported cleanly.
/// </summary>
public sealed class HoshiMigration(HoshiStore store, ILogger<HoshiMigration> logger)
{
    private sealed class LegacyMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        public DateTime At { get; set; }
        public string? ImageBase64 { get; set; }
        public string? ArtworkId { get; set; }
        public string? ArtistId { get; set; }
    }

    private sealed class LegacySession
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? ImageSource { get; set; }
        public string? PixivArtworkId { get; set; }
        public List<LegacyMessage>? Messages { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed record Result(int Imported, int Failed, bool LegacyRenamed);

    /// <summary>
    /// Imports all legacy sessions under <paramref name="legacyRoot"/> (root →
    /// user_id NULL; one-level subdirs → user id = dir name). Idempotent —
    /// sessions already in the migrations table are skipped.
    /// </summary>
    public async Task<Result> MigrateAsync(string legacyRoot, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var imported = 0; var failed = 0;
        if (!Directory.Exists(legacyRoot)) return new Result(0, 0, false);

        var jobs = new List<(string Json, string? UserId)>();
        foreach (var f in Directory.EnumerateFiles(legacyRoot, "*.json"))
            jobs.Add((f, null));
        foreach (var dir in Directory.EnumerateDirectories(legacyRoot))
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                jobs.Add((f, Path.GetFileName(dir)));

        var done = 0;
        var allOk = true;
        foreach (var (json, userId) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(json);
            progress?.Report((++done, jobs.Count));
            if (await AlreadyMigratedAsync(id, ct)) { imported++; continue; }
            try
            {
                var ok = await MigrateOneAsync(json, userId, ct);
                if (ok) { await RecordMigratedAsync(id, ct); imported++; }
                else { failed++; allOk = false; }
            }
            catch (Exception ex)
            {
                failed++; allOk = false;
                logger.LogWarning(ex, "Hoshi migration failed for {File}", json);
            }
        }

        var renamed = false;
        if (allOk && jobs.Count > 0)
        {
            var target = legacyRoot.TrimEnd('\\', '/') + ".migrated";
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(legacyRoot, target);
                renamed = true;
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to rename legacy hoshi dir"); }
        }
        return new Result(imported, failed, renamed);
    }

    private async Task<bool> MigrateOneAsync(string jsonPath, string? userId, CancellationToken ct)
    {
        var s = JsonSerializer.Deserialize<LegacySession>(
            await File.ReadAllTextAsync(jsonPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (s?.Id == null) return false;

        string? imageSha = null;
        var imgPath = Path.ChangeExtension(jsonPath, ".img");
        if (File.Exists(imgPath))
            imageSha = await store.PutImageAsync(await File.ReadAllBytesAsync(imgPath, ct), ct);

        var messages = new List<HoshiMessageRow>();
        foreach (var m in s.Messages ?? [])
        {
            string? msgSha = null;
            if (!string.IsNullOrEmpty(m.ImageBase64))
            {
                try { msgSha = await store.PutImageAsync(Convert.FromBase64String(m.ImageBase64), ct); }
                catch { }
            }
            messages.Add(new HoshiMessageRow
            {
                SessionId = s.Id, Role = m.Role ?? "user", Content = m.Content ?? "",
                At = m.At, ImageSha = msgSha, ArtworkId = m.ArtworkId, ArtistId = m.ArtistId,
            });
        }

        var header = new HoshiSessionHeader
        {
            Id = s.Id, UserId = userId, Title = s.Title ?? "",
            ImageSha = imageSha, ImageSource = s.ImageSource, PixivArtworkId = s.PixivArtworkId,
            CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
        };
        await store.SaveSessionAsync(header, messages, ct);

        // Verify: message count equal + image sha present when a sidecar existed.
        var check = await store.GetSessionMessagesAsync(s.Id, ct);
        if (check.Count != messages.Count) return false;
        if (imageSha != null)
        {
            var bytes = await store.OpenImageAsync(imageSha, ct);
            if (bytes == null) return false;
        }
        return true;
    }

    private async Task<bool> AlreadyMigratedAsync(string id, CancellationToken ct)
    {
        using var c = new SqliteConnection(store.ConnectionStringForTests);
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand("SELECT 1 FROM migrations WHERE session_id=@i", c);
        cmd.Parameters.AddWithValue("@i", id);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private async Task RecordMigratedAsync(string id, CancellationToken ct)
    {
        using var c = new SqliteConnection(store.ConnectionStringForTests);
        await c.OpenAsync(ct);
        using var cmd = new SqliteCommand(
            "INSERT OR IGNORE INTO migrations(session_id, migrated_at) VALUES(@i,@t)", c);
        cmd.Parameters.AddWithValue("@i", id);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Picks the newest real user/assistant turns that fit a token budget.
/// Pure + unit-testable: never splits a message, always keeps the newest turn.
/// </summary>
public static class HoshiContextBudget
{
    /// <summary>
    /// messages in chronological order → the suffix fitting ~<paramref name="tokenBudget"/>
    /// tokens at 4 chars/token. System/status roles are always dropped.
    /// </summary>
    public static List<(string Role, string Content)> Select(
        IEnumerable<(string Role, string Content)> messages, int tokenBudget = 6000)
    {
        var chars = tokenBudget * 4;
        var real = messages.Where(m => m.Role is "user" or "assistant").ToList();
        var picked = new List<(string, string)>();
        var used = 0;
        for (var i = real.Count - 1; i >= 0; i--)
        {
            var len = real[i].Content?.Length ?? 0;
            if (picked.Count > 0 && used + len > chars) break;
            used += len;
            picked.Insert(0, real[i]);
        }
        return picked!;
    }
}
