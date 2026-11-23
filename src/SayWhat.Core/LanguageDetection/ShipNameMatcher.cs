using System.Text.RegularExpressions;

namespace SayWhat.Core.LanguageDetection;

public sealed class ShipNameMatcher
{
    private readonly Regex? pattern;

    public ShipNameMatcher(IEnumerable<string>? shipNames)
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
            pattern = new Regex(
                @"(?<![\p{L}\p{M}\p{Nd}_])(?:" + string.Join("|", names) + @")(?![\p{L}\p{M}\p{Nd}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public string Remove(string text) => pattern is null ? text : pattern.Replace(text, " ");
}
