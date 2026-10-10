using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Pikura.Core.Models;
using Pikura.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Saves artwork metadata (id, artist, link, size, upload date, views, bookmarks, tags…) to a
/// file. The format follows the extension the user picks in the save dialog.
/// </summary>
public sealed class MetadataExportService
{
    private readonly ILogger<MetadataExportService> _logger;

    public MetadataExportService(ILogger<MetadataExportService> logger) => _logger = logger;

    /// <summary>
    /// Prompts for a destination and writes the metadata.
    /// </summary>
    /// <param name="artworks">Artworks to export; empty input is a no-op.</param>
    /// <param name="owner">Window owning the save dialog.</param>
    /// <param name="suggestedName">File name without extension.</param>
    /// <returns>Number of artworks written; 0 if cancelled or failed.</returns>
    public async Task<int> ExportAsync(
        IEnumerable<ArtworkPreview> artworks,
        Window? owner,
        string? suggestedName = null)
    {
        var list = artworks?.Where(a => a is not null).ToList() ?? [];
        if (list.Count == 0 || owner is null) return 0;

        var defaultName = Sanitize(suggestedName)
            ?? $"pikura-metadata-{DateTime.Now:yyyyMMdd-HHmmss}";

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = list.Count == 1 ? "Export artwork metadata" : $"Export metadata for {list.Count} artworks",
            SuggestedFileName = defaultName,
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("CSV spreadsheet")   { Patterns = ["*.csv"] },
                new FilePickerFileType("Tab-separated")     { Patterns = ["*.tsv"] },
                new FilePickerFileType("JSON")              { Patterns = ["*.json"] },
                new FilePickerFileType("Plain text")        { Patterns = ["*.txt"] },
            ],
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return 0;

        try
        {
            var format = ArtworkMetadataExporter.FormatFromPath(path);
            var content = ArtworkMetadataExporter.Export(list, format);

            // UTF-8 *with* BOM: without it Excel misreads non-ASCII titles and tags — which is
            // most of them here — as mojibake.
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return list.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export artwork metadata to {Path}", path);
            return 0;
        }
    }

    /// <summary>Strips characters the filesystem won't accept, so an artwork title can seed the name.</summary>
    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var cleaned = new string(name
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0) return null;
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }
}
