using SayWhat.Core.LogIngest;

namespace SayWhat.Core.Lines;

public sealed record Line(
    LineId LineId,
    string? TimestampText,
    DateTimeOffset? TimestampUtc,
    string? Speaker,
    LineKind Kind,
    string OriginalText,
    string? LanguageCode,
    double LanguageConfidence,
    LineState State,
    string? TranslatedText,
    DateTimeOffset? Deadline)
{
    public static Line FromParsed(ParsedLine parsed, LineState state) =>
        new(
            parsed.LineId,
            parsed.TimestampText,
            parsed.TimestampUtc,
            parsed.Speaker,
            parsed.Kind,
            parsed.Text,
            null,
            0,
            state,
            null,
            null);
}
