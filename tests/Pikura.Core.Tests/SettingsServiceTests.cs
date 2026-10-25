using Pikura.Core.Settings;
using Xunit;

namespace Pikura.Core.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pikura-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Rapid_Updates_Are_Debounced()
    {
        // 50ms debounce: 1000 updates should coalesce into very few writes.
        using var svc = new SettingsService(_path, TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < 1000; i++)
        {
            var v = i;
            svc.Update(s => s.DownloadDelaySeconds = v);
        }
        svc.Flush();
        // The final state must be the last update.
        var text = File.ReadAllText(_path);
        Assert.Contains("\"downloadDelaySeconds\": 999", text);
        using var svc2 = new SettingsService(_path);
        Assert.Equal(999, svc2.Current.DownloadDelaySeconds);
    }

    [Fact]
    public void Flush_With_Nothing_Pending_Is_NoOp()
    {
        var missing = Path.Combine(_dir, "never.json");
        using var svc = new SettingsService(missing, TimeSpan.FromMilliseconds(10));
        svc.Flush(); // must not throw or create a file
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Corrupt_Primary_Loads_From_Bak()
    {
        using (var svc = new SettingsService(_path))
        {
            svc.Update(s => s.DownloadDelaySeconds = 77);
            svc.Flush();
            svc.Update(s => s.DownloadDelaySeconds = 78);
            svc.Flush(); // second write leaves .bak holding the first
        }
        // Corrupt the primary; keep the .bak written by File.Replace.
        File.WriteAllText(_path, "{ not json !");
        using var svc2 = new SettingsService(_path);
        Assert.Equal(77, svc2.Current.DownloadDelaySeconds);
    }

    [Fact]
    public void Credentials_Are_Encrypted_On_Disk()
    {
        using (var svc = new SettingsService(_path))
        {
            svc.Update(s => s.PhpSessId = "secret-session-value");
            svc.Flush();
        }
        var text = File.ReadAllText(_path);
        Assert.DoesNotContain("secret-session-value", text);
        // round-trips correctly
        using var svc2 = new SettingsService(_path);
        Assert.Equal("secret-session-value", svc2.Current.PhpSessId);
    }

    [Fact]
    public void Atomic_Write_Leaves_Bak()
    {
        using (var svc = new SettingsService(_path))
        {
            svc.Update(s => s.DownloadDelaySeconds = 1);
            svc.Flush();
            svc.Update(s => s.DownloadDelaySeconds = 2);
            svc.Flush();
        }
        Assert.True(File.Exists(_path + ".bak"));
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }
}
