using System.Text;
using System.Text.RegularExpressions;

namespace SayWhat.Core.LanguageDetection;

public sealed record SetAsideResult(string Text, bool HasNoLetters);

public static class SetAside
{
    internal static readonly Regex InGameLink = new(
        @"<url=[^>]*>.*?</url>|<a\s[^>]*>.*?</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    internal static readonly Regex Url = new(
        @"(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Digits = new(@"\p{Nd}+", RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static SetAsideResult Apply(string? text, IEnumerable<string>? shipNames = null) =>
        Apply(text, new ShipNameMatcher(shipNames));

    public static SetAsideResult Apply(string? text, ShipNameMatcher shipNameMatcher)
    {
        var result = InGameLink.Replace(text ?? string.Empty, " ");
        result = Url.Replace(result, " ");
        result = shipNameMatcher.Remove(result);
        result = Digits.Replace(result, " ");
        result = Spaces.Replace(result, " ").Trim();

        return new SetAsideResult(result, !result.EnumerateRunes().Any(Rune.IsLetter));
    }
}
