using System.Collections.Generic;

namespace Pikura.Core.Services;

/// <summary>Metadata for a supported ONNX anime image tagger model.</summary>
public sealed record TaggerModelInfo(
    string Key,
    string Name,
    string Description,
    string RepoId,
    string ModelFileName,
    string TagsFileName,
    int InputSize,
    IReadOnlyList<float> Mean,
    IReadOnlyList<float> Std,
    bool ApplySigmoid,
    long EstimatedBytes,
    IReadOnlyDictionary<int, string> CategoryNames,
    // True for TensorFlow-derived models (e.g. SmilingWolf's WD taggers), which expect NHWC
    // input tensors instead of the NCHW layout used by PyTorch/ViT-style models.
    bool ChannelsLast = false,
    // True if the model expects BGR channel order instead of RGB (also a TF/cv2 convention).
    bool Bgr = false,
    // True if the model expects raw 0-255 pixel values with no ImageNet mean/std normalization.
    bool RawPixelRange = false);

public static class KnownAnimeTaggerModels
{
    /// <summary>WD Tagger v3 (SwinV2). The most widely used Danbooru tagger.</summary>
    public static TaggerModelInfo WdSwinV2TaggerV3 { get; } = new(
        Key: "wd-swinv2-tagger-v3",
        Name: "SmilingWolf WD SwinV2 Tagger v3",
        Description: "General-purpose Danbooru tagger with general, character, copyright, artist, and meta tags.",
        RepoId: "SmilingWolf/wd-swinv2-tagger-v3",
        ModelFileName: "model.onnx",
        TagsFileName: "selected_tags.csv",
        InputSize: 448,
        Mean: new[] { 0.485f, 0.456f, 0.406f },
        Std: new[] { 0.229f, 0.224f, 0.225f },
        ApplySigmoid: true,
        EstimatedBytes: 450L * 1024 * 1024,
        CategoryNames: new Dictionary<int, string>
        {
            [0] = "General",
            [1] = "Artist",
            [3] = "Copyright",
            [4] = "Character",
            [5] = "Meta"
        },
        // SmilingWolf's WD taggers are TensorFlow-derived: NHWC layout, BGR channel order, raw
        // 0-255 pixel values (no ImageNet normalization). Feeding them NCHW/RGB/normalized input
        // (the old default) causes an ONNX Runtime shape-mismatch exception on every call.
        ChannelsLast: true,
        Bgr: true,
        RawPixelRange: true);

    /// <summary>ML-Danbooru ONNX (Caformer-M36).</summary>
    public static TaggerModelInfo MlDanbooruCaformer { get; } = new(
        Key: "ml-danbooru-caformer",
        Name: "DeepGHS ML-Danbooru Caformer-M36",
        Description: "Transformer-based Danbooru tagger with ~12.5k tags and high accuracy.",
        RepoId: "deepghs/ml-danbooru-onnx",
        ModelFileName: "ml_caformer_m36_dec-5-97527.onnx",
        TagsFileName: "tags.txt",
        InputSize: 448,
        Mean: new[] { 0.485f, 0.456f, 0.406f },
        Std: new[] { 0.229f, 0.224f, 0.225f },
        ApplySigmoid: true,
        EstimatedBytes: 500L * 1024 * 1024,
        CategoryNames: new Dictionary<int, string>
        {
            [0] = "General",
            [1] = "Artist",
            [2] = "Copyright",
            [3] = "Character",
            [4] = "Meta",
            [5] = "Rating"
        });

    /// <summary>PixAI Tagger v0.9 (EVA02-based, recall-first).</summary>
    public static TaggerModelInfo PixaiTaggerV09 { get; } = new(
        Key: "pixai-tagger-v0.9",
        Name: "PixAI Tagger v0.9",
        Description: "Recall-first Danbooru tagger trained on a 2025 snapshot; strong character coverage.",
        RepoId: "1038lab/pixai-tagger",
        ModelFileName: "model.onnx",
        TagsFileName: "tags.json",
        InputSize: 448,
        Mean: new[] { 0.485f, 0.456f, 0.406f },
        Std: new[] { 0.229f, 0.224f, 0.225f },
        ApplySigmoid: true,
        EstimatedBytes: 600L * 1024 * 1024,
        CategoryNames: new Dictionary<int, string>
        {
            [0] = "General",
            [1] = "Character",
            [2] = "Copyright"
        });

    public static IReadOnlyList<TaggerModelInfo> All { get; } = new[]
    {
        WdSwinV2TaggerV3,
        MlDanbooruCaformer,
        PixaiTaggerV09
    };

    public static TaggerModelInfo? GetByKey(string key)
    {
        foreach (var m in All)
        {
            if (m.Key.Equals(key, System.StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }
}
