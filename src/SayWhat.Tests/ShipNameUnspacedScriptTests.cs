using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests;

public class ShipNameUnspacedScriptTests
{
    private const string Unspaced = @"\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}";
    private const string SpacedLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_-[" + Unspaced + "]]";
    private const string AnyLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_]";
    private const int ShortNameLength = 3;
    private const int RiskListSize = 15;

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
        var (measuredMisses, measuredFalseMatches) = Measure(Build(strategy, Names));

        Assert.Equal(misses, measuredMisses);
        Assert.Equal(falseMatches, measuredFalseMatches);
    }

    [Fact]
    public void ShippedMatcherAgreesWithWholeWordStrategy()
    {
        var shipped = new ShipNameMatcher(Names);

        Assert.Equal(Measure(Build("whole-word", Names)), Measure(shipped.Remove));
    }

    private static readonly string[] StrategyNames = ["whole-word", "substring", "spaced-boundary", "spaced-boundary-min-3"];

    private static readonly Dictionary<string, string[]> MentionTemplates = new()
    {
        ["ja"] = ["{0}で攻撃", "{0}が来た", "私の{0}", "{0}、逃げろ", "{0} 攻撃", "{0}。", "敵の{0}を撃て", "{0}3隻", "{0}pls"],
        ["zh"] = ["{0}在这里", "我的{0}", "{0}，攻击", "{0}很强", "{0} 攻击", "{0}。", "敌人的{0}来了", "{0}3艘", "{0}pls"],
    };

    [Fact]
    public async Task Measure_LiveSdeNames_JapaneseAndChineseStrategies()
    {
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var workFolder = Path.Combine(Path.GetTempPath(), "saywhat-bl008-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        try
        {
            var databasePath = Path.Combine(workFolder, "reference.db");
            new SdeImporter(new DirectorySdeSource(cache!)).Import(databasePath, TestContext.Current.CancellationToken);
            var database = new ReferenceDatabase(databasePath);
            Report($"BL008 source: SDE build {database.SdeVersion}");

            var shipTypeIds = database.ShipNames("en").Select(entry => entry.TypeId).ToHashSet();
            var itemNames = ReadPublishedNonShipNames(cache!, shipTypeIds);
            foreach (var language in MentionTemplates.Keys)
            {
                MeasureLanguage(database, language, itemNames);
            }
        }
        finally
        {
            Directory.Delete(workFolder, recursive: true);
        }
    }

    private static void MeasureLanguage(ReferenceDatabase database, string language, List<Dictionary<string, string>> itemNames)
    {
        var names = database.ShipNames(language).Select(entry => entry.Name).Distinct(StringComparer.Ordinal).ToList();
        var nameSet = names.ToHashSet(StringComparer.Ordinal);
        var mentions = names.SelectMany(name => MentionTemplates[language].Select(template => (Name: name, Text: string.Format(template, name)))).ToList();
        var baits = database.SystemNames(language)
            .Concat(itemNames.Where(map => map.ContainsKey(language)).Select(map => map[language]))
            .Where(text => text.Length > 0 && !nameSet.Contains(text))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Report($"BL008 {language}: {names.Count} ship names; {mentions.Count} mention lines ({MentionTemplates[language].Length} templates per name); {baits.Count} bait names (system names and published non-ship item names)");
        Report($"BL008 {language}: ship name lengths 1:{names.Count(name => name.Length == 1)} 2:{names.Count(name => name.Length == 2)} 3:{names.Count(name => name.Length == 3)} 4+:{names.Count(name => name.Length >= 4)}");

        foreach (var strategy in StrategyNames)
        {
            var remove = Build(strategy, names);
            var misses = mentions.Count(mention => remove(mention.Text).Contains(mention.Name, StringComparison.Ordinal));
            var falseMatches = baits.Count(bait => remove(bait) != bait);
            Report($"BL008 {language} {strategy}: misses {misses}/{mentions.Count}; false matches {falseMatches}/{baits.Count}");
            if (strategy == "substring")
            {
                Assert.Equal(0, misses);
            }
        }

        var hitCounts = names
            .Select(name => (Name: name, Hits: baits.Count(bait => bait.Contains(name, StringComparison.Ordinal))))
            .Where(entry => entry.Hits > 0)
            .OrderByDescending(entry => entry.Hits)
            .ThenBy(entry => entry.Name.Length)
            .ToList();
        Report($"BL008 {language} at risk: {hitCounts.Count} of {names.Count} ship names occur inside a bait name; of 1-2 characters {hitCounts.Count(entry => entry.Name.Length <= 2)}, of 3 characters {hitCounts.Count(entry => entry.Name.Length == 3)}, of 4+ {hitCounts.Count(entry => entry.Name.Length >= 4)}");
        foreach (var entry in hitCounts.Take(RiskListSize))
        {
            Report($"BL008 {language} risk: {entry.Name} (length {entry.Name.Length}) inside {entry.Hits} bait names");
        }

        Assert.True(mentions.Count > 0 && baits.Count > 0);
    }

    private static void Report(string line)
    {
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("SAYWHAT_BL008_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static List<Dictionary<string, string>> ReadPublishedNonShipNames(string cache, HashSet<int> shipTypeIds)
    {
        var result = new List<Dictionary<string, string>>();
        using var file = File.OpenRead(Path.Combine(cache, "types.jsonl"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (shipTypeIds.Contains(record.GetProperty("typeID").GetInt32())
                || !record.TryGetProperty("published", out var published) || !published.GetBoolean())
            {
                continue;
            }

            result.Add(record.GetProperty("name").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty));
        }

        return result;
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

    private static Func<string, string> Build(string strategy, IReadOnlyList<string> names) => strategy switch
    {
        "whole-word" => Compile(names, "(?<!" + AnyLetterOrDigit + ")", "(?!" + AnyLetterOrDigit + ")"),
        "substring" => Compile(names, string.Empty, string.Empty),
        "spaced-boundary" => Compile(names, "(?<!" + SpacedLetterOrDigit + ")", "(?!" + SpacedLetterOrDigit + ")"),
        "spaced-boundary-min-3" => Combine(
            Compile(names.Where(name => name.Length >= ShortNameLength), "(?<!" + SpacedLetterOrDigit + ")", "(?!" + SpacedLetterOrDigit + ")"),
            Compile(names.Where(name => name.Length < ShortNameLength), "(?<!" + AnyLetterOrDigit + ")", "(?!" + AnyLetterOrDigit + ")")),
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
