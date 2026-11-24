namespace SayWhat.Core.Lines;

public sealed record LineParts(string? Timestamp, string? Speaker, string? Badge, string? Marker, string Text);

public static class LineLayout
{
    public const string PendingMarker = "…";
    public const string FailedMarker = "✕";
    public const string UnknownMarker = "?";

    public static LineParts Parts(Line line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.State == LineState.Translated && line.TranslatedText is not null
            ? line.TranslatedText
            : line.OriginalText;
        return new LineParts(line.TimestampText, line.Speaker, Badge(line), Marker(line.State), text);
    }

    public static string Format(Line line)
    {
        var parts = Parts(line);
        string?[] segments = [parts.Timestamp, parts.Speaker, parts.Badge, parts.Marker, parts.Text];
        return string.Join(' ', segments.Where(segment => !string.IsNullOrEmpty(segment)));
    }

    public static string? Badge(Line line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.State is not (LineState.Pending or LineState.Translated or LineState.Failed))
        {
            return null;
        }

        return BadgeFor(line.LanguageCode);
    }

    public static string? Marker(LineState state) => state switch
    {
        LineState.Pending => PendingMarker,
        LineState.Failed => FailedMarker,
        LineState.LanguageUnknown => UnknownMarker,
        _ => null,
    };

    private static string? BadgeFor(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return null;
        }

        var end = languageCode.IndexOfAny(['-', '_']);
        var primary = end < 0 ? languageCode : languageCode[..end];
        return primary.Length == 2 ? primary.ToUpperInvariant() : null;
    }
}
