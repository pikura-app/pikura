using Pikura.Core.Models;
using Xunit;

namespace Pikura.Core.Tests;

/// <summary>pixiv's aiType: 0 = not specified, 1 = not AI-generated, 2 = AI-generated.</summary>
public class AiTypeTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ArtworkPreview_IsAiGenerated_Only_For_AiType_2(int aiType, bool expected)
        => Assert.Equal(expected, new ArtworkPreview { AiType = aiType }.IsAiGenerated);
}
