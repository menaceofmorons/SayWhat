using System.Text.RegularExpressions;
using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public class ShipNameUnspacedScriptTests
{
    private const string Unspaced = @"\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}";
    private const string SpacedLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_-[" + Unspaced + "]]";
    private const string AnyLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_]";
    private const int ShortNameLength = 3;

    private static readonly string[] Names = ["リフター", "タロス", "ブリッツ", "鹰级", "渡鸦级", "裂谷级"];

    private static readonly (string Text, string? Ship, string? Bait)[] Corpus =
    [
        ("リフターで攻撃", "リフター", null),
        ("タロスが来た", "タロス", null),
        ("鹰级在这里", "鹰级", null),
        ("渡鸦级很强", "渡鸦级", null),
        ("我的鹰级", "鹰级", null),
        ("裂谷级，攻击", "裂谷级", null),
        ("リフター 攻撃", "リフター", null),
        ("ブリッツ。逃げろ", "ブリッツ", null),
        ("アタロスト", null, "アタロスト"),
        ("リフターズ", null, "リフターズ"),
        ("雄鹰级别很高", null, "雄鹰级别很高"),
        ("渡鸦级别", null, "渡鸦级别"),
    ];

    public static TheoryData<string, int, int> Strategies => new()
    {
        { "whole-word", 5, 0 },
        { "substring", 0, 4 },
        { "spaced-boundary", 0, 4 },
        { "spaced-boundary-min-3", 2, 3 },
    };

    [Theory]
    [MemberData(nameof(Strategies))]
    public void StrategyMissAndFalseMatchCounts(string strategy, int misses, int falseMatches)
    {
        var (measuredMisses, measuredFalseMatches) = Measure(Build(strategy));

        Assert.Equal(misses, measuredMisses);
        Assert.Equal(falseMatches, measuredFalseMatches);
    }

    [Fact]
    public void ShippedMatcherAgreesWithWholeWordStrategy()
    {
        var shipped = new ShipNameMatcher(Names);

        Assert.Equal(Measure(Build("whole-word")), Measure(shipped.Remove));
    }

    private static (int Misses, int FalseMatches) Measure(Func<string, string> remove)
    {
        var misses = 0;
        var falseMatches = 0;
        foreach (var (text, ship, bait) in Corpus)
        {
            var remaining = remove(text);
            if (ship is not null && remaining.Contains(ship, StringComparison.Ordinal))
            {
                misses++;
            }

            if (bait is not null && !remaining.Contains(bait, StringComparison.Ordinal))
            {
                falseMatches++;
            }
        }

        return (misses, falseMatches);
    }

    private static Func<string, string> Build(string strategy) => strategy switch
    {
        "whole-word" => Compile(Names, "(?<!" + AnyLetterOrDigit + ")", "(?!" + AnyLetterOrDigit + ")"),
        "substring" => Compile(Names, string.Empty, string.Empty),
        "spaced-boundary" => Compile(Names, "(?<!" + SpacedLetterOrDigit + ")", "(?!" + SpacedLetterOrDigit + ")"),
        "spaced-boundary-min-3" => Combine(
            Compile(Names.Where(name => name.Length >= ShortNameLength), "(?<!" + SpacedLetterOrDigit + ")", "(?!" + SpacedLetterOrDigit + ")"),
            Compile(Names.Where(name => name.Length < ShortNameLength), "(?<!" + AnyLetterOrDigit + ")", "(?!" + AnyLetterOrDigit + ")")),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    private static Func<string, string> Compile(IEnumerable<string> names, string before, string after)
    {
        var alternatives = string.Join("|", names.OrderByDescending(name => name.Length).Select(Regex.Escape));
        var pattern = new Regex(before + "(?:" + alternatives + ")" + after);
        return text => pattern.Replace(text, " ");
    }

    private static Func<string, string> Combine(Func<string, string> first, Func<string, string> second) =>
        text => second(first(text));
}
