namespace SayWhat.Core.LanguageDetection;

public enum DetectionOutcomeKind
{
    NotRun,
    Unknown,
    SameLanguage,
    Detected,
}

public sealed record DetectionOutcome(DetectionOutcomeKind Kind, string? LanguageCode, double Confidence)
{
    public static DetectionOutcome NotRun { get; } = new(DetectionOutcomeKind.NotRun, null, 0);

    public static DetectionOutcome Unknown { get; } = new(DetectionOutcomeKind.Unknown, null, 0);
}

public static class LanguageDetection
{
    public const double DefaultConfidenceCutoff = 0.5;

    public static DetectionOutcome Run(
        string? text,
        string targetLanguage,
        ILanguageDetector detector,
        bool translationEnabled,
        IEnumerable<string>? shipNames = null,
        double confidenceCutoff = DefaultConfidenceCutoff)
    {
        if (!translationEnabled)
        {
            return DetectionOutcome.NotRun;
        }

        var setAside = SetAside.Apply(text, shipNames);
        if (setAside.HasNoLetters)
        {
            return DetectionOutcome.Unknown;
        }

        var script = ScriptCheck.Check(setAside.Text);
        return script.Kind switch
        {
            ScriptCheckKind.Decided => Classify(script.LanguageCode, 1, targetLanguage),
            ScriptCheckKind.Latin or ScriptCheckKind.SharedScript =>
                Statistical(setAside.Text, script, targetLanguage, detector, confidenceCutoff),
            _ => DetectionOutcome.Unknown,
        };
    }

    private static DetectionOutcome Statistical(
        string text,
        ScriptCheckResult script,
        string targetLanguage,
        ILanguageDetector detector,
        double confidenceCutoff)
    {
        if (!detector.IsReady)
        {
            return DetectionOutcome.Unknown;
        }

        var result = detector.Detect(text);
        if (result.IsUnknown || result.Confidence < confidenceCutoff)
        {
            return DetectionOutcome.Unknown;
        }

        var candidates = script.Kind == ScriptCheckKind.SharedScript ? ScriptCandidates.For(script.DominantScript) : null;
        if (candidates is not null && !candidates.Contains(result.LanguageCode!))
        {
            return DetectionOutcome.Unknown;
        }

        return Classify(result.LanguageCode, result.Confidence, targetLanguage);
    }

    private static DetectionOutcome Classify(string? languageCode, double confidence, string targetLanguage)
    {
        if (languageCode is null)
        {
            return DetectionOutcome.Unknown;
        }

        var kind = string.Equals(languageCode, targetLanguage, StringComparison.OrdinalIgnoreCase)
            ? DetectionOutcomeKind.SameLanguage
            : DetectionOutcomeKind.Detected;
        return new DetectionOutcome(kind, languageCode, confidence);
    }
}
