using System.Text.RegularExpressions;

namespace SayWhat.Core.LanguageDetection;

public sealed class ShipNameMatcher
{
    private const string Unspaced = @"\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}";
    private const string AnyLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_]";
    private const string SpacedLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_-[" + Unspaced + "]]";

    private readonly Regex? pattern;

    public ShipNameMatcher(IEnumerable<string>? shipNames, bool spacedBoundaryOnly = false)
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
            pattern = new Regex(
                "(?<!" + boundary + ")(?:" + string.Join("|", names) + ")(?!" + boundary + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public string Remove(string text) => pattern is null ? text : pattern.Replace(text, " ");

    public string Replace(string text, Func<string, string> replacement) =>
        pattern is null ? text : pattern.Replace(text, match => replacement(match.Value));
}
