using Microsoft.Win32;

namespace Pikura.Core.Services;

/// <summary>
/// Backend that can self-download and apply updates (Velopack on Windows).
/// Implemented in the app layer so Core keeps no Velopack dependency.
/// </summary>
public interface IAppUpdateBackend
{
    /// <summary>True when the app is running from a managed (Velopack) install.</summary>
    bool IsInstalled { get; }
    /// <summary>Check, download and apply the update; restarts the app itself.</summary>
    Task<bool> DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken ct);
}

/// <summary>
/// Detects whether the current Windows install is a legacy Inno Setup install.
/// Registry access is behind a seam for testability.
/// </summary>
public static class WindowsInstallInfo
{
    public const string InnoAppId = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}_is1";

    /// <summary>
    /// True when <paramref name="exeDir"/> contains <c>unins000.exe</c>, or the Inno
    /// uninstall registry key exists (checked via <paramref name="regKeyExists"/>;
    /// pass null to use the real registry on Windows).
    /// </summary>
    public static bool IsInnoInstall(string exeDir, Func<bool>? regKeyExists = null)
    {
        if (File.Exists(Path.Combine(exeDir, "unins000.exe"))) return true;
        return regKeyExists?.Invoke() ?? RegistryKeyExists();
    }

    private static bool RegistryKeyExists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k1 = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{InnoAppId}");
            if (k1 != null) return true;
            using var k2 = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{InnoAppId}");
            return k2 != null;
        }
        catch { return false; }
    }

    /// <summary>Path to the Inno uninstaller next to the running exe, or null.</summary>
    public static string? FindInnoUninstaller(string exeDir)
    {
        var p = Path.Combine(exeDir, "unins000.exe");
        return File.Exists(p) ? p : null;
    }
}
