using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class DetectorContractTests
{
    private sealed class StandInDetector(bool ready) : ILanguageDetector
    {
        public bool IsReady => ready;

        public DetectionResult Detect(string text) =>
            text.Length == 0 ? DetectionResult.Unknown() : new DetectionResult("en", 0.9, TextScript.Latin);
    }

    [Fact]
    public void StandIn_ReturnsCodeConfidenceAndScript()
    {
        ILanguageDetector detector = new StandInDetector(true);

        var result = detector.Detect("hello");

        Assert.True(detector.IsReady);
        Assert.False(result.IsUnknown);
        Assert.Equal("en", result.LanguageCode);
        Assert.Equal(0.9, result.Confidence);
        Assert.Equal(TextScript.Latin, result.DominantScript);
    }

    [Fact]
    public void StandIn_ReturnsUnknown()
    {
        ILanguageDetector detector = new StandInDetector(false);

        var result = detector.Detect(string.Empty);

        Assert.False(detector.IsReady);
        Assert.True(result.IsUnknown);
        Assert.Null(result.LanguageCode);
        Assert.Equal(0, result.Confidence);
    }
}
