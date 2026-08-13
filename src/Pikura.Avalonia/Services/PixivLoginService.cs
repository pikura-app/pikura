using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Pikura.Avalonia.Views.Login;
using Pikura.Core.Services;
using Pikura.Core.Settings;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Single entry point for all Pixiv sign-in flows. Each call site
/// (MainWindow's "Add Account" button, Settings' "Sign In" command, an
/// expired-session re-auth prompt down the line) should call
/// <see cref="LoginAsync"/> and react to the returned <see cref="LoginResult"/>.
///
/// Behind the scenes the service picks the right backend per OS:
/// <list type="bullet">
///   <item>Windows / macOS → embedded <see cref="PixivLoginWindow"/> (WebView2 / WKWebView).</item>
///   <item>Linux           → <see cref="PlaywrightLoginService"/> (Playwright Chromium).</item>
/// </list>
///
/// If the primary backend fails (Chromium install errored, WebView2 missing,
/// etc.) we fall through to the legacy <see cref="ManualCookieDialog"/> so the
/// user can still get in by pasting their PHPSESSID. That dialog's copy was
/// reworded to make clear it's an emergency fallback, not the happy path.
/// </summary>
public sealed class PixivLoginService
{
    private readonly PlaywrightLoginService _playwright;
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly PixivClient _pixivClient;
    private readonly ILogger<PixivLoginService> _logger;

    public PixivLoginService(
        PlaywrightLoginService playwright,
        SettingsService settings,
        AccountService accounts,
        PixivClient pixivClient,
        ILogger<PixivLoginService> logger)
    {
        _playwright = playwright;
        _settings = settings;
        _accounts = accounts;
        _pixivClient = pixivClient;
        _logger = logger;
    }

    /// <summary>
    /// Runs the appropriate login flow for the current OS, persists the
    /// resulting session, and returns success/failure. Callers should
    /// refresh UI (account chip, gallery) only when <c>Success == true</c>.
    /// </summary>
    public async Task<LoginResult> LoginAsync(
        Window owner, bool clearCookies, CancellationToken ct = default)
    {
        try
        {
            LoginResult result;
            if (OperatingSystem.IsLinux())
            {
                result = await _playwright.LoginAsync(owner, clearCookies, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    // Playwright path failed (offline, install cancelled, etc.) — offer the
                    // manual fallback so the user isn't stuck.
                    result = await Dispatcher.UIThread.InvokeAsync(() => TryManualFallbackAsync(owner, result.ErrorMessage));
                }
            }
            else
            {
                result = await Dispatcher.UIThread.InvokeAsync(() => RunEmbeddedWebViewLoginAsync(owner, clearCookies));
            }

            // Fire-and-forget: some pixiv.net subdomains (e.g. embed.pixiv.net, used for
            // Collection collage thumbnails) enforce Cloudflare bot-management that a plain
            // PHPSESSID doesn't satisfy. Don't block login completion on this — it's only
            // needed for that specific feature, and login should still succeed if it fails.
            if (result.Success) _ = RefreshCloudflareSessionAsync(CancellationToken.None);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login orchestration failed");
            return new LoginResult(false, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Solves Pixiv's Cloudflare challenge in a headless browser and persists the resulting
    /// <c>cf_clearance</c> cookie so <see cref="Pikura.Core.Http.PixivHttpClientFactory"/> can
    /// send it on every request. Safe to call repeatedly (e.g. on startup, after login, or from
    /// a manual "Refresh Cloudflare Session" Settings button) — failures are logged, not thrown.
    /// </summary>
    public async Task RefreshCloudflareSessionAsync(CancellationToken ct = default)
    {
        var sid = _settings.Current.PhpSessId;
        if (string.IsNullOrWhiteSpace(sid))
        {
            _logger.LogDebug("Skipping Cloudflare session refresh — no PHPSESSID yet");
            return;
        }

        try
        {
            var cloudflare = new CloudflareSessionService(sid);
            var clearance = await cloudflare.RefreshSessionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(clearance))
            {
                _settings.Update(s =>
                {
                    s.CfClearance = clearance;
                    s.CfClearanceObtainedAt = DateTime.UtcNow;
                });
                _logger.LogInformation("Refreshed Cloudflare cf_clearance cookie");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Cloudflare session");
        }
    }

    /// <summary>True when <see cref="CfClearance"/>'s age suggests it should be refreshed —
    /// Cloudflare clearance tokens are typically valid for hours, not indefinitely.</summary>
    public bool IsCloudflareSessionStale =>
        string.IsNullOrWhiteSpace(_settings.Current.CfClearance) ||
        _settings.Current.CfClearanceObtainedAt is null ||
        DateTime.UtcNow - _settings.Current.CfClearanceObtainedAt.Value > TimeSpan.FromHours(20);

    /// <summary>
    /// Windows / macOS path. Opens the existing <see cref="PixivLoginWindow"/>
    /// (WebView2 or Avalonia WebKit). On Windows specifically, if the WebView
    /// fails to initialise we fall back to the manual cookie dialog so the
    /// user can still sign in.
    /// </summary>
    private async Task<LoginResult> RunEmbeddedWebViewLoginAsync(Window owner, bool clearCookies)
    {
        var loginWindow = new PixivLoginWindow(clearCookiesForNewAccount: clearCookies);
        await loginWindow.ShowDialog(owner);

        if (loginWindow.LoginSucceeded)
        {
            var s = _settings.Current;
            return new LoginResult(true, s.UserId, s.UserName);
        }

        if (loginWindow.WebViewFailed)
            return await TryManualFallbackAsync(owner, "Embedded browser couldn't load.");

        return new LoginResult(false, ErrorMessage: "Sign-in window closed before login completed.");
    }

    /// <summary>
    /// Last-resort: show the manual PHPSESSID dialog. Settings are persisted
    /// inline so the calling site doesn't have to.
    /// </summary>
    private async Task<LoginResult> TryManualFallbackAsync(Window owner, string? reason)
    {
        var dlg = new ManualCookieDialog();
        if (!string.IsNullOrWhiteSpace(reason)) dlg.SetReason(reason);
        await dlg.ShowDialog(owner);

        if (string.IsNullOrWhiteSpace(dlg.PhpSessId))
            return new LoginResult(false, ErrorMessage: "Manual sign-in was cancelled.");

        _settings.Update(s => s.PhpSessId = dlg.PhpSessId);
        try { _accounts.UpsertFromCurrentSession(); } catch { /* non-fatal */ }
        try { await _pixivClient.ValidateSessionAsync().ConfigureAwait(false); } catch { /* non-fatal */ }
        try { _accounts.UpsertFromCurrentSession(); } catch { /* non-fatal — picks up IsPremium resolved above */ }

        var cur = _settings.Current;
        return new LoginResult(true, cur.UserId, cur.UserName);
    }
}
