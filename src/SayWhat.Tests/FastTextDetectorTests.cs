using SayWhat.Core.LanguageDetection;
using SayWhat.Platform.Windows;

namespace SayWhat.Tests;

public class FastTextDetectorTests(FastTextModelFixture fixture) : IClassFixture<FastTextModelFixture>
{
    [Fact]
    public void ModelOnDisk_LoadsAndReportsReady()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        Assert.True(detector.IsReady);
    }

    [Fact]
    public void DisposedDetector_IsNotReadyAndReturnsUnknown()
    {
        var detector = new FastTextLanguageDetector(fixture.ModelPath);
        detector.Dispose();

        Assert.False(detector.IsReady);
        Assert.True(detector.Detect("Hello there, how are you today").IsUnknown);
        detector.Dispose();
    }

    [Fact]
    public void MissingModel_ReportsNotReadyAndReturnsUnknown()
    {
        using var detector = new FastTextLanguageDetector(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".ftz"));

        Assert.False(detector.IsReady);
        Assert.True(detector.Detect("Hello there, how are you today").IsUnknown);
    }

    [Fact]
    public void UnreadableModel_ReportsNotReady()
    {
        var path = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".ftz");
        File.WriteAllText(path, "not a model");
        try
        {
            using var detector = new FastTextLanguageDetector(path);

            Assert.False(detector.IsReady);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("en", "Is anyone heading to the Jita market tonight, we need more hands for the fleet", TextScript.Latin)]
    [InlineData("de", "Wir treffen uns heute Abend am Sammelpunkt und fliegen gemeinsam zum Ziel", TextScript.Latin)]
    [InlineData("fr", "Nous nous retrouvons ce soir au point de rassemblement avant de partir ensemble", TextScript.Latin)]
    [InlineData("es", "Nos vemos esta noche en el punto de encuentro antes de salir todos juntos", TextScript.Latin)]
    [InlineData("ru", "Мы встречаемся сегодня вечером на точке сбора и летим вместе к цели", TextScript.Cyrillic)]
    [InlineData("ja", "今夜は集合場所で会って、みんなで一緒に目的地へ向かいましょう", TextScript.Kana)]
    [InlineData("ko", "오늘 밤 집결지에서 만나서 함께 목적지로 출발합시다", TextScript.Hangul)]
    [InlineData("zh", "今晚我们在集合地点见面然后一起前往目的地", TextScript.Han)]
    public void ClearSentence_ReturnsItsLanguageAboveTheCutoff(string language, string text, TextScript script)
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var result = detector.Detect(text);

        Assert.Equal(language, result.LanguageCode);
        Assert.True(result.Confidence >= LanguageDetection.DefaultConfidenceCutoff);
        Assert.InRange(result.Confidence, 0, 1);
        Assert.Equal(script, result.DominantScript);
    }

    [Fact]
    public void SharedScript_ReturnsOnlyACandidateOfThatScript()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var result = detector.Detect("Мы встречаемся сегодня вечером");

        Assert.Contains(result.LanguageCode!, ScriptCandidates.For(TextScript.Cyrillic)!);
    }

    [Fact]
    public void EmptyText_ReturnsUnknown()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        Assert.True(detector.Detect("   ").IsUnknown);
    }
}
