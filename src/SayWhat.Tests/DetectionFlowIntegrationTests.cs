using System.Reflection;
using SayWhat.Core.LanguageDetection;
using SayWhat.Platform.Windows;

namespace SayWhat.Tests;

public class DetectionFlowIntegrationTests(FastTextModelFixture fixture) : IClassFixture<FastTextModelFixture>
{
    private const string Target = "en";

    private static string MissingModelPath() =>
        Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".ftz");

    [Theory]
    [InlineData("de", "Wir treffen uns heute Abend am Sammelpunkt und fliegen gemeinsam zum Ziel")]
    [InlineData("fr", "Nous nous retrouvons ce soir au point de rassemblement avant de partir ensemble")]
    [InlineData("es", "Nos vemos esta noche en el punto de encuentro antes de salir todos juntos")]
    [InlineData("ru", "Мы встречаемся сегодня вечером на точке сбора и летим вместе к цели")]
    [InlineData("ja", "今夜は集合場所で会って、みんなで一緒に目的地へ向かいましょう")]
    [InlineData("ko", "오늘 밤 집결지에서 만나서 함께 목적지로 출발합시다")]
    [InlineData("zh", "今晚我们在集合地点见面然后一起前往目的地")]
    public void OtherLanguageLine_IsDetectedThroughTheWholeFlow(string language, string text)
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run(text, Target, detector, translationEnabled: true);

        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal(language, outcome.LanguageCode);
        Assert.InRange(outcome.Confidence, LanguageDetection.DefaultConfidenceCutoff, 1);
    }

    [Fact]
    public void TargetLanguageLine_IsSameLanguage()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run("Is anyone heading to the Jita market tonight, we need more hands for the fleet", Target, detector, translationEnabled: true);

        Assert.Equal(DetectionOutcomeKind.SameLanguage, outcome.Kind);
        Assert.Equal(Target, outcome.LanguageCode);
    }

    [Fact]
    public void TranslationOff_RunsNothing()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run("Wir treffen uns heute Abend am Sammelpunkt", Target, detector, translationEnabled: false);

        Assert.Equal(DetectionOutcomeKind.NotRun, outcome.Kind);
        Assert.Null(outcome.LanguageCode);
    }

    [Fact]
    public void UrlAndDigits_AreSetAsideBeforeDetection()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run(
            "Wir treffen uns heute Abend am Sammelpunkt https://example.com/fleet 42 1337",
            Target,
            detector,
            translationEnabled: true);

        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal("de", outcome.LanguageCode);
    }

    [Fact]
    public void ShipNamesOnly_HaveNoLanguageSignal()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run("Vargur Rifter 3", Target, detector, translationEnabled: true, DetectionEvaluation.ShipNames);

        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void ShipNamesInsideALine_DoNotChangeItsLanguage()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        const string line = "Wir treffen uns heute Abend am Sammelpunkt mit der Vargur und der Rifter";

        var outcome = LanguageDetection.Run(line, Target, detector, translationEnabled: true, DetectionEvaluation.ShipNames);

        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal("de", outcome.LanguageCode);
    }

    [Fact]
    public void ConfidenceBelowTheCutoff_IsUnknown()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run(
            "Wir treffen uns heute Abend am Sammelpunkt",
            Target,
            detector,
            translationEnabled: true,
            confidenceCutoff: 1.01);

        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
    }

    [Theory]
    [InlineData("今晚我们在集合地点见面")]
    [InlineData("今晚我們在集合地點見面")]
    public void SimplifiedAndTraditionalChinese_BothResolveToZh(string text)
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        var outcome = LanguageDetection.Run(text, Target, detector, translationEnabled: true);

        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal("zh", outcome.LanguageCode);
    }

    [Fact]
    public void ModelMissing_ScriptDecidedLinesStillResolveAndStatisticalLinesAreUnknown()
    {
        using var detector = new FastTextLanguageDetector(MissingModelPath());

        var korean = LanguageDetection.Run("오늘 밤 집결지에서 만나서 함께 출발합시다", Target, detector, translationEnabled: true);
        var german = LanguageDetection.Run("Wir treffen uns heute Abend am Sammelpunkt", Target, detector, translationEnabled: true);
        var russian = LanguageDetection.Run("Мы встречаемся сегодня вечером", Target, detector, translationEnabled: true);

        Assert.False(detector.IsReady);
        Assert.Equal("ko", korean.LanguageCode);
        Assert.Equal(DetectionOutcomeKind.Unknown, german.Kind);
        Assert.Equal(DetectionOutcomeKind.Unknown, russian.Kind);
    }

    [Fact]
    public void EverySyntheticAndRealLine_YieldsAWellFormedOutcome()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        var lines = DetectionEvaluation.Synthetic().Concat(DetectionEvaluation.Real()).ToList();

        foreach (var line in lines)
        {
            var outcome = LanguageDetection.Run(line.Text, Target, detector, translationEnabled: true, DetectionEvaluation.ShipNames);

            Assert.NotEqual(DetectionOutcomeKind.NotRun, outcome.Kind);
            Assert.InRange(outcome.Confidence, 0, 1);
            if (outcome.Kind == DetectionOutcomeKind.Unknown)
            {
                Assert.Null(outcome.LanguageCode);
                Assert.Equal(0, outcome.Confidence);
                continue;
            }

            Assert.False(string.IsNullOrEmpty(outcome.LanguageCode));
            Assert.True(
                outcome.Confidence >= LanguageDetection.DefaultConfidenceCutoff,
                $"'{line.Text}' returned {outcome.LanguageCode} at {outcome.Confidence}");
            Assert.Equal(
                string.Equals(outcome.LanguageCode, Target, StringComparison.OrdinalIgnoreCase),
                outcome.Kind == DetectionOutcomeKind.SameLanguage);
        }
    }

    [Fact]
    public void ConcurrentCallers_AllGetTheSameAnswer()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        const string line = "Nous nous retrouvons ce soir au point de rassemblement avant de partir ensemble";

        var codes = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => LanguageDetection.Run(line, Target, detector, translationEnabled: true).LanguageCode)
            .ToList();

        Assert.All(codes, code => Assert.Equal("fr", code));
    }

    [Fact]
    public void CoreContract_IsPublicInCoreAndKeepsTheDetectorSwappable()
    {
        var core = typeof(ILanguageDetector).Assembly;

        Assert.Equal("SayWhat.Core", core.GetName().Name);
        Assert.Same(core, typeof(DetectionResult).Assembly);
        Assert.Same(core, typeof(DetectionOutcome).Assembly);
        Assert.True(typeof(ILanguageDetector).IsAssignableFrom(typeof(FastTextLanguageDetector)));
        Assert.Equal(["Confidence", "DominantScript", "IsUnknown", "LanguageCode"], Properties(typeof(DetectionResult)));
        Assert.Equal(["Confidence", "Kind", "LanguageCode"], Properties(typeof(DetectionOutcome)));
        Assert.Equal(["IsReady"], Properties(typeof(ILanguageDetector)));
        Assert.Equal(["Detect"], typeof(ILanguageDetector).GetMethods().Where(method => !method.IsSpecialName).Select(method => method.Name).Order());
        Assert.Equal(
            ["Decided", "Latin", "NoLetters", "SharedScript", "Undetermined"],
            Enum.GetNames<ScriptCheckKind>().Order());
    }

    private static string[] Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType == type)
            .Select(property => property.Name)
            .Order()
            .ToArray();
}
