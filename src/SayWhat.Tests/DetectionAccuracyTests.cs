using SayWhat.Core.LanguageDetection;
using SayWhat.Platform.Windows;

namespace SayWhat.Tests;

public class DetectionAccuracyTests(FastTextModelFixture fixture) : IClassFixture<FastTextModelFixture>
{
    private const string ReportVariable = "SAYWHAT_EVAL_REPORT";

    private sealed class RecordingDetector(string language, double confidence) : ILanguageDetector
    {
        public List<string> Inputs { get; } = [];

        public bool IsReady => true;

        public DetectionResult Detect(string text)
        {
            Inputs.Add(text);
            return new DetectionResult(language, confidence, TextScript.Latin);
        }
    }

    [Fact]
    public void FixturesLoad_AndRealSampleHasNoLinkMarkup()
    {
        var synthetic = DetectionEvaluation.Synthetic();
        var real = DetectionEvaluation.Real();

        Assert.NotEmpty(synthetic);
        Assert.Equal(170, real.Count);
        Assert.DoesNotContain(real, line => line.Text.Contains("<url=", StringComparison.OrdinalIgnoreCase) || line.Text.Contains("<a ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScriptDecidedLines_AreDecidedWithoutTheDetector()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        var rows = DetectionEvaluation.Evaluate(detector, DetectionEvaluation.Synthetic().Concat(DetectionEvaluation.Real()));

        foreach (var row in rows.Where(row => row.Script == ScriptCheckKind.Decided && !row.Line.IsMixed))
        {
            Assert.Equal(DetectionOutcomeKind.Detected, row.With.Kind);
            Assert.Equal(row.Without.LanguageCode, row.With.LanguageCode);
        }

        foreach (var row in rows.Where(row => row.Line.Category == "short" && row.Line.Label is "ja" or "ko" or "zh"))
        {
            Assert.True(row.IsCorrect(row.With), row.Line.Text);
        }
    }

    [Fact]
    public void ClearSentences_AreAboveTheCutoffAndRightForEveryClientLanguage()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        var rows = DetectionEvaluation.Evaluate(detector, DetectionEvaluation.Synthetic().Where(line => line.Category == "sentence"));

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.True(row.IsCorrect(row.With), $"{row.Line.Label}: {row.Line.Text}");
            Assert.True(row.With.Confidence >= DetectionRunner.DefaultConfidenceCutoff, row.Line.Text);
        }
    }

    [Fact]
    public void BelowTheCutoff_EveryStatisticalLineIsUnknown()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);

        foreach (var line in DetectionEvaluation.Synthetic().Concat(DetectionEvaluation.Real()))
        {
            var setAside = SetAside.Apply(line.Text, DetectionEvaluation.ShipNames);
            var script = ScriptCheck.Check(setAside.Text);
            if (script.Kind is not (ScriptCheckKind.Latin or ScriptCheckKind.SharedScript))
            {
                continue;
            }

            var outcome = DetectionRunner.Run(line.Text, DetectionEvaluation.TargetLanguage, detector, true, DetectionEvaluation.ShipNames, confidenceCutoff: 1.01);

            Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
        }
    }

    [Fact]
    public void ShipNamesSetAside_NeverReachTheDetector()
    {
        var detector = new RecordingDetector("en", 0.9);

        foreach (var line in DetectionEvaluation.Synthetic().Where(line => line.Category == "ship"))
        {
            DetectionRunner.Run(line.Text, "xx", detector, true, DetectionEvaluation.ShipNames);
        }

        Assert.NotEmpty(detector.Inputs);
        foreach (var input in detector.Inputs)
        {
            foreach (var ship in DetectionEvaluation.ShipNames)
            {
                Assert.DoesNotContain(ship, input, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void LineOfOnlyShipNames_IsUnknownAndNeverDetected()
    {
        var detector = new RecordingDetector("en", 0.9);

        var outcome = DetectionRunner.Run("Rifter Megathron", "xx", detector, true, DetectionEvaluation.ShipNames);

        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
        Assert.Empty(detector.Inputs);
    }

    [Fact]
    public void Evaluation_WritesTheReportWhenRequested()
    {
        using var detector = new FastTextLanguageDetector(fixture.ModelPath);
        var synthetic = DetectionEvaluation.Evaluate(detector, DetectionEvaluation.Synthetic());
        var real = DetectionEvaluation.Evaluate(detector, DetectionEvaluation.Real());

        var report = DetectionEvaluation.Report(synthetic, real);

        Assert.Contains("## real lines", report);
        var path = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, report);
        }
    }
}
