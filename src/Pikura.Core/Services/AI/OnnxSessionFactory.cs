using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Pikura.Core.Services.AI;

/// <summary>Creates InferenceSessions with the requested acceleration, falling back to
/// CPU when the GPU provider fails to load.</summary>
public static class OnnxSessionFactory
{
    /// <summary>The provider the most recently created session actually uses
    /// ("DirectML GPU" / "CPU") — read by the settings UI.</summary>
    public static string LastProvider { get; private set; } = "CPU";

    public static InferenceSession Create(string modelPath, string? mode, ILogger? logger)
    {
        var wantGpu = !string.Equals(mode, "Cpu", StringComparison.OrdinalIgnoreCase)
                      && OperatingSystem.IsWindows();
        if (wantGpu)
        {
            try
            {
                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // Required for DirectML — memory pattern + parallelism are unsupported.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                opts.AppendExecutionProvider_DML(0);
                var s = new InferenceSession(modelPath, opts);
                LastProvider = "DirectML GPU";
                logger?.LogInformation("ONNX session {Path} on DirectML GPU", modelPath);
                return s;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "DirectML unavailable — falling back to CPU");
            }
        }
        LastProvider = "CPU";
        return new InferenceSession(modelPath, new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        });
    }
}
