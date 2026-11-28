using System.IO.Compression;
using Pikura.Core.Models;
using Pikura.Core.Services;
using Xunit;

namespace Pikura.Avalonia.Tests;

public class NovelExportTests
{
    private static readonly NovelText[] Chapters =
    [
        new() { Id = "1", Title = "第一話", UserName = "作者", Content = "[chapter:序章]\n本文[[rb:漢字 > かんじ]]です\n[newpage]\n二ページ目" },
        new() { Id = "2", Title = "第二話", UserName = "作者", Content = "続き & <tags>" },
    ];

    [Theory]
    [InlineData(NovelExportFormat.Txt)]
    [InlineData(NovelExportFormat.Epub)]
    [InlineData(NovelExportFormat.Pdf)]
    [InlineData(NovelExportFormat.Docx)]
    public void Export_Writes_Each_Format(NovelExportFormat format)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pikura-export-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "series" + NovelExporter.Extension(format));
        try
        {
            NovelExporter.Export(Chapters, "シリーズ", "作者", format, path);
            Assert.True(new FileInfo(path).Length > 100);
            switch (format)
            {
                case NovelExportFormat.Txt:
                    var txt = File.ReadAllText(path);
                    Assert.Contains("第二話", txt);
                    Assert.Contains("漢字", txt);
                    Assert.DoesNotContain("[newpage]", txt);
                    break;
                case NovelExportFormat.Pdf:
                    Assert.StartsWith("%PDF", File.ReadAllText(path)[..4]);
                    break;
                case NovelExportFormat.Epub:
                    using (var zip = ZipFile.OpenRead(path))
                    {
                        Assert.Equal("mimetype", zip.Entries[0].FullName);
                        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".opf"));
                    }
                    break;
                case NovelExportFormat.Docx:
                    using (var zip = ZipFile.OpenRead(path))
                    {
                        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                        var xml = r.ReadToEnd();
                        Assert.Contains("続き &amp; &lt;tags&gt;", xml);
                    }
                    break;
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SafeFileName_Strips_Invalid_Characters()
    {
        var name = NovelExporter.SafeFileName("a/b:c*d?\"e<f>g|h");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
        Assert.False(string.IsNullOrWhiteSpace(NovelExporter.SafeFileName("   ")));
    }
}

public class DicLibraryTests
{
    [Fact]
    public void Saved_And_History_Persist_And_Dedupe()
    {
        var path = Path.Combine(Path.GetTempPath(), "pikura-dic-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lib = new DicLibraryService(path);
            var a = new DicLibraryEntry("初音ミク", "Hatsune Miku", "en", "", DateTimeOffset.Now);
            Assert.True(lib.ToggleSaved(a));
            lib.AddHistory(a);
            lib.AddHistory(a with { Time = DateTimeOffset.Now.AddMinutes(1) });
            lib.AddHistory(a with { Lang = "ja" });

            var reloaded = new DicLibraryService(path);
            Assert.True(reloaded.IsSaved("初音ミク", "en"));
            Assert.False(reloaded.IsSaved("初音ミク", "ja"));
            Assert.Equal(2, reloaded.History.Count);
            Assert.False(reloaded.ToggleSaved(a));
            reloaded.ClearHistory();
            Assert.Empty(new DicLibraryService(path).History);
            Assert.Empty(new DicLibraryService(path).Saved);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class DicPagingTests
{
    [Theory]
    [InlineData("Anime category : 7813 results", 652)]
    [InlineData("アニメのカテゴリ：153983件", 12832)]
    [InlineData("SearchArticles found：139 results", 12)]
    [InlineData("Articles found：1,200 results", 100)]
    [InlineData("", null)]
    public void ParseTotalPages_Uses_Twelve_Per_Page(string info, int? expected) =>
        Assert.Equal(expected, Pikura.Avalonia.ViewModels.DicViewModel.ParseTotalPages(info));
}
