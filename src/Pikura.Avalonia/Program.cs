using Avalonia;
using Pikura.Avalonia.Services;
using System;
using System.IO;

namespace Pikura.Avalonia;

internal static class AgentServiceGuard
{
    /// <summary>Stop/start the PikuraAgent Windows service if it exists (best-effort).</summary>
    public static void StopService() => Run("stop");
    public static void StartService() => Run("start");

    private static void Run(string verb)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "sc.exe", $"{verb} PikuraAgent") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(15000);
        }
        catch { /* no service / no permission — fine */ }
    }
}

sealed class Program
{
    private static CrashReportService? _crashService;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack lifecycle hook — must run before anything else.
        // No-op when the app isn't installed via Velopack.
        // OnAfterUpdateFastCallback: restart the Agent service if it was running
        // (we stop it before applying an update so its files aren't locked).
        if (OperatingSystem.IsWindows())
        {
            Velopack.VelopackApp.Build()
                .OnAfterUpdateFastCallback(_ => AgentServiceGuard.StartService())
                .Run();
        }
        // On Linux, WebKitGTK's GL compositor produces a blank surface on systems
        // without hardware GPU acceleration (VMs, CI, broken Mesa drivers).
        // Setting these env vars before Avalonia/WebKit initialise tells WebKit to
        // fall back to a software render path that works everywhere.
        // They are harmless on real hardware — WebKit silently ignores them when GPU
        // compositing is available and working.
        if (OperatingSystem.IsLinux())
        {
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE", "1");
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER",  "1");
            Environment.SetEnvironmentVariable("WEBKIT_FORCE_SANDBOX",            "0");
            Environment.SetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE",           "1");
        }

        // Pin Playwright's Chromium cache to a Pikura-owned directory so we don't
        // share state with other Playwright apps on the machine, and so future
        // Microsoft.Playwright NuGet upgrades don't silently re-download Chromium
        // when our existing install is still perfectly usable. Has to run before
        // any Playwright API is touched — that's why this lives in Program.Main.
        PlaywrightSettings.EnsureBrowsersPathIsolation();

        // Ensure the working directory is always the app's own directory.
        // Microsoft.Extensions.Hosting calls GetCwd() to set the content root; if the
        // process was launched from a deleted or unreachable directory it throws
        // FileNotFoundException and the app exits silently before any window appears.
        try
        {
            var appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
                Directory.SetCurrentDirectory(appDir);
        }
        catch { /* non-fatal */ }

        // Initialize crash reporting service early
        _crashService = new CrashReportService();

        // Handle unhandled exceptions before Avalonia starts
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _crashService?.GenerateCrashReport(ex, "AppDomain - Fatal unhandled exception before/during startup");
            }
            else
            {
                _crashService?.GenerateCrashReport(
                    new Exception("Unknown non-exception error object: " + e.ExceptionObject?.ToString()),
                    "AppDomain - Fatal non-exception error");
            }
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Generate detailed crash report
            _crashService?.GenerateCrashReport(ex, "Startup - Failed to initialize Avalonia application");

            // Also write simple error for immediate visibility
            var simpleLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura", "startup_error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(simpleLog)!);
            File.WriteAllText(simpleLog, $"[{DateTime.Now}] Startup failed:\n{ex}\n");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
