using System.Collections.Generic;

namespace Pikura.Core.Services;

/// <summary>Structured result from an anime image tagger model.</summary>
public sealed record AnimeTagResult(
    IReadOnlyList<ScoredTag> General,
    IReadOnlyList<ScoredTag> Character,
    IReadOnlyList<ScoredTag> Copyright,
    IReadOnlyList<ScoredTag> Artist,
    IReadOnlyList<ScoredTag> Meta,
    IReadOnlyList<ScoredTag> Rating);
