using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pikura.Core.Services;

/// <summary>
/// Local ONNX-based anime image tagger.
/// Supports Danbooru-style taggers such as WD Tagger and ML-Danbooru.
/// Models are downloaded on demand from Hugging Face and cached under
/// %LocalApplicationData%/Pikura/TaggerModels.
/// </summary>
public sealed class AnimeTaggerService : IDisposable
{
    private readonly ILogger<AnimeTaggerService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private InferenceSession? _session;
    private List<TagEntry>? _tags;
    private TaggerModelInfo? _loadedModel;
    private bool _disposed;

    public AnimeTaggerService(ILogger<AnimeTaggerService> logger, HttpClient? http = null)
    {
        _logger = logger;
        _http = http ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
    }

    public static string ModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pikura",
        "TaggerModels");

    public bool IsLoaded => _loadedModel != null && _session != null;
    public string? LoadedModelKey => _loadedModel?.Key;

    /// <summary>Returns the local cache directory for a given model.</summary>
    public static string GetModelDirectory(TaggerModelInfo model) => Path.Combine(ModelsDirectory, model.Key);

    /// <summary>Checks whether both the model weights and tag list are already cached.</summary>
    public static bool IsModelInstalled(TaggerModelInfo model)
    {
        var dir = GetModelDirectory(model);
        return File.Exists(Path.Combine(dir, model.ModelFileName))
            && File.Exists(Path.Combine(dir, model.TagsFileName));
    }

    /// <summary>Downloads missing model files from Hugging Face. Returns true if everything is present at the end.</summary>
    public async Task<bool> EnsureModelInstalledAsync(
        TaggerModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetModelDirectory(model);
        Directory.CreateDirectory(dir);

        var modelPath = Path.Combine(dir, model.ModelFileName);
        var tagsPath = Path.Combine(dir, model.TagsFileName);

        bool ok = true;

        if (!File.Exists(modelPath))
        {
            var url = HuggingFaceRawUrl(model.RepoId, model.ModelFileName);
            _logger.LogInformation("Downloading tagger model {Model} from {Url}", model.Key, url);
            ok &= await DownloadFileAsync(url, modelPath, progress, ct);
        }

        if (!File.Exists(tagsPath))
        {
            var url = HuggingFaceRawUrl(model.RepoId, model.TagsFileName);
            _logger.LogInformation("Downloading tagger tags {Model} from {Url}", model.Key, url);
            ok &= await DownloadFileAsync(url, tagsPath, progress, ct);
        }

        return ok;
    }

    /// <summary>
    /// Loads a model into memory. If it is not cached locally, this method throws;
    /// call <see cref="EnsureModelInstalledAsync"/> first.
    /// </summary>
    public async Task LoadModelAsync(TaggerModelInfo model, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AnimeTaggerService));
            if (_loadedModel?.Key == model.Key && _session != null) return;

            var dir = GetModelDirectory(model);
            var modelPath = Path.Combine(dir, model.ModelFileName);
            var tagsPath = Path.Combine(dir, model.TagsFileName);

            if (!File.Exists(modelPath)) throw new FileNotFoundException($"Model not installed: {modelPath}");
            if (!File.Exists(tagsPath)) throw new FileNotFoundException($"Tags not installed: {tagsPath}");

            _session?.Dispose();
            _session = new InferenceSession(modelPath);
            _tags = LoadTags(tagsPath, model);
            _loadedModel = model;

            _logger.LogInformation("Loaded anime tagger {Model} with {TagCount} tags", model.Key, _tags.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Runs inference on an image and returns scored tags grouped by category.</summary>
    public async Task<AnimeTagResult> TagImageAsync(
        byte[] imageBytes,
        TaggerModelInfo model,
        double threshold = 0.35,
        int maxTags = 50,
        CancellationToken ct = default)
    {
        await LoadModelAsync(model, ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_session == null || _tags == null || _loadedModel == null)
                throw new InvalidOperationException("No tagger model is loaded.");

            var inputTensor = PreprocessImage(imageBytes, _loadedModel);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_session.InputMetadata.First().Key, inputTensor)
            };

            using var outputs = _session.Run(inputs);
            var output = outputs.First().AsTensor<float>();
            if (output == null)
                throw new InvalidOperationException("Tagger model produced no output tensor.");

            var probabilities = output.ToArray();
            if (_loadedModel.ApplySigmoid)
                probabilities = probabilities.Select(Sigmoid).ToArray();

            var scored = _tags
                .Select((tag, i) => (tag, score: i < probabilities.Length ? probabilities[i] : 0.0))
                .Where(x => x.score >= threshold)
                .OrderByDescending(x => x.score)
                .Take(maxTags)
                .Select(x => new ScoredTag(x.tag.Name, x.score))
                .ToList();

            var general = scored.Where(t => CategoryOf(t.Name) == 0).ToList();
            var artist = scored.Where(t => CategoryOf(t.Name) == 1).ToList();
            var copyright = scored.Where(t => CategoryOf(t.Name) == 3).ToList();
            var character = scored.Where(t => CategoryOf(t.Name) == 4).ToList();
            var meta = scored.Where(t => CategoryOf(t.Name) == 5).ToList();
            var rating = scored.Where(t => _loadedModel.CategoryNames.ContainsKey(5) && CategoryOf(t.Name) == 5).ToList();

            return new AnimeTagResult(general, character, copyright, artist, meta, rating);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _lock.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string HuggingFaceRawUrl(string repoId, string fileName) =>
        $"https://huggingface.co/{repoId}/resolve/main/{fileName}";

    private async Task<bool> DownloadFileAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            if (total <= 0 || progress == null)
            {
                await source.CopyToAsync(fs, ct);
            }
            else
            {
                var buffer = new byte[8192];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, n, ct);
                    read += n;
                    progress.Report((double)read / total);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {Url} to {Destination}", url, destination);
            try { File.Delete(destination); } catch { }
            return false;
        }
    }

    private static List<TagEntry> LoadTags(string path, TaggerModelInfo model)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" => LoadCsvTags(path),
            ".txt" => LoadTxtTags(path),
            ".json" => LoadJsonTags(path),
            _ => throw new NotSupportedException($"Unsupported tag file format: {ext}")
        };
    }

    private static List<TagEntry> LoadCsvTags(string path)
    {
        var tags = new List<TagEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (parts[0].Trim().Equals("tag_id", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(parts[0].Trim(), out var id)) continue;
            if (!int.TryParse(parts[2].Trim(), out var category)) continue;
            tags.Add(new TagEntry(id, parts[1].Trim(), category));
        }
        return tags;
    }

    private static List<TagEntry> LoadTxtTags(string path)
    {
        var tags = new List<TagEntry>();
        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
        for (int i = 0; i < lines.Count; i++)
            tags.Add(new TagEntry(i, lines[i], 0));
        return tags;
    }

    private static List<TagEntry> LoadJsonTags(string path)
    {
        var tags = new List<TagEntry>();
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return tags;

        int index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string name = string.Empty;
            int category = 0;

            if (el.ValueKind == JsonValueKind.String)
            {
                name = el.GetString() ?? string.Empty;
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    var pname = prop.Name.ToLowerInvariant();
                    if (pname is "name" or "tag" or "key" && prop.Value.ValueKind == JsonValueKind.String)
                        name = prop.Value.GetString() ?? string.Empty;
                    else if (pname is "category" or "category_id" or "index" && prop.Value.ValueKind == JsonValueKind.Number)
                        category = prop.Value.GetInt32();
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
                tags.Add(new TagEntry(index++, name.Trim(), category));
        }
        return tags;
    }

    private DenseTensor<float> PreprocessImage(byte[] imageBytes, TaggerModelInfo model)
    {
        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap == null)
            throw new InvalidOperationException("Could not decode image bytes.");

        var size = model.InputSize;
        using var resized = bitmap.Resize(
            new SKImageInfo(size, size, SKColorType.Rgba8888),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized == null)
            throw new InvalidOperationException("Could not resize image for tagger.");

        var mean = model.Mean;
        var std = model.Std;
        var tensor = model.ChannelsLast
            ? new DenseTensor<float>(new[] { 1, size, size, 3 })
            : new DenseTensor<float>(new[] { 1, 3, size, size });
        var span = tensor.Buffer.Span;

        var pixels = resized.Pixels;
        int area = size * size;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var c = pixels[y * size + x];

                // Danbooru/WD-style taggers (SmilingWolf, TensorFlow-derived) expect raw 0-255
                // BGR channels-last input with no normalization. PyTorch/ViT-style taggers expect
                // normalized RGB channels-first input. See TaggerModelInfo for the per-model flags.
                float v0 = model.Bgr ? c.Blue : c.Red;
                float v1 = c.Green;
                float v2 = model.Bgr ? c.Red : c.Blue;

                if (!model.RawPixelRange)
                {
                    v0 = (v0 / 255.0f - mean[0]) / std[0];
                    v1 = (v1 / 255.0f - mean[1]) / std[1];
                    v2 = (v2 / 255.0f - mean[2]) / std[2];
                }

                if (model.ChannelsLast)
                {
                    int offset = (y * size + x) * 3;
                    span[offset] = v0;
                    span[offset + 1] = v1;
                    span[offset + 2] = v2;
                }
                else
                {
                    int offset = y * size + x;
                    span[offset] = v0;
                    span[area + offset] = v1;
                    span[2 * area + offset] = v2;
                }
            }
        }

        return tensor;
    }

    private int CategoryOf(string name)
    {
        var entry = _tags?.FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
        return entry?.Category ?? 0;
    }

    private static float Sigmoid(float x) => 1.0f / (1.0f + MathF.Exp(-x));

    private sealed record TagEntry(int Id, string Name, int Category);
}
