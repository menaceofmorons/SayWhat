using System.Text;

namespace SayWhat.Core.LanguageDetection;

public enum ScriptCheckKind
{
    NoLetters,
    Decided,
    SharedScript,
    Latin,
    Undetermined,
}

public sealed record ScriptCheckResult(ScriptCheckKind Kind, TextScript DominantScript, string? LanguageCode);

public static class ScriptCheck
{
    public const string Korean = "ko";
    public const string Greek = "el";
    public const string Thai = "th";
    public const string Japanese = "ja";
    public const string Chinese = "zh";

    public static ScriptCheckResult Check(string? text)
    {
        var counts = new Dictionary<TextScript, int>();
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            var script = Classify(rune.Value);
            counts[script] = counts.GetValueOrDefault(script) + 1;
        }

        if (counts.Count == 0)
        {
            return new ScriptCheckResult(ScriptCheckKind.NoLetters, TextScript.None, null);
        }

        if (counts.ContainsKey(TextScript.Kana))
        {
            counts[TextScript.Kana] += counts.GetValueOrDefault(TextScript.Han);
            counts.Remove(TextScript.Han);
        }

        var dominant = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        return dominant switch
        {
            TextScript.Hangul => new(ScriptCheckKind.Decided, dominant, Korean),
            TextScript.Greek => new(ScriptCheckKind.Decided, dominant, Greek),
            TextScript.Thai => new(ScriptCheckKind.Decided, dominant, Thai),
            TextScript.Kana => new(ScriptCheckKind.Decided, dominant, Japanese),
            TextScript.Han => new(ScriptCheckKind.Decided, dominant, Chinese),
            TextScript.Cyrillic or TextScript.Arabic => new(ScriptCheckKind.SharedScript, dominant, null),
            TextScript.Latin => new(ScriptCheckKind.Latin, dominant, null),
            _ => new(ScriptCheckKind.Undetermined, dominant, null),
        };
    }

    private static TextScript Classify(int codePoint) => codePoint switch
    {
        (>= 0x41 and <= 0x24F) or (>= 0x1E00 and <= 0x1EFF) => TextScript.Latin,
        (>= 0x370 and <= 0x3FF) or (>= 0x1F00 and <= 0x1FFF) => TextScript.Greek,
        (>= 0x400 and <= 0x52F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F) => TextScript.Cyrillic,
        (>= 0x600 and <= 0x6FF) or (>= 0x750 and <= 0x77F) or (>= 0x8A0 and <= 0x8FF) or (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) => TextScript.Arabic,
        >= 0xE00 and <= 0xE7F => TextScript.Thai,
        (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) => TextScript.Hangul,
        (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F) => TextScript.Kana,
        (>= 0x2E80 and <= 0x2FDF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F) => TextScript.Han,
        _ => TextScript.Other,
    };
}
