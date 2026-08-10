using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Pikura.Avalonia.Views.Login;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Login result returned from any login backend (WebView2 on Windows,
/// Playwright on Linux, or the manual cookie fallback). Settings are
/// already persisted by the time the caller sees this — they just need
/// to refresh UI / kick off a gallery reload on <see cref="Success"/>.
/// </summary>
public sealed record LoginResult(
    bool Success,
    string? UserId = null,
    string? UserName = null,
    string? ErrorMessage = null);

/// <summary>
/// Linux (and any platform-of-last-resort) login path: opens a headed
/// Chromium window via Playwright, lets the user sign in normally, then
/// reads the <c>PHPSESSID</c> cookie out of the browser context.
///
/// Why Playwright instead of the native WebView controls we used to ship?
/// The old NativeWebView/NativeWebDialog backends require WPE WebKit or
/// libwebkit2gtk to be installed AND working with the user's compositor.
/// On Ubuntu 24.04 the libs are often absent or fail silently in VMs,
/// leaving users to copy-paste PHPSESSID out of DevTools. Playwright
/// bundles its own Chromium that doesn't depend on the host's WebKit at
/// all, so we get a consistent, working login window on every distro.
/// </summary>
public sealed class PlaywrightLoginService
{
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly PixivClient _pixivClient;
    private readonly ILogger<PlaywrightLoginService> _logger;

    private const string LoginUrl = "https://accounts.pixiv.net/login?lang=en&source=pc&view_type=page";
    private const string SelfStatusUrl = "https://www.pixiv.net/touch/ajax/user/self/status?lang=en";
    private const int PollIntervalMs = 1500;

    public PlaywrightLoginService(
        SettingsService settings,
        AccountService accounts,
        PixivClient pixivClient,
        ILogger<PlaywrightLoginService> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _pixivClient = pixivClient;
        _logger = logger;
    }

    /// <summary>
    /// Drives the full Linux login flow:
    ///   1. Ensure Chromium is installed (show a progress dialog the first time).
    ///   2. Launch headed Chromium pointed at Pixiv's login page.
    ///   3. Poll until Pixiv reports we're logged in.
    ///   4. Read <c>PHPSESSID</c> from the browser context and persist it.
    /// Returns a populated <see cref="LoginResult"/> regardless of outcome.
    /// </summary>
    public async Task<LoginResult> LoginAsync(Window owner, bool clearCookies, CancellationToken ct = default)
    {
        // .NET single-file apps extract embedded binaries to a temp dir each run,
        // and the extraction does not preserve the executable bit on Linux.
        // Fix permissions unconditionally here so node is always executable,
        // regardless of whether this is a first-run install or a subsequent launch.
        FixPlaywrightNodePermissions();

        // Step 1: bootstrap — Chromium download is gated behind a visible dialog
        // so the user understands why the app froze for the first 1-3 minutes.
        if (!await EnsureChromiumInstalledAsync(owner, ct).ConfigureAwait(false))
            return new LoginResult(false, ErrorMessage: "Chromium installation was cancelled or failed.");

        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync().ConfigureAwait(false);

            // headed = the user actually sees the window and clicks through the login.
            // No --headless flag, no stealth args needed — this IS a real browser
            // session driven by the user, not a scraper.
            browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--no-first-run", "--no-default-browser-check" },
            }).ConfigureAwait(false);

            // Every login window gets its own fresh BrowserContext (NewContextAsync gives
            // an isolated cookie jar by default, no shared state with the user's regular
            // Chrome profile or with previous Pikura login windows). `clearCookies` is
            // therefore effectively always-true for the Playwright path — we keep the
            // parameter on the public API for symmetry with the WebView2 backend that
            // does distinguish persistent vs ephemeral sessions.
            _ = clearCookies; // intentional: kept for API symmetry; see comment above.
            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1100, Height = 800 },
                UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            };
            var context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);

            var page = await context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(LoginUrl, new PageGotoOptions { Timeout = 30_000 })
                .ConfigureAwait(false);

            // Step 3: poll Pixiv's self-status endpoint from inside the page
            // (so cookies travel automatically). This is the same trick the
            // old NativeWebView flow used. Loop until the user finishes login
            // OR closes the browser window.
            var (userId, userName) = await PollForLoginAsync(page, ct).ConfigureAwait(false);
            if (userId is null)
            {
                return new LoginResult(false, ErrorMessage: "Sign-in window closed before login completed.");
            }

            // Step 4: pull PHPSESSID out of the context's cookie jar.
            var cookies = await context.CookiesAsync(new[] { "https://www.pixiv.net" })
                .ConfigureAwait(false);
            var phpSessId = cookies.FirstOrDefault(c =>
                string.Equals(c.Name, "PHPSESSID", StringComparison.OrdinalIgnoreCase))?.Value;

            if (string.IsNullOrWhiteSpace(phpSessId))
                return new LoginResult(false, ErrorMessage: "Logged in but PHPSESSID cookie was missing.");

            _settings.Update(s =>
            {
                s.PhpSessId = phpSessId;
                s.UserId = userId;
                s.UserName = userName ?? userId;
            });
            try { _accounts.UpsertFromCurrentSession(); } catch { /* non-fatal */ }
            try { await _pixivClient.ValidateSessionAsync().ConfigureAwait(false); } catch { /* non-fatal */ }
            try { _accounts.UpsertFromCurrentSession(); } catch { /* non-fatal — picks up IsPremium resolved above */ }

            return new LoginResult(true, userId, userName);
        }
        catch (OperationCanceledException)
        {
            return new LoginResult(false, ErrorMessage: "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playwright login failed");
            return new LoginResult(false, ErrorMessage: ex.Message);
        }
        finally
        {
            try { if (browser != null) await browser.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { pw?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Polls the page's session-status endpoint until Pixiv reports we're
    /// signed in. The loop exits when:
    ///   - login succeeds (returns userId / userName)
    ///   - the user closes the browser window (page.IsClosed)
    ///   - the caller cancels (ct)
    /// </summary>
    private static async Task<(string? UserId, string? UserName)> PollForLoginAsync(
        IPage page, CancellationToken ct)
    {
        const string script = @"
            (async () => {
                try {
                    const r = await fetch('" + SelfStatusUrl + @"', { credentials: 'include' });
                    const j = await r.json();
                    const u = j && j.body && j.body.user_status;
                    if (u && u.is_logged_in)
                        return { ok: true, userId: String(u.user_id), userName: u.user_name };
                    return { ok: false };
                } catch (e) { return { ok: false }; }
            })()";

        while (!ct.IsCancellationRequested)
        {
            if (page.IsClosed) return (null, null);
            try
            {
                var resultJson = await page.EvaluateAsync<JsonElement>(script).ConfigureAwait(false);
                if (resultJson.ValueKind == JsonValueKind.Object
                    && resultJson.TryGetProperty("ok", out var ok)
                    && ok.ValueKind == JsonValueKind.True)
                {
                    var userId = resultJson.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
                    var userName = resultJson.TryGetProperty("userName", out var un) ? un.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(userId))
                        return (userId, userName);
                }
            }
            catch
            {
                // Page is mid-navigation or got closed — try again on the next tick.
                if (page.IsClosed) return (null, null);
            }

            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (null, null); }
        }
        return (null, null);
    }

    /// <summary>
    /// When Pikura runs as a .NET single-file binary on Linux, embedded native
    /// files are extracted to a temp directory without the executable bit set.
    /// Playwright's installer needs to spawn its bundled <c>node</c> binary, which
    /// fails with EACCES (permission denied) unless we fix the permissions first.
    /// </summary>
    private static void FixPlaywrightNodePermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            // AppContext.BaseDirectory points to the extraction temp dir for
            // single-file apps, e.g. /home/user/.net/Pikura/<hash>/
            var playwrightDir = Path.Combine(AppContext.BaseDirectory, ".playwright");
            if (!Directory.Exists(playwrightDir)) return;

            // chmod -R +x on the entire .playwright dir — covers node, playwright-cli,
            // and any other binaries extracted there. Simple and no extra dependencies.
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                ArgumentList = { "-R", "+x", playwrightDir },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(5000);
        }
        catch { /* non-fatal — the install will surface its own error */ }
    }

    /// <summary>
    /// Installs the system libraries Chromium needs using whatever package manager
    /// is available on the current distro. Best-effort — failures are logged but
    /// do not block the login attempt (the libs may already be installed).
    /// Requires the process to be running as root or with sudo privileges.
    /// </summary>
    private void InstallChromiumSystemDeps()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Core libs required by Playwright's Chromium on most distros.
        // Covers: NSS, ATK/AT-SPI2, GBM, compositing, damage, randr, XKB.
        const string aptPkgs   = "libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 libgbm1 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libxkbcommon0 libpango-1.0-0 libcairo2 libasound2";
        const string dnfPkgs   = "nss atk at-spi2-atk cups-libs mesa-libgbm libXcomposite libXdamage libXfixes libXrandr libxkbcommon pango cairo alsa-lib";
        const string pacmanPkgs= "nss atk at-spi2-atk libcups libdrm mesa libxcomposite libxdamage libxfixes libxrandr libxkbcommon pango cairo alsa-lib";
        const string zypperPkgs= "mozilla-nss libatk-1_0-0 at-spi2-atk libcups2 libdrm2 Mesa-libgbm1 libXcomposite1 libXdamage1 libXfixes3 libXrandr2 libxkbcommon0 libpango-1_0-0 libcairo2 libasound2";

        (string manager, string installArgs)? pm = null;

        if (CommandExists("apt-get"))
            pm = ("apt-get", $"install -y {aptPkgs}");
        else if (CommandExists("dnf"))
            pm = ("dnf", $"install -y {dnfPkgs}");
        else if (CommandExists("pacman"))
            pm = ("pacman", $"-S --noconfirm {pacmanPkgs}");
        else if (CommandExists("zypper"))
            pm = ("zypper", $"install -y {zypperPkgs}");

        if (pm is null)
        {
            _logger.LogWarning("No recognised package manager found — skipping system dep install. Chromium may still launch if libs are already present.");
            return;
        }

        try
        {
            _logger.LogInformation("Installing Chromium system deps via {Manager}", pm.Value.manager);
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pm.Value.manager,
                Arguments = pm.Value.installArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(120_000); // 2 min max
            if (proc.ExitCode != 0)
                _logger.LogWarning("{Manager} install-deps exited {Code} — Chromium may still work if libs are present", pm.Value.manager, proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "system dep install via {Manager} failed — continuing anyway", pm.Value.manager);
        }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "which",
                ArgumentList = { command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            proc.Start();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Ensures Playwright's bundled Chromium is on disk. Returns false if the
    /// user cancelled the install dialog or the install itself failed.
    ///
    /// We use a heuristic check first (looking for the cache directory) to
    /// avoid showing the dialog every launch — Playwright's install command
    /// is idempotent but slow to even start up.
    /// </summary>
    private async Task<bool> EnsureChromiumInstalledAsync(Window owner, CancellationToken ct)
    {
        // Pin: if ANY chromium revision is already cached in our isolated dir
        // we use it as-is, even if the current NuGet's browsers.json now lists
        // a different one. Prevents accidental re-download on NuGet upgrades.
        if (!PlaywrightSettings.NeedsChromiumInstall()) return true;

        using var installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // ChromiumInstallDialog must be constructed on the UI thread, but we
        // cannot wrap ShowDialog inside InvokeAsync — ShowDialog pumps its own
        // nested event loop and would deadlock if the UI thread is already held
        // by InvokeAsync. Instead we:
        //   1. Use InvokeAsync to construct the dialog and call ShowDialog,
        //      capturing the returned Task (which resolves when the dialog closes).
        //   2. Kick off the Chromium install on the thread pool in parallel.
        //   3. Await the dialog Task from the background thread — ShowDialog's
        //      internal loop keeps pumping normally because InvokeAsync has
        //      already returned (we only used it to post the creation, not to
        //      await the dialog itself).
        var dialogTask = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var dialog = new ChromiumInstallDialog(installCts);
            var bundled = PlaywrightSettings.GetBundledChromiumRevision();
            if (!string.IsNullOrEmpty(bundled))
                dialog.SetDetail($"Installing Chromium revision {bundled} into {PlaywrightSettings.BrowsersCacheDir}…");
            return (dialog, dialogShowTask: dialog.ShowDialog(owner));
        });

        var (dlg, showTask) = dialogTask;

        bool installOk = false;
        var installTask = Task.Run(() =>
        {
            try
            {
                // On Linux, .NET's single-file extractor unpacks embedded native binaries
                // (including Playwright's bundled node) without the executable bit set.
                // chmod +x before invoking Program.Main, otherwise we get EACCES (13).
                FixPlaywrightNodePermissions();

                // Program.Main returns an int exit code — 0 = success, non-zero = failure.
                // It does not throw on install errors; we must check the return value.
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                if (exitCode != 0)
                {
                    _logger.LogError("Playwright chromium install exited with code {Code}", exitCode);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { dlg.SetDetail($"Chromium install failed (exit code {exitCode}). Check logs."); }
                        catch { /* dialog already closed */ }
                    });
                    installOk = false;
                }
                else
                {
                    // Chromium needs system libraries (libnss, libatk, libgbm, etc.).
                    // We try to install them via the distro's package manager.
                    // This is best-effort — if it fails we still attempt to launch;
                    // many distros already ship these libs.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { dlg.SetDetail("Installing system dependencies…"); }
                        catch { /* dialog already closed */ }
                    });
                    InstallChromiumSystemDeps();
                    installOk = true;
                }
            }
            catch (Exception ex)
            {
                installOk = false;
                _logger.LogError(ex, "Playwright chromium install threw an exception");
                Dispatcher.UIThread.Post(() =>
                {
                    try { dlg.SetDetail($"Install error: {ex.Message}"); }
                    catch { /* dialog already closed */ }
                });
            }
            finally
            {
                // Give the user a moment to read the error before closing
                if (!installOk) System.Threading.Thread.Sleep(2000);
                Dispatcher.UIThread.Post(() => { try { dlg.Close(); } catch { /* already closed */ } });
            }
        }, installCts.Token);

        // Wait for the dialog to close (either install finished or user cancelled)
        await showTask.ConfigureAwait(false);
        try { await installTask.ConfigureAwait(false); } catch { /* swallowed in installOk */ }

        if (dlg.Cancelled || installCts.IsCancellationRequested) return false;
        return installOk && !PlaywrightSettings.NeedsChromiumInstall();
    }
}
