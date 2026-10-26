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

using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Services;

/// <summary>Thrown when Ollama cannot be found running and no executable is installed.</summary>
public sealed class OllamaNotInstalledException : Exception
{
    public OllamaNotInstalledException(string message) : base(message) { }
}

/// <summary>
/// Manages a local Ollama instance: process lifecycle, model availability, and chat/vision inference.
/// Uses moondream (the smallest vision-capable model, ~1.7 GB).
/// </summary>
public sealed class OllamaService : IDisposable
{
    public const string DefaultVisionModel  = "qwen3-vl:4b";
    public const string DefaultTextModel    = "qwen3-vl:4b";
    private const string OllamaHost  = "http://localhost:11434";

    private readonly ILogger<OllamaService> _logger;
    private readonly SettingsService _settings;
    private OllamaApiClient? _client;
    private readonly List<OllamaSharp.Models.Chat.Message> _history = new();
    private readonly Dictionary<string, IReadOnlySet<string>> _capCache = new(StringComparer.OrdinalIgnoreCase);
    private Process?         _ollamaProcess;
    private bool             _disposed;

    // ── Public state ────────────────────────────────────────────────────────
    public bool IsEnabled      { get; private set; }
    public bool IsReady        { get; private set; }
    public bool IsModelPulled  { get; private set; }
    public string StatusText   { get; private set; } = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_HoshiIsOff"];

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
        StatusText = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_WakingUpHoshi"];
        NotifyState();

        try
        {
            await EnsureOllamaRunningAsync(startupCt);
            progress?.Report("Checking model…");
            StatusText = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_LoadingHoshiSModel"];
            NotifyState();

            await EnsureModelAsync(progress, startupCt);

            IsReady = true;
            StatusText = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_HoshiReady"];
            NotifyState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable Ollama");
            IsEnabled  = false;
            IsReady    = false;
            _enableTask = null;  // allow retry
            StatusText = ex is OllamaNotInstalledException
                ? Localization.LocalizationService.Instance["Hoshi_OllamaNotInstalled"]
                : $"Error: {ex.Message}";
            NotifyState();
        }
    }

    public void Disable()
    {
        IsEnabled = false;
        IsReady   = false;
        StatusText = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_HoshiIsOff"];
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

    /// <summary>True when a vision-capable model is configured for image chats.</summary>
    public bool SupportsVision
    {
        get
        {
            var n = GetVisionModel();
            return n.Contains("llava", StringComparison.OrdinalIgnoreCase)
                || n.Contains("moondream", StringComparison.OrdinalIgnoreCase)
                || n.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || n.Contains("-vl", StringComparison.OrdinalIgnoreCase)
                || n.Contains("minicpm", StringComparison.OrdinalIgnoreCase);
        }
    }

    private string GetVisionModel()
    {
        var settings = _settings.Current;
        if (settings.UseCustomHoshiModels && !string.IsNullOrEmpty(settings.HoshiVisionModel))
            return settings.HoshiVisionModel;
        return DefaultVisionModel;
    }

    /// <summary>
    /// Downscales image bytes to a max dimension of 1024px so Ollama vision models
    /// don't OOM on large Pixiv images (5000×7000px etc.).
    /// Uses SkiaSharp directly — thread-safe, no UI thread required.
    /// </summary>
    private static byte[] ResizeForVision(byte[] src, int maxDim = 1024)
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
            using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        catch
        {
            return src;
        }
    }

    /// <summary>Clears the in-memory chat history (starts a fresh conversation).</summary>
    public void ClearHistory() => _history.Clear();

    /// <summary>Seeds chat history from persisted messages (reopening a session).</summary>
    public void ResetHistory(IEnumerable<(string Role, string Content)> turns, byte[]? firstUserImage = null)
    {
        _history.Clear();
        var first = true;
        foreach (var (role, content) in turns)
        {
            var msg = role == "assistant"
                ? new OllamaSharp.Models.Chat.Message(OllamaSharp.Models.Chat.ChatRole.Assistant, content, null)
                : new OllamaSharp.Models.Chat.Message(OllamaSharp.Models.Chat.ChatRole.User, content, null);
            if (first && firstUserImage != null && role == "user")
            {
                msg.Images = [Convert.ToBase64String(firstUserImage)];
                first = false;
            }
            _history.Add(msg);
        }
    }

    /// <summary>A callable chat tool: schema + invoker returning a one-line result.</summary>
    public sealed class HoshiTool
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public Dictionary<string, string>? Parameters { get; init; }
        public IReadOnlyList<string>? RequiredParams { get; init; }
        public required Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<string>> InvokeAsync { get; init; }
    }

    /// <summary>Model capabilities via /api/show, cached per model name.</summary>
    public async Task<IReadOnlySet<string>> GetCapabilitiesAsync(string model, CancellationToken ct = default)
    {
        if (_capCache.TryGetValue(model, out var cached)) return cached;
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await EnsureClientAvailableAsync(ct);
            var info = await _client!.ShowModelAsync(new OllamaSharp.Models.ShowModelRequest { Model = model }, ct);
            if (info?.Capabilities != null) caps.UnionWith(info.Capabilities);
        }
        catch
        {
            if (model.Contains("llava", StringComparison.OrdinalIgnoreCase)
                || model.Contains("moondream", StringComparison.OrdinalIgnoreCase)
                || model.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || model.Contains("-vl", StringComparison.OrdinalIgnoreCase)
                || model.Contains("minicpm", StringComparison.OrdinalIgnoreCase))
                caps.Add("vision");
        }
        _capCache[model] = caps;
        return caps;
    }

    /// <summary>True when the configured vision model advertises the "vision" capability.</summary>
    public async Task<bool> SupportsVisionAsync(CancellationToken ct = default)
        => (await GetCapabilitiesAsync(GetVisionModel(), ct)).Contains("vision");

    /// <summary>True when the model that would handle a request supports tools.</summary>
    public async Task<bool> SupportsToolsAsync(bool withImage, CancellationToken ct = default)
    {
        var model = withImage ? GetVisionModel() : GetTextModel();
        return (await GetCapabilitiesAsync(model, ct)).Contains("tools");
    }

    /// <summary>
    /// Sends one user turn: [system] + budgeted history + new message, streams visible text,
    /// executes tool calls (max 4 rounds), then commits the turn to history.
    /// Replaces ChatAsync/ChatWithImageAsync.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        string userMessage,
        byte[]? image,
        string systemPrompt,
        IReadOnlyList<HoshiTool>? tools,
        bool remember = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        var model = image != null ? GetVisionModel() : GetTextModel();
        var caps = await GetCapabilitiesAsync(model, ct);
        var vision = caps.Contains("vision");
        var useTools = tools is { Count: > 0 } && caps.Contains("tools");

        string? imageB64 = null;
        if (image != null)
            imageB64 = Convert.ToBase64String(await Task.Run(() => ResizeForVision(image), ct).ConfigureAwait(false));

        var history = Pikura.Core.Services.Hoshi.HoshiContextBudget.Select(
            _history.Select(m => (m.Role.ToString() ?? "user", m.Content ?? "")))
            .Select(t => new OllamaSharp.Models.Chat.Message(
                t.Item1 == "assistant" ? OllamaSharp.Models.Chat.ChatRole.Assistant : OllamaSharp.Models.Chat.ChatRole.User,
                t.Item2, null)).ToList();
        // Images: strip all except the newest image-bearing user message's when vision.
        if (_history.Count > 0 && history.Count > 0)
        {
            var kept = _history.Skip(_history.Count - history.Count).ToList();
            var lastImg = kept.FindLastIndex(m => m.Images?.Any() == true);
            for (var i = 0; i < history.Count; i++)
                history[i].Images = (vision && i == lastImg) ? kept[i].Images : null;
        }

        var userMsg = new OllamaSharp.Models.Chat.Message(
            OllamaSharp.Models.Chat.ChatRole.User, userMessage, null);
        if (imageB64 != null) userMsg.Images = [imageB64];

        var messages = new List<OllamaSharp.Models.Chat.Message>
        {
            new(OllamaSharp.Models.Chat.ChatRole.System, systemPrompt, null)
        };
        messages.AddRange(history);
        messages.Add(userMsg);

        var ollamaTools = useTools ? tools!.Select(ToOllamaTool).ToList() : null;
        var toolByName = useTools ? tools!.ToDictionary(t => t.Name) : null;
        var allText = new System.Text.StringBuilder();
        var sb = new System.Text.StringBuilder();

        try
        {
            for (var round = 0; round < 4; round++)
            {
                var req = new OllamaSharp.Models.Chat.ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Stream = true,
                    Options = new OllamaSharp.Models.RequestOptions
                    {
                        NumThread = InferenceThreadCount,
                        NumCtx = 8192,
                    },
                    Tools = ollamaTools,
                };

                sb.Clear();
                var visibleLen = 0; // per round — resets each request
                OllamaSharp.Models.Chat.Message? toolCallMsg = null;
                await foreach (var chunk in _client!.ChatAsync(req, ct).ConfigureAwait(false))
                {
                    if (chunk?.Message?.Content is { } c && c.Length > 0)
                    {
                        sb.Append(c);
                        var visible = StripThink(sb.ToString());
                        if (visible.Length > visibleLen)
                        {
                            var delta = visible[visibleLen..];
                            visibleLen = visible.Length;
                            yield return delta;
                        }
                    }
                    if (chunk?.Message?.ToolCalls is { } tc && tc.Any())
                        toolCallMsg = chunk.Message;
                }
                allText.Append(StripThink(sb.ToString()));
                sb.Clear();

                var calls = toolCallMsg?.ToolCalls?.ToList();
                if (calls is not { Count: > 0 } || toolCallMsg == null || toolByName == null || round == 3) break;

                messages.Add(toolCallMsg);
                foreach (var call in calls)
                {
                    string result;
                    try
                    {
                        if (!toolByName.TryGetValue(call.Function?.Name ?? "", out var tool))
                            result = "Error: unknown tool.";
                        else
                        {
                            var args = call.Function?.Arguments?
                                .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString())
                                ?? new Dictionary<string, string?>();
                            result = await tool.InvokeAsync(args, ct);
                        }
                    }
                    catch (Exception ex) { result = $"Error: {ex.Message}"; }
                    messages.Add(new OllamaSharp.Models.Chat.Message(
                        OllamaSharp.Models.Chat.ChatRole.Tool, result, null));
                }
            }
        }
        finally
        {
            // Commit user + partial/full assistant text even on cancellation mid-stream —
            // losing a half-written answer silently corrupts the conversation for the model.
            if (remember)
            {
                _history.Add(userMsg);
                _history.Add(new OllamaSharp.Models.Chat.Message(
                    OllamaSharp.Models.Chat.ChatRole.Assistant, allText + StripThink(sb.ToString()), null));
            }
        }
    }

    private static string StripThink(string s)
    {
        while (true)
        {
            var a = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (a < 0) return s;
            var b = s.IndexOf("</think>", a, StringComparison.OrdinalIgnoreCase);
            s = b < 0 ? s[..a] : s.Remove(a, b + "</think>".Length - a);
        }
    }

    private static OllamaSharp.Models.Chat.Tool ToOllamaTool(HoshiTool t)
    {
        var props = new Dictionary<string, OllamaSharp.Models.Chat.Property>();
        if (t.Parameters != null)
            foreach (var (name, desc) in t.Parameters)
                props[name] = new OllamaSharp.Models.Chat.Property { Type = "string", Description = desc };
        return new OllamaSharp.Models.Chat.Tool
        {
            Type = "function",
            Function = new OllamaSharp.Models.Chat.Function
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = new OllamaSharp.Models.Chat.Parameters
                {
                    Type = "object",
                    Properties = props,
                    Required = t.RequiredParams?.ToArray(),
                },
            },
        };
    }

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

            _client = new OllamaApiClient(new HttpClient { BaseAddress = new Uri(OllamaHost), Timeout = TimeSpan.FromMinutes(10) }) { SelectedModel = GetTextModel() };
            return;
        }

        // Never auto-install — the Inno Setup wizard popping up unprompted was the
        // reported bug (#23). Installing is only done on explicit user consent via
        // InstallOllamaAsync.
        var exe = FindOllamaExecutable();
        if (exe == null)
            throw new OllamaNotInstalledException(
                "Ollama isn't installed. Hoshi needs Ollama — install it from https://ollama.com");

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

        _client = new OllamaApiClient(new HttpClient { BaseAddress = new Uri(OllamaHost), Timeout = TimeSpan.FromMinutes(10) }) { SelectedModel = GetTextModel() };
    }

    private async Task EnsureModelAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var models = await _client!.ListLocalModelsAsync(ct);

        // Existing users with the old llava+llama3.2 defaults must never get an unexpected
        // qwen3-vl pull — migrate them to explicit custom-model settings instead.
        if (Pikura.Core.Services.Hoshi.HoshiPrompt.ShouldMigrateToLegacyModels(
                _settings.Current.UseCustomHoshiModels, models.Select(m => m.Name)))
        {
            _settings.Update(s =>
            {
                s.UseCustomHoshiModels = true;
                s.HoshiTextModel   = "llama3.2";
                s.HoshiVisionModel = "llava";
            });
            _logger.LogInformation("Migrated Hoshi models to legacy llava+llama3.2 (skipping qwen3-vl pull)");
        }

        var textModel = GetTextModel();
        var visionModel = GetVisionModel();
        var hasTextModel = models.Any(m => m.Name.StartsWith(textModel, StringComparison.OrdinalIgnoreCase));
        var hasVisionModel = models.Any(m => m.Name.StartsWith(visionModel, StringComparison.OrdinalIgnoreCase));

        if (hasTextModel && hasVisionModel)
        {
            IsModelPulled = true;
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

    /// <summary>True if the Ollama server responds or an executable is installed.</summary>
    public async Task<bool> IsOllamaAvailableAsync()
        => await PingAsync(CancellationToken.None) || FindOllamaExecutable() != null;

    /// <summary>Downloads and runs the Ollama installer interactively (Windows only).
    /// Only ever called after the user explicitly consented. Returns true if an Ollama
    /// executable is found afterwards.</summary>
    public async Task<bool> InstallOllamaAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        var installerUrl = "https://ollama.com/download/OllamaSetup.exe";
        var installerPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _logger.LogInformation("Downloading Ollama installer from {Url}", installerUrl);
            var bytes = await http.GetByteArrayAsync(installerUrl, ct);
            await File.WriteAllBytesAsync(installerPath, bytes, ct);

            StatusText = Pikura.Avalonia.Localization.LocalizationService.Instance["Hoshi_InstallingOllama"];
            NotifyState();

            // Run the Inno Setup installer interactively so the user sees and controls the
            // wizard — no silent flags ("/S" is an NSIS flag Inno ignores anyway).
            var install = new Process
            {
                StartInfo = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                }
            };
            install.Start();
            await install.WaitForExitAsync(ct);

            // Wait a moment for PATH to settle, then re-check
            await Task.Delay(2000, ct);
            return FindOllamaExecutable() != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install Ollama");
            return false;
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
