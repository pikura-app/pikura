using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pikura.Core.Models;

namespace Pikura.Core.Services;

/// <summary>File formats the metadata exporter can produce.</summary>
public enum MetadataExportFormat
{
    /// <summary>Comma-separated, one row per artwork. Opens directly in Excel/Sheets.</summary>
    Csv,
    /// <summary>Tab-separated. Safer than CSV when titles and tags contain commas.</summary>
    Tsv,
    /// <summary>Structured JSON array, for scripting against.</summary>
    Json,
    /// <summary>Human-readable key/value blocks.</summary>
    Text,
}

/// <summary>
/// Turns artwork metadata into an exportable file. Pure formatting — no file or UI access — so the
/// caller decides where the result goes.
/// </summary>
public static class ArtworkMetadataExporter
{
    /// <summary>Columns, in export order. Centralised so every format stays consistent.</summary>
    private static readonly (string Header, Func<ArtworkPreview, string?> Value)[] Columns =
    [
        ("Id",            a => a.Id),
        ("Title",         a => a.Title),
        ("ArtistId",      a => a.UserId),
        ("ArtistName",    a => a.UserName),
        ("Link",          ArtworkLink),
        ("ArtistLink",    a => string.IsNullOrEmpty(a.UserId) ? null : $"https://www.pixiv.net/users/{a.UserId}"),
        ("Type",          a => a.TypeLabel),
        ("Pages",         a => a.PageCount.ToString(CultureInfo.InvariantCulture)),
        ("Width",         a => a.Width  > 0 ? a.Width.ToString(CultureInfo.InvariantCulture)  : null),
        ("Height",        a => a.Height > 0 ? a.Height.ToString(CultureInfo.InvariantCulture) : null),
        ("Size",          a => a.Width > 0 && a.Height > 0 ? $"{a.Width}x{a.Height}" : null),
        ("Views",         a => a.ViewCount?.ToString(CultureInfo.InvariantCulture)),
        ("Bookmarks",     a => a.BookmarkCount?.ToString(CultureInfo.InvariantCulture)),
        ("Likes",         a => a.LikeCount?.ToString(CultureInfo.InvariantCulture)),
        ("UploadDate",    a => Iso(a.CreateDate)),
        ("UpdatedDate",   a => Iso(a.UpdateDate)),
        ("IsR18",         a => a.IsR18.ToString()),
        ("IsR18G",        a => a.IsR18G.ToString()),
        ("IsAiGenerated", a => a.IsAiGenerated.ToString()),
        ("Tags",          a => a.Tags.Count > 0 ? string.Join("; ", a.Tags) : null),
        ("ThumbnailUrl",  a => a.ThumbnailUrl),
        ("Description",   a => a.Description),
    ];

    /// <summary>Canonical pixiv.net page for an artwork.</summary>
    public static string? ArtworkLink(ArtworkPreview a) =>
        string.IsNullOrEmpty(a.Id) ? null : $"https://www.pixiv.net/en/artworks/{a.Id}";

    // Round-trip ISO 8601 so dates stay sortable and unambiguous across locales.
    private static string? Iso(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>Default file extension for a format, without the dot.</summary>
    public static string ExtensionFor(MetadataExportFormat format) => format switch
    {
        MetadataExportFormat.Csv  => "csv",
        MetadataExportFormat.Tsv  => "tsv",
        MetadataExportFormat.Json => "json",
        _                         => "txt",
    };

    /// <summary>Picks a format from a file name's extension, defaulting to CSV.</summary>
    public static MetadataExportFormat FormatFromPath(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".tsv" => MetadataExportFormat.Tsv,
            ".json" => MetadataExportFormat.Json,
            ".txt" or ".md" => MetadataExportFormat.Text,
            _ => MetadataExportFormat.Csv,
        };

    /// <summary>Renders <paramref name="artworks"/> in the requested format.</summary>
    public static string Export(IEnumerable<ArtworkPreview> artworks, MetadataExportFormat format)
    {
        var list = artworks as IReadOnlyList<ArtworkPreview> ?? artworks.ToList();
        return format switch
        {
            MetadataExportFormat.Json => ExportJson(list),
            MetadataExportFormat.Text => ExportText(list),
            MetadataExportFormat.Tsv  => ExportDelimited(list, '\t'),
            _                         => ExportDelimited(list, ','),
        };
    }

    private static string ExportDelimited(IReadOnlyList<ArtworkPreview> list, char delimiter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, Columns.Select(c => Escape(c.Header, delimiter))));

        foreach (var a in list)
            sb.AppendLine(string.Join(delimiter, Columns.Select(c => Escape(c.Value(a), delimiter))));

        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 style escaping: quote when the value contains the delimiter, a quote or a line
    /// break, and double any embedded quotes. Newlines inside descriptions are the common case
    /// here, and getting this wrong silently corrupts every following row.
    /// </summary>
    private static string Escape(string? value, char delimiter)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var needsQuotes = value.Contains(delimiter)
                       || value.Contains('"')
                       || value.Contains('\n')
                       || value.Contains('\r');

        if (!needsQuotes) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string ExportText(IReadOnlyList<ArtworkPreview> list)
    {
        var sb = new StringBuilder();
        var width = Columns.Max(c => c.Header.Length);

        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.AppendLine(new string('-', 48));

            foreach (var (header, value) in Columns)
            {
                var text = value(list[i]);
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append(header.PadRight(width)).Append("  ").AppendLine(text);
            }
        }

        return sb.ToString();
    }

    private static string ExportJson(IReadOnlyList<ArtworkPreview> list)
    {
        // Projected through the same column set so JSON can't drift from the table formats.
        var rows = list.Select(a =>
        {
            var row = new Dictionary<string, object?>(Columns.Length, StringComparer.Ordinal);
            foreach (var (header, value) in Columns) row[header] = value(a);
            // Tags are more useful as a real array than the "; "-joined column.
            row["Tags"] = a.Tags;
            return row;
        });

        return JsonSerializer.Serialize(rows, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
