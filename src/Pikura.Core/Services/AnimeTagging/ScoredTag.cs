namespace Pikura.Core.Services;

/// <summary>A single predicted tag with its confidence score.</summary>
public sealed record ScoredTag(string Name, double Confidence);
