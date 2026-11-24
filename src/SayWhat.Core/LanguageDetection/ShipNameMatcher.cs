using System.Text.RegularExpressions;

namespace SayWhat.Core.LanguageDetection;

public sealed class ShipNameMatcher
{
    private const string Unspaced = @"\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}";
    private const string AnyLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_]";
    private const string SpacedLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_-[" + Unspaced + "]]";

    private const string HangulScript = @"\p{IsHangulSyllables}\p{IsHangulJamo}\p{IsHangulCompatibilityJamo}";
    private const string NonHangulLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_-[" + HangulScript + "]]";

    private readonly Regex? pattern;

    public ShipNameMatcher(IEnumerable<string>? shipNames, bool spacedBoundaryOnly = false, bool hangulMayFollow = false)
    {
        var names = (shipNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length)
            .Select(Regex.Escape)
            .ToList();

        if (names.Count > 0)
        {
            var boundary = spacedBoundaryOnly ? SpacedLetterOrDigit : AnyLetterOrDigit;
            var after = hangulMayFollow ? NonHangulLetterOrDigit : boundary;
            pattern = new Regex(
                "(?<!" + boundary + ")(?:" + string.Join("|", names) + ")(?!" + after + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public string Remove(string text) => pattern is null ? text : pattern.Replace(text, " ");

    public string Replace(string text, Func<string, string> replacement) =>
        pattern is null ? text : pattern.Replace(text, match => replacement(match.Value));
}
