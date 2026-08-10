using SkiaSharp;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models;
using Pikura.Core.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Manages a local Ollama instance: process lifecycle, model availability, and chat/vision inference.
/// Uses moondream (the smallest vision-capable model, ~1.7 GB).
/// </summary>
public sealed class OllamaService : IDisposable
{
    public const string DefaultVisionModel  = "llava";  // Better vision model
    public const string DefaultTextModel    = "llama3.2";  // More capable text model
    private const string OllamaHost  = "http://localhost:11434";

    private readonly ILogger<OllamaService> _logger;
    private readonly SettingsService _settings;
    private OllamaApiClient? _client;
    private Chat?            _chat;
    private Process?         _ollamaProcess;
    private bool             _disposed;

    // ── Public state ────────────────────────────────────────────────────────
    public bool IsEnabled      { get; private set; }
    public bool IsReady        { get; private set; }
    public bool IsModelPulled  { get; private set; }
    public string StatusText   { get; private set; } = "Hoshi is off";

    public event EventHandler? StateChanged;

    private Task? _enableTask;

    public OllamaService(ILogger<OllamaService> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Leave at least 2 cores free for the UI thread — CPU-only inference (chat/vision) can
    /// otherwise saturate every core and starve Pikura's own UI thread of scheduling time,
    /// which reads as the whole app "freezing" for the duration of the request even though
    /// nothing is actually deadlocked. Applied per-request via <see cref="RequestOptions.NumThread"/>
    /// (the OLLAMA_NUM_THREADS environment variable is not honored by Ollama — the num_thread
    /// option must be sent with each request) so it takes effect even if Ollama was already
    /// running before Pikura started (e.g. as a background service) and we never launched it.
    /// </summary>
    private static int InferenceThreadCount => Math.Max(1, Environment.ProcessorCount - 2);

    // ── Enable / disable ──────────────────────────────────────────────────────
    public Task EnableAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // If already enabling or enabled, return the same task to avoid races
        if (IsReady) return Task.CompletedTask;
        if (_enableTask is { IsCompleted: false }) return _enableTask;
        _enableTask = EnableInternalAsync(progress, ct);
        return _enableTask;
    }

    private async Task EnableInternalAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // Use an independent token for the startup sequence — we don't want a chat
        // cancellation to abort Ollama launch/model-pull.
        using var startupCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var startupCt = startupCts.Token;

        IsEnabled = true;
        StatusText = "Waking up Hoshi…";
        NotifyState();

        try
        {
            await EnsureOllamaRunningAsync(startupCt);
            progress?.Report("Checking model…");
            StatusText = "Loading Hoshi's model…";
            NotifyState();

            await EnsureModelAsync(progress, startupCt);

            IsReady = true;
            StatusText = "Hoshi 星 ready";
            NotifyState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable Ollama");
            IsEnabled  = false;
            IsReady    = false;
            _enableTask = null;  // allow retry
            StatusText = $"Error: {ex.Message}";
            NotifyState();
        }
    }

    public void Disable()
    {
        IsEnabled = false;
        IsReady   = false;
        StatusText = "Hoshi is off";
        // We intentionally leave the Ollama process running — it's shared system software.
        NotifyState();
    }

    // ── Helper methods to get model names ─────────────────────────────────────
    private string GetTextModel()
    {
        var settings = _settings.Current;
        if (settings.UseCustomHoshiModels && !string.IsNullOrEmpty(settings.HoshiTextModel))
            return settings.HoshiTextModel;
        return DefaultTextModel;
    }

    private string GetVisionModel()
    {
        var settings = _settings.Current;
        if (settings.UseCustomHoshiModels && !string.IsNullOrEmpty(settings.HoshiVisionModel))
            return settings.HoshiVisionModel;
        return DefaultVisionModel;
    }

    // ── Chat (text only) ─────────────────────────────────────────────────────
    public async IAsyncEnumerable<string> ChatAsync(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        // Use text model for better responses
        var textModel = GetTextModel();
        _client!.SelectedModel = textModel;
        var textChat = new Chat(_client) { Options = new RequestOptions { NumThread = InferenceThreadCount } };
        await foreach (var token in textChat.SendAsync(userMessage, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
        // Restore default model
        _client!.SelectedModel = GetTextModel();
    }

    // ── Chat with image ──────────────────────────────────────────────────────
    public async IAsyncEnumerable<string> ChatWithImageAsync(
        string userMessage,
        byte[] imageBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        var visionModel = GetVisionModel();
        _client!.SelectedModel = visionModel;
        var visionChat = new Chat(_client) { Options = new RequestOptions { NumThread = InferenceThreadCount } };
        // async IAsyncEnumerable methods run synchronously up to the first `await` on whatever
        // thread calls MoveNextAsync — for a UI-triggered SendAsync that's the UI thread. Decoding
        // and re-encoding a full-size Pixiv image (up to ~5000x7000px) on that thread could
        // freeze/hang the window for a moment before the first token streams back, which read as
        // an "almost crash". Push it to a background thread so the UI never blocks on it.
        var imageB64 = Convert.ToBase64String(await Task.Run(() => ResizeForVision(imageBytes), ct).ConfigureAwait(false));
        await foreach (var token in visionChat.SendAsync(userMessage, [imageB64], ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
        _client!.SelectedModel = GetTextModel();
    }

    /// <summary>
    /// Downscales image bytes to a max dimension of 512px so Ollama vision models
    /// don't OOM on large Pixiv images (5000×7000px etc.).
    /// Uses SkiaSharp directly — thread-safe, no UI thread required.
    /// </summary>
    private static byte[] ResizeForVision(byte[] src, int maxDim = 512)
    {
        try
        {
            using var skBitmap = SKBitmap.Decode(src);
            if (skBitmap == null) return src;

            int w = skBitmap.Width, h = skBitmap.Height;
            if (w <= maxDim && h <= maxDim) return src;

            float scale = (float)maxDim / Math.Max(w, h);
            int newW = (int)(w * scale), newH = (int)(h * scale);

            using var resized = skBitmap.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized == null) return src;

            using var image = SKImage.FromBitmap(resized);
            using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        }
        catch
        {
            return src;
        }
    }

    /// <summary>Clears the in-memory chat history (starts a fresh conversation).</summary>
    public void ClearHistory() => _chat = _client != null ? new Chat(_client) : null;

    // ── Model management (public API for settings UI) ────────────────────────

    /// <summary>Info about an installed Ollama model.</summary>
    public sealed record InstalledModel(string Name, long SizeBytes, DateTime ModifiedAt, bool SupportsVision);

    /// <summary>Progress info while pulling a model.</summary>
    public sealed record ModelPullProgress(string Status, long Completed, long Total)
    {
        public double Percent => Total > 0 ? Math.Clamp((double)Completed / Total * 100.0, 0, 100) : 0;
    }

    /// <summary>
    /// Make sure Ollama is running and a client is available WITHOUT touching IsEnabled / IsReady.
    /// Safe to call from settings UI to list/manage models without "starting Hoshi".
    /// </summary>
    public async Task EnsureClientAvailableAsync(CancellationToken ct = default)
    {
        if (_client != null && await PingAsync(ct)) return;
        await EnsureOllamaRunningAsync(ct);
    }

    /// <summary>Lists installed Ollama models on the local machine, including whether each supports vision input.</summary>
    public async Task<IReadOnlyList<InstalledModel>> ListInstalledModelsAsync(CancellationToken ct = default)
    {
        await EnsureClientAvailableAsync(ct);
        var models = await _client!.ListLocalModelsAsync(ct);

        // Query capabilities (completion/vision/tools/...) for each model in parallel so the
        // Text/Vision dropdowns can be filtered to models that actually support each mode.
        var withCapabilities = await Task.WhenAll(models.Select(async m =>
        {
            bool supportsVision = false;
            try
            {
                var info = await _client!.ShowModelAsync(new OllamaSharp.Models.ShowModelRequest { Model = m.Name }, ct);
                supportsVision = info?.Capabilities?.Contains("vision", StringComparer.OrdinalIgnoreCase) == true;
            }
            catch
            {
                // Fall back to a name-based heuristic if /api/show fails for this model.
                supportsVision = m.Name is { } n && (
                    n.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("moondream", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("-vl", StringComparison.OrdinalIgnoreCase));
            }
            return new InstalledModel(m.Name ?? "", m.Size, m.ModifiedAt, supportsVision);
        }));

        return withCapabilities
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Pulls/downloads a model by name. Streams progress.</summary>
    public async IAsyncEnumerable<ModelPullProgress> PullManualAsync(
        string modelName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name is required", nameof(modelName));

        await EnsureClientAvailableAsync(ct);

        await foreach (var status in _client!.PullModelAsync(modelName, ct).ConfigureAwait(false))
        {
            if (status is null) continue;
            yield return new ModelPullProgress(
                status.Status ?? "",
                status.Completed,
                status.Total);
        }
    }

    /// <summary>Deletes an installed model.</summary>
    public async Task DeleteModelAsync(string modelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name is required", nameof(modelName));

        await EnsureClientAvailableAsync(ct);
        await _client!.DeleteModelAsync(new OllamaSharp.Models.DeleteModelRequest { Model = modelName }, ct);
    }

    // ── Internal helpers ────────────────────────────────────────────────────
    private async Task EnsureOllamaRunningAsync(CancellationToken ct)
    {
        // Try the existing server first
        if (await PingAsync(ct))
        {
            // Ollama is already running (e.g. as a background service we never launched) —
            // still try to lower its scheduling priority so a heavy Describe/chat request
            // doesn't starve Pikura's own UI thread. Best-effort: the actual CPU-usage fix
            // is RequestOptions.NumThread sent with each request (see InferenceThreadCount).
            try
            {
                foreach (var p in Process.GetProcessesByName("ollama"))
                {
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch { /* ignore per-process failures (e.g. no permission) */ }
                }
            }
            catch { /* non-fatal — priority is a best-effort scheduling hint */ }

            _client = new OllamaApiClient(new Uri(OllamaHost)) { SelectedModel = GetTextModel() };
            return;
        }

        // Try to locate ollama executable — auto-install if missing
        var exe = FindOllamaExecutable();
        if (exe == null)
        {
            StatusText = "Downloading Ollama installer…";
            NotifyState();
            exe = await DownloadAndInstallOllamaAsync(ct);
            if (exe == null)
                throw new InvalidOperationException(
                    "Could not install Ollama automatically. Please install it manually from https://ollama.com and try again.");
        }

        _logger.LogInformation("Starting Ollama process: {Exe}", exe);
        var startInfo = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        _ollamaProcess = new Process { StartInfo = startInfo };
        _ollamaProcess.Start();
        try { _ollamaProcess.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* non-fatal — priority is a best-effort scheduling hint */ }

        // Wait up to 45 seconds for it to respond
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await PingAsync(ct)) break;
            await Task.Delay(500, ct);
        }

        if (!await PingAsync(ct))
            throw new TimeoutException("Ollama did not start within 45 seconds.");

        _client = new OllamaApiClient(new Uri(OllamaHost)) { SelectedModel = GetTextModel() };
    }

    private async Task EnsureModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var models = await _client!.ListLocalModelsAsync(ct);
        var textModel = GetTextModel();
        var visionModel = GetVisionModel();
        var hasTextModel = models.Any(m => m.Name.StartsWith(textModel, StringComparison.OrdinalIgnoreCase));
        var hasVisionModel = models.Any(m => m.Name.StartsWith(visionModel, StringComparison.OrdinalIgnoreCase));

        if (hasTextModel && hasVisionModel)
        {
            IsModelPulled = true;
            _chat = new Chat(_client);
            return;
        }

        // Pull text model first
        if (!hasTextModel)
        {
            progress?.Report($"Pulling {textModel} — first-time only…");
            StatusText = $"Downloading {textModel} for Hoshi…";
            NotifyState();

            await foreach (var status in _client!.PullModelAsync(textModel, ct))
            {
                if (status?.Status is { } s)
                    progress?.Report(s);
            }
        }

        // Then pull vision model
        if (!hasVisionModel)
        {
            progress?.Report($"Pulling {visionModel} — first-time only…");
            StatusText = $"Downloading {visionModel} for Hoshi…";
            NotifyState();

            await foreach (var status in _client!.PullModelAsync(visionModel, ct))
            {
                if (status?.Status is { } s)
                    progress?.Report(s);
            }
        }

        IsModelPulled = true;
        _chat = new Chat(_client);
    }

    private static async Task<bool> PingAsync(CancellationToken _ = default)
    {
        try
        {
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{OllamaHost}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<string?> DownloadAndInstallOllamaAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        var installerUrl = "https://ollama.com/download/OllamaSetup.exe";
        var installerPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _logger.LogInformation("Downloading Ollama installer from {Url}", installerUrl);
            var bytes = await http.GetByteArrayAsync(installerUrl, ct);
            await File.WriteAllBytesAsync(installerPath, bytes, ct);

            StatusText = "Installing Ollama…";
            NotifyState();

            // Run installer silently
            var install = new Process
            {
                StartInfo = new ProcessStartInfo(installerPath, "/S")
                {
                    UseShellExecute = true,
                }
            };
            install.Start();
            await install.WaitForExitAsync(ct);

            // Wait a moment for PATH to settle, then re-check
            await Task.Delay(2000, ct);
            return FindOllamaExecutable();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-install Ollama");
            return null;
        }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }
    }

    private static string? FindOllamaExecutable()
    {
        // Common install locations
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
                @"C:\Program Files\Ollama\ollama.exe",
                "ollama.exe", // PATH
            }
            : new[]
            {
                "/usr/local/bin/ollama",
                "/usr/bin/ollama",
                "ollama",
            };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
            // Check PATH for bare names
            if (!Path.IsPathRooted(c))
            {
                var fromPath = FindInPath(c);
                if (fromPath != null) return fromPath;
            }
        }
        return null;
    }

    private static string? FindInPath(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private void EnsureReady()
    {
        if (!IsReady || _client == null)
            throw new InvalidOperationException("OllamaService is not ready.");
    }

    private void NotifyState() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ollamaProcess?.Kill(entireProcessTree: true); } catch { }
        _ollamaProcess?.Dispose();
    }
}
