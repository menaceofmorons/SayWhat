namespace SayWhat.Core.LanguageDetection;

public static class ScriptCandidates
{
    private static readonly IReadOnlySet<string> CyrillicLanguages = new HashSet<string>(StringComparer.Ordinal)
    {
        "ru", "uk", "bg", "be", "sr", "mk", "kk", "ky", "mn", "tg", "ba", "tt", "cv", "ce", "os", "sah", "bxr",
    };

    private static readonly IReadOnlySet<string> ArabicLanguages = new HashSet<string>(StringComparer.Ordinal)
    {
        "ar", "fa", "ur", "ps", "sd", "ckb", "arz", "azb", "pnb",
    };

    public static IReadOnlySet<string>? For(TextScript script) => script switch
    {
        TextScript.Cyrillic => CyrillicLanguages,
        TextScript.Arabic => ArabicLanguages,
        _ => null,
    };
}
