using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class LanguageDetectionTests
{
    private sealed class StandInDetector(string? language, double confidence, bool ready = true) : ILanguageDetector
    {
        public int Calls { get; private set; }

        public string? LastText { get; private set; }

        public bool IsReady => ready;

        public DetectionResult Detect(string text)
        {
            Calls++;
            LastText = text;
            return new DetectionResult(language, confidence, TextScript.Latin);
        }
    }

    private static DetectionOutcome Run(string text, StandInDetector detector, string target = "en", bool translation = true, string[]? ships = null) =>
        DetectionRunner.Run(text, target, detector, translation, ships);

    [Fact]
    public void BelowTheCutoff_IsUnknown()
    {
        var outcome = Run("hello there friend", new StandInDetector("de", 0.49));

        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void AtTheCutoff_CarriesTheLanguage()
    {
        var outcome = Run("hello there friend", new StandInDetector("de", DetectionRunner.DefaultConfidenceCutoff));

        Assert.Equal(new DetectionOutcome(DetectionOutcomeKind.Detected, "de", 0.5), outcome);
    }

    [Fact]
    public void TargetLanguage_IsSameLanguagePassThrough()
    {
        var outcome = Run("hello there friend", new StandInDetector("en", 0.9));

        Assert.Equal(DetectionOutcomeKind.SameLanguage, outcome.Kind);
    }

    [Fact]
    public void OtherLanguage_IsDetected()
    {
        var outcome = Run("hallo mein freund", new StandInDetector("de", 0.9));

        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal("de", outcome.LanguageCode);
    }

    [Fact]
    public void SharedScript_AcceptsACandidateAndRejectsAForeignLanguage()
    {
        var accepted = Run("привет друг", new StandInDetector("ru", 0.9));
        var rejected = Run("привет друг", new StandInDetector("en", 0.9));

        Assert.Equal("ru", accepted.LanguageCode);
        Assert.Equal(DetectionOutcomeKind.Unknown, rejected.Kind);
    }

    [Theory]
    [InlineData("안녕하세요 친구", "ko")]
    [InlineData("こんにちは 友達", "ja")]
    [InlineData("你好 朋友", "zh")]
    [InlineData("γεια σου φίλε", "el")]
    [InlineData("สวัสดีเพื่อน", "th")]
    public void DecidedScripts_NeverReachTheDetector(string text, string language)
    {
        var detector = new StandInDetector("en", 0.99);

        var outcome = Run(text, detector, target: "de");

        Assert.Equal(0, detector.Calls);
        Assert.Equal(DetectionOutcomeKind.Detected, outcome.Kind);
        Assert.Equal(language, outcome.LanguageCode);
    }

    [Fact]
    public void DecidedScriptMatchingTheTarget_IsSameLanguage()
    {
        var outcome = Run("안녕하세요 친구", new StandInDetector("en", 0.9), target: "ko");

        Assert.Equal(DetectionOutcomeKind.SameLanguage, outcome.Kind);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("https://example.com 42")]
    [InlineData("")]
    public void NoLetters_NeverReachesTheDetector(string text)
    {
        var detector = new StandInDetector("en", 0.99);

        var outcome = Run(text, detector);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void TranslationOff_DoesNotCallTheDetector()
    {
        var detector = new StandInDetector("de", 0.99);

        var outcome = Run("hallo mein freund", detector, translation: false);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(DetectionOutcomeKind.NotRun, outcome.Kind);
    }

    [Fact]
    public void SetAsideText_IsRemovedBeforeDetection()
    {
        var detector = new StandInDetector("de", 0.9);

        Run("Vargur 12 hallo https://example.com", detector, ships: ["Vargur"]);

        Assert.Equal("hallo", detector.LastText);
    }

    [Fact]
    public void NotReadyDetector_IsUnknown()
    {
        var detector = new StandInDetector("de", 0.9, ready: false);

        var outcome = Run("hallo mein freund", detector);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(DetectionOutcomeKind.Unknown, outcome.Kind);
    }
}
