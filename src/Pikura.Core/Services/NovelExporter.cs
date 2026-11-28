using System.IO.Compression;
using System.Net;
using System.Text;
using Pikura.Core.Models;
using SkiaSharp;

namespace Pikura.Core.Services;

public enum NovelExportFormat { Txt, Epub, Pdf, Docx }

/// <summary>Writes one or more novel chapters to TXT, EPUB, PDF or DOCX.</summary>
public static class NovelExporter
{
    public static string Extension(NovelExportFormat f) => f switch
    {
        NovelExportFormat.Epub => ".epub",
        NovelExportFormat.Pdf => ".pdf",
        NovelExportFormat.Docx => ".docx",
        _ => ".txt",
    };

    public static void Export(IReadOnlyList<NovelText> chapters, string title, string author, NovelExportFormat format, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        switch (format)
        {
            case NovelExportFormat.Epub: WriteEpub(chapters, title, author, path); break;
            case NovelExportFormat.Pdf: WritePdf(chapters, title, author, path); break;
            case NovelExportFormat.Docx: WriteDocx(chapters, title, author, path); break;
            default: File.WriteAllText(path, ToText(chapters, title, author), new UTF8Encoding(true)); break;
        }
    }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "novel" : s.Length > 120 ? s[..120] : s;
    }

    /// <summary>Paragraph lines for a chapter: (text, isHeading). Ruby becomes 漢字(かんじ).</summary>
    public static IEnumerable<(string Text, bool Heading)> Lines(NovelText chapter)
    {
        var pages = NovelMarkup.Parse(chapter.Content);
        for (var p = 0; p < pages.Count; p++)
        {
            if (p > 0) yield return ("", false);
            foreach (var b in pages[p])
            {
                var text = string.Concat(b.Spans.Select(s => s.Ruby is { Length: > 0 } r ? $"{s.Text}({r})" : s.Text));
                switch (b.Kind)
                {
                    case NovelBlockKind.Chapter: yield return (text, true); break;
                    case NovelBlockKind.Text:
                        foreach (var line in text.Split('\n')) yield return (line, false);
                        break;
                    case NovelBlockKind.PixivImage:
                    case NovelBlockKind.UploadedImage: yield return ("[image]", false); break;
                }
            }
        }
    }

    public static string ToText(IReadOnlyList<NovelText> chapters, string title, string author)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title).AppendLine(author).AppendLine();
        foreach (var c in chapters)
        {
            if (chapters.Count > 1 || c.Title != title) sb.AppendLine("■ " + c.Title).AppendLine();
            foreach (var (text, heading) in Lines(c)) sb.AppendLine(heading ? "【" + text + "】" : text);
            sb.AppendLine().AppendLine();
        }
        return sb.ToString();
    }

    private static string X(string s) => WebUtility.HtmlEncode(s);

    private static string ChapterXhtml(NovelText c)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"ja\"><head><meta charset=\"utf-8\"/><title>")
          .Append(X(c.Title)).Append("</title><style>p{margin:0 0 .6em;line-height:1.8}</style></head><body><h1>").Append(X(c.Title)).Append("</h1>");
        foreach (var page in NovelMarkup.Parse(c.Content))
        {
            foreach (var b in page)
            {
                if (b.Kind == NovelBlockKind.Chapter) { sb.Append("<h2>").Append(Inline(b.Spans)).Append("</h2>"); continue; }
                if (b.Kind != NovelBlockKind.Text) continue;
                foreach (var line in Inline(b.Spans).Split('\n'))
                    sb.Append(line.Length == 0 ? "<p>&#160;</p>" : "<p>" + line + "</p>");
            }
            sb.Append("<hr/>");
        }
        return sb.Append("</body></html>").ToString();

        static string Inline(IReadOnlyList<NovelSpan> spans) => string.Concat(spans.Select(s =>
            s.Ruby is { Length: > 0 } r ? $"<ruby>{X(s.Text)}<rt>{X(r)}</rt></ruby>"
            : s.Url is { } u ? $"<a href=\"{X(u)}\">{X(s.Text)}</a>" : X(s.Text)));
    }

    private static void WriteEpub(IReadOnlyList<NovelText> chapters, string title, string author, string path)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var nav = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
        {
            var file = $"ch{i + 1:D4}.xhtml";
            Add("OEBPS/" + file, ChapterXhtml(chapters[i]));
            manifest.Append($"<item id=\"c{i}\" href=\"{file}\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{i}\"/>");
            nav.Append($"<li><a href=\"{file}\">{X(chapters[i].Title)}</a></li>");
        }
        Add("OEBPS/nav.xhtml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>{X(title)}</title></head><body><nav epub:type=\"toc\"><ol>{nav}</ol></nav></body></html>");
        Add("OEBPS/content.opf", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">pikura-{X(chapters[0].Id)}</dc:identifier><dc:title>{X(title)}</dc:title><dc:creator>{X(author)}</dc:creator><dc:language>ja</dc:language><meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta></metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>{manifest}</manifest><spine>{spine}</spine></package>");
    }

    private static void WriteDocx(IReadOnlyList<NovelText> chapters, string title, string author, string path)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        var body = new StringBuilder();
        void P(string text, int size = 0, bool bold = false, bool pageBreak = false)
        {
            body.Append("<w:p>");
            if (pageBreak) body.Append("<w:r><w:br w:type=\"page\"/></w:r>");
            body.Append("<w:r>");
            if (size > 0 || bold) body.Append("<w:rPr>").Append(bold ? "<w:b/>" : "").Append(size > 0 ? $"<w:sz w:val=\"{size}\"/>" : "").Append("</w:rPr>");
            body.Append("<w:t xml:space=\"preserve\">").Append(X(text)).Append("</w:t></w:r></w:p>");
        }
        P(title, 40, true);
        P(author);
        foreach (var c in chapters)
        {
            P(c.Title, 32, true, pageBreak: true);
            foreach (var (text, heading) in Lines(c)) P(text, heading ? 28 : 0, heading);
        }
        Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>");
    }

    private static void WritePdf(IReadOnlyList<NovelText> chapters, string title, string author, string path)
    {
        const float pageW = 420, pageH = 595, margin = 40, size = 11, lineH = size * 1.7f;
        using var stream = new SKFileWStream(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title, Author = author, Creator = "Pikura" });
        var typeface = SKFontManager.Default.MatchCharacter('あ') ?? SKTypeface.Default;
        using var font = new SKFont(typeface, size);
        using var bold = new SKFont(typeface, size * 1.4f) { Embolden = true };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        SKCanvas? canvas = null;
        float y = 0;
        void NewPage()
        {
            if (canvas is not null) doc.EndPage();
            canvas = doc.BeginPage(pageW, pageH);
            y = margin;
        }
        void Line(string text, SKFont f, float height)
        {
            if (canvas is null || y + height > pageH - margin) NewPage();
            canvas!.DrawText(text, margin, y + f.Size, SKTextAlign.Left, f, paint);
            y += height;
        }
        void Paragraph(string text, SKFont f, float height)
        {
            if (text.Length == 0) { y += height * 0.6f; return; }
            var max = pageW - margin * 2;
            var start = 0;
            while (start < text.Length)
            {
                var len = 1;
                while (start + len < text.Length && f.MeasureText(text.AsSpan(start, len + 1)) <= max) len++;
                Line(text.Substring(start, len), f, height);
                start += len;
            }
        }
        NewPage();
        Paragraph(title, bold, size * 2.2f);
        Paragraph(author, font, lineH);
        foreach (var c in chapters)
        {
            NewPage();
            Paragraph(c.Title, bold, size * 2.2f);
            foreach (var (text, heading) in Lines(c)) Paragraph(text, heading ? bold : font, heading ? size * 2.2f : lineH);
        }
        doc.EndPage();
        doc.Close();
    }
}
