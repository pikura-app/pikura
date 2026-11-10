using System.Globalization;
using System.Text.RegularExpressions;
using Pikura.Core.Data;
using Pikura.Core.Models;

namespace Pikura.Core.Services.Library;

public enum RenameStatus { Rename, Unchanged, Skipped }

/// <summary>One file in a rename preview.</summary>
public sealed record RenamePlanItem(long FileId, string OldPath, string NewPath, RenameStatus Status, string? MissingTokens = null,
    bool OutsideLibrary = false);

/// <summary>Per-file data the templates need beyond the <see cref="LibraryFile"/> row.</summary>
public sealed record RenameFileInfo(LibraryFile File, int PageCount = 1, string? Character = null, string? Series = null);

public sealed record RenameOptions(string FolderTemplate, string FilenameTemplate, string DateFormat = "yyyy-MM-dd",
    bool SeparateR18Folder = false);

public sealed record RenameMove(long FileId, string From, string To);

public sealed class RenameResult
{
    public List<RenameMove> Moved { get; } = [];
    public List<(string Path, string Error)> Failed { get; } = [];
}

/// <summary>
/// Renames Library files with the download folder/filename templates. <see cref="Plan"/> is a
/// pure preview; <see cref="ApplyAsync"/> moves the files (plus their <c>.tags.txt</c> sidecar),
/// updates the index, and returns what moved so <see cref="UndoAsync"/> can put it back.
/// </summary>
public sealed class LibraryRenameService
{
    private static readonly Regex TokenRx = new(@"%([a-zA-Z_]+)(?:\{[^}]*\})?%", RegexOptions.Compiled);

    /// <summary>Tokens whose value comes from the file's metadata — a file missing one is skipped
    /// rather than renamed to a name with a hole in it.</summary>
    public static readonly IReadOnlySet<string> DataTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "image_id", "member_id", "artist", "title", "tags",
        "works_date", "works_date_only", "works_date_fmt", "character", "series",
    };

    private readonly LibraryRepository _repo;

    public LibraryRenameService(LibraryRepository repo) => _repo = repo;

    /// <summary>Loads page counts and top character/series for <paramref name="files"/>.</summary>
    public async Task<IReadOnlyList<RenameFileInfo>> LoadInfoAsync(IReadOnlyList<LibraryFile> files, CancellationToken ct = default)
    {
        var pages = await _repo.GetPageCountsAsync(
            files.Where(f => !string.IsNullOrEmpty(f.ArtworkId)).Select(f => f.ArtworkId!).ToList(), ct);
        var ai = await _repo.GetTopCharacterSeriesAsync(files.Select(f => f.Id).ToList(), ct);
        return files.Select(f =>
        {
            ai.TryGetValue(f.Id, out var cs);
            var count = f.ArtworkId != null && pages.TryGetValue(f.ArtworkId, out var n) ? n : 1;
            return new RenameFileInfo(f, Math.Max(count, (f.PageIndex ?? 0) + 1), Pretty(cs.Character), Pretty(cs.Series));
        }).ToList();
    }

    private static string? Pretty(string? tag) => string.IsNullOrWhiteSpace(tag) ? null : tag.Replace('_', ' ');

    public static IReadOnlyList<RenamePlanItem> Plan(IReadOnlyList<RenameFileInfo> files, RenameOptions o)
    {
        var template = new FilenameTemplate(o.DateFormat);
        var plan = new List<RenamePlanItem>(files.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(files.Select(f => f.File.Path), StringComparer.OrdinalIgnoreCase);
        var used = TokenRx.Matches(o.FolderTemplate + o.FilenameTemplate).Select(m => m.Groups[1].Value)
            .Where(DataTokens.Contains).Distinct().ToList();

        foreach (var info in files)
        {
            var f = info.File;
            var ctx = Context(info);
            var missing = used.Where(t => string.IsNullOrWhiteSpace(template.Resolve($"%{t}%", ctx))).ToList();
            if (missing.Count > 0)
            {
                plan.Add(new RenamePlanItem(f.Id, f.Path, f.Path, RenameStatus.Skipped,
                    string.Join(", ", missing.Select(t => $"%{t}%"))));
                claimed.Add(f.Path);
                continue;
            }

            var nameTemplate = o.FilenameTemplate;
            if (info.PageCount > 1 && !nameTemplate.Contains("%page_index%", StringComparison.OrdinalIgnoreCase)
                && !nameTemplate.Contains("%page_number%", StringComparison.OrdinalIgnoreCase)
                && !nameTemplate.Contains("%urlFilename%", StringComparison.OrdinalIgnoreCase))
                nameTemplate += "_p%page_index%";

            var root = Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetFullPath(Path.Combine(root, template.Resolve(o.FolderTemplate, ctx)));
            if (o.SeparateR18Folder && f.IsR18) dir = Path.Combine(dir, "R-18");
            // An absolute or ".." folder template must not move files out of their library folder.
            if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new RenamePlanItem(f.Id, f.Path, f.Path, RenameStatus.Skipped, OutsideLibrary: true));
                claimed.Add(f.Path);
                continue;
            }
            var ext = Path.GetExtension(f.Path);
            var stem = PixivDownloadService.SafeName(template.Resolve(nameTemplate, ctx));
            var target = Path.GetFullPath(Path.Combine(dir, stem + ext));

            if (target.Equals(f.Path, StringComparison.Ordinal))
            {
                plan.Add(new RenamePlanItem(f.Id, f.Path, f.Path, RenameStatus.Unchanged));
                claimed.Add(f.Path);
                continue;
            }

            // A case-only change of the same file is still a rename; otherwise number around
            // anything already on disk (that isn't moving away) or claimed earlier in this batch.
            var n = 1;
            var candidate = target;
            while (claimed.Contains(candidate)
                   || (!candidate.Equals(f.Path, StringComparison.OrdinalIgnoreCase)
                       && !sources.Contains(candidate) && File.Exists(candidate)))
                candidate = Path.Combine(dir, $"{stem} ({++n}){ext}");
            claimed.Add(candidate);
            plan.Add(new RenamePlanItem(f.Id, f.Path, candidate, RenameStatus.Rename));
        }
        return plan;
    }

    private static FilenameContext Context(RenameFileInfo info)
    {
        var f = info.File;
        DateTimeOffset? created = DateTimeOffset.TryParse(f.CreateDate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        return new FilenameContext
        {
            Artwork = new ArtworkPreview
            {
                Id = f.ArtworkId ?? string.Empty,
                UserId = f.ArtistId ?? string.Empty,
                UserName = f.ArtistName ?? string.Empty,
                Title = f.Title ?? string.Empty,
                Tags = string.IsNullOrWhiteSpace(f.Tags) ? [] : f.Tags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList(),
                XRestrict = f.XRestrict,
                CreateDate = created,
            },
            PageIndex = f.PageIndex ?? 0,
            PageCount = info.PageCount,
            OriginalUrl = f.OriginalUrl ?? f.Path,
            Character = info.Character,
            Series = info.Series,
        };
    }

    public async Task<RenameResult> ApplyAsync(IReadOnlyList<RenamePlanItem> plan, IReadOnlyCollection<string> libraryRoots,
        CancellationToken ct = default)
    {
        var result = new RenameResult();
        var pending = plan.Where(p => p.Status == RenameStatus.Rename).ToList();

        // Move whatever has a free target; repeat, since each move can free another file's target
        // (A→B while B→C). Case-only renames count as free.
        for (var progressed = true; pending.Count > 0 && progressed;)
        {
            progressed = false;
            foreach (var p in pending.ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(p.NewPath) && !p.NewPath.Equals(p.OldPath, StringComparison.OrdinalIgnoreCase)) continue;
                pending.Remove(p);
                progressed = true;
                await MoveOneAsync(p, p.OldPath, result, ct);
            }
        }
        // Whatever's left swaps names in a cycle (A→B, B→A): park each under a temp name first.
        var parked = new List<(RenamePlanItem Item, string Temp)>();
        foreach (var p in pending)
        {
            var temp = p.OldPath + ".pikura-rename-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                File.Move(p.OldPath, temp);
                // The index row moves too, or the partner's path update hits the unique-path rule.
                try { await _repo.UpdatePathAsync(p.FileId, temp, ct); }
                catch { File.Move(temp, p.OldPath); throw; }
                parked.Add((p, temp));
            }
            catch (Exception ex) { result.Failed.Add((p.OldPath, ex.Message)); }
        }
        foreach (var (p, temp) in parked) await MoveOneAsync(p, temp, result, ct);

        RemoveEmptyFolders(result.Moved.Select(m => m.From), libraryRoots);
        return result;
    }

    /// <summary>Moves one file (from <paramref name="currentPath"/>, which is a temp name for parked
    /// files) and its index row. On any failure the file goes back to its original path.</summary>
    private async Task MoveOneAsync(RenamePlanItem p, string currentPath, RenameResult result, CancellationToken ct)
    {
        var moved = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p.NewPath)!);
            File.Move(currentPath, p.NewPath);
            moved = true;
            await _repo.UpdatePathAsync(p.FileId, p.NewPath, ct);
        }
        catch (Exception ex)
        {
            try
            {
                File.Move(moved ? p.NewPath : currentPath, p.OldPath);
                if (currentPath != p.OldPath) await _repo.UpdatePathAsync(p.FileId, p.OldPath, CancellationToken.None);
            }
            catch { /* left where it is; reported as failed */ }
            result.Failed.Add((p.OldPath, ex.Message));
            return;
        }
        MoveSidecar(p.OldPath, p.NewPath);
        result.Moved.Add(new RenameMove(p.FileId, p.OldPath, p.NewPath));
    }

    public async Task<RenameResult> UndoAsync(RenameResult done, IReadOnlyCollection<string> libraryRoots, CancellationToken ct = default)
    {
        var back = done.Moved.Select(m => new RenamePlanItem(m.FileId, m.To, m.From, RenameStatus.Rename)).ToList();
        return await ApplyAsync(back, libraryRoots, ct);
    }

    private static void MoveSidecar(string from, string to)
    {
        var src = from + ".tags.txt";
        if (!File.Exists(src)) return;
        try { File.Move(src, to + ".tags.txt", overwrite: false); } catch { /* sidecar is optional */ }
    }

    /// <summary>Deletes folders emptied by the move, walking up — never a library root or anything outside one.</summary>
    internal static void RemoveEmptyFolders(IEnumerable<string> movedFrom, IReadOnlyCollection<string> roots)
    {
        var normRoots = roots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)).ToList();
        foreach (var start in movedFrom.Select(Path.GetDirectoryName).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.GetFullPath(start!).TrimEnd(Path.DirectorySeparatorChar);
            while (normRoots.Any(r => dir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir);
                }
                catch { break; }
                dir = Path.GetDirectoryName(dir)!;
            }
        }
    }
}
