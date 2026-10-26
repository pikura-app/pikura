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
/// Detects whether the running copy is a legacy Inno Setup install.
/// </summary>
public static class WindowsInstallInfo
{
    /// <summary>
    /// True only when the running exe's own folder contains <c>unins000.exe</c>. A registry
    /// check is deliberately not used: it made any copy (e.g. a dev build or portable zip)
    /// look like an Inno install whenever an installed Pikura also existed on the machine,
    /// and migrating from such a copy would uninstall the unrelated installed one.
    /// </summary>
    public static bool IsInnoInstall(string exeDir)
        => File.Exists(Path.Combine(exeDir, "unins000.exe"));

    /// <summary>Path to the Inno uninstaller next to the running exe, or null.</summary>
    public static string? FindInnoUninstaller(string exeDir)
    {
        var p = Path.Combine(exeDir, "unins000.exe");
        return File.Exists(p) ? p : null;
    }
}
