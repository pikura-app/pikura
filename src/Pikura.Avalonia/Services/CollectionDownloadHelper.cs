using System.IO;
using System.Linq;
using Pikura.Core.Models;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Shared logic for turning a <see cref="PixivCollection"/> into download targets, used by both
/// the Collections tab's "Download this collection" button and the Batch Download "By Collection"
/// tab, so the two stay in sync.
/// </summary>
public static class CollectionDownloadHelper
{
    /// <summary>
    /// Builds one <see cref="DownloadTarget"/> per artwork in the collection. Either way, output
    /// lands somewhere under a "Collections" folder rather than mixed in with everything else:
    /// <paramref name="useCollectionFolder"/> = true groups every work into one flat folder named
    /// after the collection (<c>Collections\{title}</c>, via <c>CustomOutputFolder</c> — a
    /// job-level override would be silently discarded, see the comment in
    /// <see cref="Pikura.Avalonia.ViewModels.CollectionsViewModel"/>); = false still roots
    /// everything under <c>Collections\</c> but lets your normal folder/filename template
    /// (per-artist subfolders etc.) apply beneath that, via <c>DownloadRoot</c> instead.
    /// </summary>
    public static (System.Collections.Generic.List<DownloadTarget> Targets, string? Folder) BuildTargets(
        PixivCollection collection, bool useCollectionFolder, string downloadRoot)
        => BuildTargets(collection.Works, collection.Title, useCollectionFolder, downloadRoot);

    /// <summary>
    /// Same folder logic as the <see cref="PixivCollection"/> overload, but for an arbitrary
    /// subset of a collection's works (e.g. just the ones the user checked) rather than the
    /// whole thing — used by the "Download Selected" / "Download Selected with Preset" actions
    /// in the single-collection detail view.
    /// </summary>
    public static (System.Collections.Generic.List<DownloadTarget> Targets, string? Folder) BuildTargets(
        System.Collections.Generic.IReadOnlyList<ArtworkPreview> works, string collectionTitle,
        bool useCollectionFolder, string downloadRoot)
    {
        var collectionsRoot = Path.Combine(downloadRoot, "Collections");
        string? flatFolder = useCollectionFolder ? Path.Combine(collectionsRoot, SanitizeFolderName(collectionTitle)) : null;

        var targets = works.Select(w => new DownloadTarget
        {
            TargetId = w.Id,
            Name = w.Title,
            Type = TargetType.Artwork,
            PageRange = "0",
            CustomSettings = new SettingsOverride
            {
                UseGlobalSettings = false,
                CustomOutputFolder = flatFolder,
                DownloadRoot = flatFolder == null ? collectionsRoot : null,
            },
        }).ToList();

        return (targets, flatFolder ?? collectionsRoot);
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Untitled Collection" : cleaned;
    }
}
