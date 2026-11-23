using System.Text;
using System.Text.RegularExpressions;

namespace SayWhat.Core.LanguageDetection;

public sealed record SetAsideResult(string Text, bool HasNoLetters);

public static class SetAside
{
    private static readonly Regex InGameLink = new(
        @"<url=[^>]*>.*?</url>|<a\s[^>]*>.*?</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Url = new(
        @"(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Digits = new(@"\p{Nd}+", RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static SetAsideResult Apply(string? text, IEnumerable<string>? shipNames = null)
    {
        var result = InGameLink.Replace(text ?? string.Empty, " ");
        result = Url.Replace(result, " ");
        result = RemoveShipNames(result, shipNames);
        result = Digits.Replace(result, " ");
        result = Spaces.Replace(result, " ").Trim();

        return new SetAsideResult(result, !result.EnumerateRunes().Any(Rune.IsLetter));
    }

    private static string RemoveShipNames(string text, IEnumerable<string>? shipNames)
    {
        var names = (shipNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length)
            .Select(Regex.Escape)
            .ToList();

        if (names.Count == 0)
        {
            return text;
        }

        var pattern = @"(?<![\p{L}\p{M}\p{Nd}_])(?:" + string.Join("|", names) + @")(?![\p{L}\p{M}\p{Nd}_])";
        return Regex.Replace(text, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
