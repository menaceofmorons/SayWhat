using Panlingo.LanguageIdentification.FastText;

namespace SayWhat.Tests;

public class LanguageCoverageTests(FastTextModelFixture fixture) : IClassFixture<FastTextModelFixture>
{
    private const string LabelPrefix = "__label__";

    public static TheoryData<string> ClientLanguages => ["en", "de", "fr", "es", "ru", "ja", "ko", "zh"];

    [Theory]
    [MemberData(nameof(ClientLanguages))]
    public void ModelLabelSet_ContainsEachClientLanguage(string code)
    {
        Assert.Contains(code, Labels());
    }

    private HashSet<string> Labels()
    {
        using var detector = new FastTextDetector();
        detector.LoadModel(fixture.ModelPath);
        return detector.Predict("hello there", 1000, 0f)
            .Select(prediction => prediction.Label.StartsWith(LabelPrefix, StringComparison.Ordinal) ? prediction.Label[LabelPrefix.Length..] : prediction.Label)
            .ToHashSet(StringComparer.Ordinal);
    }
}
