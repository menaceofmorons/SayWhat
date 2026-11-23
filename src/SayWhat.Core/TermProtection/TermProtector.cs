using System.Text;
using System.Text.RegularExpressions;
using SayWhat.Core.Engines;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Core.TermProtection;

public sealed record ProtectedLine(string Text, IReadOnlyList<string> Items, IReadOnlyList<string> DigitSequences);

public sealed record RestoreResult
{
    private RestoreResult(string? text, EngineError? error)
    {
        Text = text;
        Error = error;
    }

    public string? Text { get; }

    public EngineError? Error { get; }

    public bool IsSuccess => Error is null;

    public static RestoreResult Restored(string text) => new(text, null);

    public static RestoreResult BadResponse(string reason) =>
        new(null, new EngineError(EngineErrorCategory.BadResponse, reason));
}

public static class TermProtector
{
    public const string TokenOpen = "⟦";

    public const string TokenClose = "⟧";

    private static readonly string TokenLikePattern =
        Regex.Escape(TokenOpen) + @"\s*\p{Nd}+\s*" + Regex.Escape(TokenClose);

    private static readonly Regex Protectable = new(
        SetAside.InGameLink.ToString() + "|" + SetAside.Url.ToString() + "|" + TokenLikePattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Token = new(
        @"[⟦〚]\s*(?<n>\d+)\s*[⟧〛\]\)\}>】]"
        + @"|[\[\(\{<【]\s*(?<n>\d+)\s*[⟧〛]"
        + @"|\[\[\s*(?<n>\d+)\s*\]\]",
        RegexOptions.CultureInvariant);

    private static readonly Regex CanonicalToken = new(
        Regex.Escape(TokenOpen) + @"\d+" + Regex.Escape(TokenClose),
        RegexOptions.CultureInvariant);

    private static readonly Regex Digits = new(@"\p{Nd}+", RegexOptions.CultureInvariant);

    public static string FormatToken(int index) => TokenOpen + index + TokenClose;

    public static ProtectedLine Protect(
        string? text,
        string sourceLanguage,
        string targetLanguage,
        ReferenceDatabase? reference = null,
        string shipNameLanguage = ReferenceDatabase.DefaultShipLanguage)
    {
        var items = new List<string>();
        string AddItem(string original)
        {
            items.Add(original);
            return FormatToken(items.Count - 1);
        }

        var protectedText = Protectable.Replace(text ?? string.Empty, match => AddItem(match.Value));
        if (reference is not null)
        {
            protectedText = NameProtection.Apply(protectedText, sourceLanguage, targetLanguage, reference, shipNameLanguage, AddItem);
        }

        var digits = Digits.Matches(CanonicalToken.Replace(protectedText, " "))
            .Select(match => match.Value)
            .ToList();

        return new ProtectedLine(protectedText, items, digits);
    }

    public static RestoreResult Restore(ProtectedLine line, string? translated)
    {
        var output = translated ?? string.Empty;
        var matches = Token.Matches(output);
        var seen = new HashSet<int>();
        var itemIndexes = new List<int>();

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["n"].Value, out var index) || index >= line.Items.Count)
            {
                return RestoreResult.BadResponse("Unknown placeholder in reply");
            }

            if (!seen.Add(index))
            {
                return RestoreResult.BadResponse("Placeholder duplicated in reply");
            }

            itemIndexes.Add(index);
        }

        if (seen.Count != line.Items.Count)
        {
            return RestoreResult.BadResponse("Placeholder missing from reply");
        }

        var segments = new List<string>();
        var position = 0;

        foreach (Match match in matches)
        {
            segments.Add(output[position..match.Index]);
            position = match.Index + match.Length;
        }

        segments.Add(output[position..]);

        if (segments.Sum(segment => Digits.Matches(segment).Count) != line.DigitSequences.Count)
        {
            return RestoreResult.BadResponse("Digit sequences differ from the source");
        }

        var digitIndex = 0;
        var builder = new StringBuilder();

        for (var i = 0; i < segments.Count; i++)
        {
            builder.Append(Digits.Replace(segments[i], _ => line.DigitSequences[digitIndex++]));

            if (i < itemIndexes.Count)
            {
                builder.Append(line.Items[itemIndexes[i]]);
            }
        }

        return RestoreResult.Restored(builder.ToString());
    }
}
