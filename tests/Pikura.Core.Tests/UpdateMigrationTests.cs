using Pikura.Core.Services;
using Xunit;

namespace Pikura.Core.Tests;

public class UpdateMigrationTests
{
    private static UpdateCheckService.GitHubRelease Rel(params string[] names) => new()
    {
        TagName = "v9.9.9",
        Assets = names.Select(n => new UpdateCheckService.GitHubAsset
        {
            Name = n,
            BrowserDownloadUrl = "https://example.test/" + n,
        }).ToArray(),
    };

    [Fact]
    public void Legacy_AssetPicker_Still_Picks_Inno_Setup()
    {
        var rel = Rel(
            "Pikura-9.9.9-windows.exe",       // Velopack setup — must NOT be picked
            "Pikura-9.9.9-Setup.exe",          // Inno installer — legacy clients want this
            "releases.win.json",
            "PikuraApp-9.9.9-full.nupkg",
            "Pikura-9.9.9-windows-portable.zip");
        var a = UpdateCheckService.FindWindowsAsset(rel);
        Assert.Equal("Pikura-9.9.9-Setup.exe", a!.Name);
    }

    [Fact]
    public void VelopackSetup_Finder_Picks_WindowsExe_Only()
    {
        var rel = Rel(
            "Pikura-9.9.9-Setup.exe",
            "Pikura-9.9.9-windows.exe",
            "Pikura-9.9.9-windows-portable.zip");
        var a = UpdateCheckService.FindVelopackSetupAsset(rel);
        Assert.Equal("Pikura-9.9.9-windows.exe", a!.Name);

        Assert.Null(UpdateCheckService.FindVelopackSetupAsset(Rel("Pikura-9.9.9-Setup.exe")));
    }

    [Fact]
    public void InnoInstall_Detected_By_Unins000()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pikura-inno-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(WindowsInstallInfo.IsInnoInstall(dir));
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "x");
            Assert.True(WindowsInstallInfo.IsInnoInstall(dir));
            Assert.NotNull(WindowsInstallInfo.FindInnoUninstaller(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CleanupScript_Contains_DataDirs_Not_DownloadRoot()
    {
        var dirs = DataCleanupScript.GetDataDirs();
        var dl = Path.Combine(Path.GetTempPath(), "my-downloads");
        Assert.DoesNotContain(dirs, d => d == dl);
        var script = DataCleanupScript.WriteScript(1234, dirs);
        var content = File.ReadAllText(script);
        foreach (var d in dirs) Assert.Contains(d, content);
        Assert.DoesNotContain("my-downloads", content);
        if (OperatingSystem.IsWindows())
            Assert.Contains("tasklist", content);
        else
            Assert.Contains("kill -0", content);
        File.Delete(script);
    }
}
