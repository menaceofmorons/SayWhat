namespace SayWhat.Core.LanguageDetection;

public sealed record DetectionResult(string? LanguageCode, double Confidence, TextScript DominantScript)
{
    public bool IsUnknown => LanguageCode is null;

    public static DetectionResult Unknown(TextScript dominantScript = TextScript.None) =>
        new(null, 0, dominantScript);
}
