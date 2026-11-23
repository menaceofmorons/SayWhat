using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SayWhat.Core.ReferenceData;
using SayWhat.Core.TermProtection;

namespace SayWhat.Tests;

public sealed class NameProtectionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-names-" + Guid.NewGuid().ToString("N"));
    private readonly ReferenceDatabase database;

    public NameProtectionTests()
    {
        Directory.CreateDirectory(directory);
        var sde = SyntheticSde.Create();
        sde.Set(
            "types",
            false,
            SyntheticSde.ShipLine(600, 25, code => code switch { "en" => "Rifter", "ja" => "リフター", "zh" => "裂谷级", "de" => "Rifter", "fr" => "Rifteur", _ => $"Rifter-{code}" }),
            SyntheticSde.ShipLine(601, 25, code => code switch { "en" => "Rifter Navy Issue", "fr" => "Rifteur Marine", _ => $"RNI-{code}" }),
            SyntheticSde.ShipLine(602, 25, code => code switch { "en" => "Hawk", "ja" => "ホーク", _ => $"Hawk-{code}" }));
        sde.Set(
            "mapSolarSystems",
            false,
            System(30000001, code => code switch { "en" => "Jita", "ja" => "ジタ", "ko" => "지타", "zh" => "吉他", _ => "Jita" }),
            System(30000002, code => code switch { "en" => "New Eden", "de" => "Neu Eden", _ => "New Eden" }),
            System(30000003, code => code == "en" ? "Tanoo" : "Tanoo-" + code));
        var path = Path.Combine(directory, "reference.db");
        new SdeImporter(sde, new SdeImportOptions { MinimumShipTypes = 3, MinimumSolarSystems = 3 }).Import(path);
        database = new ReferenceDatabase(path);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static string System(int id, Func<string, string> name) =>
        $"{{\"solarSystemID\":{id},\"name\":{SyntheticSde.NameMap(name)}}}";

    private string Translate(string text, string source, string target, string shipLanguage = "en")
    {
        var line = TermProtector.Protect(text, source, target, database, shipLanguage);
        var result = TermProtector.Restore(line, line.Text);
        Assert.True(result.IsSuccess, result.Error?.Detail);
        return result.Text!;
    }

    [Theory]
    [InlineData("en", "Jita")]
    [InlineData("ja", "ジタ")]
    [InlineData("ko", "지타")]
    [InlineData("zh", "吉他")]
    public void SystemNamesInEveryLanguageAreProtectedAndRestoredAsWritten(string source, string name)
    {
        var line = TermProtector.Protect($"going to {name} now", source, "de", database);

        Assert.Equal("going to ⟦0⟧ now", line.Text);
        Assert.Equal([name], line.Items);
        Assert.Equal($"going to {name} now", Translate($"going to {name} now", source, "de"));
    }

    [Fact]
    public void SystemNamesAreCaseInsensitiveAndMultiWord()
    {
        var line = TermProtector.Protect("at jita and NEW EDEN.", "en", "de", database);

        Assert.Equal("at ⟦0⟧ and ⟦1⟧.", line.Text);
        Assert.Equal(["jita", "NEW EDEN"], line.Items);
    }

    [Fact]
    public void SystemNameInsideALongerWordIsNotProtected()
    {
        Assert.Equal("Jitaaa Xjita jita9 jita_", TermProtector.Protect("Jitaaa Xjita jita9 jita_", "en", "de", database).Text);
    }

    [Fact]
    public void SystemNameNextToUrlAndDigitsIsProtected()
    {
        var line = TermProtector.Protect("Jita https://a.b/c 5 Jita,7", "en", "de", database);

        Assert.Equal("⟦1⟧ ⟦0⟧ 5 ⟦2⟧,7", line.Text);
        Assert.Equal(["https://a.b/c", "Jita", "Jita"], line.Items);
        Assert.Equal(["5", "7"], line.DigitSequences);
    }

    [Fact]
    public void ShipNameIsReplacedByTheTargetClientLanguageName()
    {
        var line = TermProtector.Protect("my rifter is fast", "en", "fr", database);

        Assert.Equal("my ⟦0⟧ is fast", line.Text);
        Assert.Equal(["Rifteur"], line.Items);
        Assert.Equal("my Rifteur is fast", Translate("my rifter is fast", "en", "fr"));
    }

    [Fact]
    public void ShipNameForANonClientTargetUsesTheShipNameLanguage()
    {
        Assert.Equal("my Rifter is fast", Translate("my Rifteur is fast", "fr", "pt"));
        Assert.Equal("my Rifter-ru is fast", Translate("my Rifteur is fast", "fr", "pt", "ru"));
    }

    [Fact]
    public void LongestShipNameMatchesFirst()
    {
        var line = TermProtector.Protect("a Rifter Navy Issue and a Rifter", "en", "fr", database);

        Assert.Equal("a ⟦0⟧ and a ⟦1⟧", line.Text);
        Assert.Equal(["Rifteur Marine", "Rifteur"], line.Items);
    }

    [Fact]
    public void ShipNameInsideALongerWordIsNotProtected()
    {
        Assert.Equal("Rifters Xrifter Hawks", TermProtector.Protect("Rifters Xrifter Hawks", "en", "fr", database).Text);
    }

    [Fact]
    public void ShipNamesAreMatchedInTheSourceLanguageOnly()
    {
        Assert.Equal("Rifteur Hawk-fr", Translate("Rifteur Hawk", "en", "fr"));
        Assert.Equal("Hawk Rifter", Translate("Hawk Rifter", "en", "en"));
    }

    [Fact]
    public void JapaneseShipNameGluedToCjkTextIsMatchedButNotGluedToLatin()
    {
        Assert.Equal("Rifterで攻撃", Translate("リフターで攻撃", "ja", "en"));
        Assert.Equal("ホーク3隻", Translate("ホーク3隻", "ja", "ja"));
        Assert.Equal("ホーク3隻", TermProtector.Protect("ホーク3隻", "ja", "en", database).Text);
        Assert.Equal("⟦0⟧ pls", TermProtector.Protect("ホーク pls", "ja", "en", database).Text);
    }

    [Fact]
    public void ChineseShipNameNextToHanTextIsMatched()
    {
        Assert.Equal("我的Rifter很强", Translate("我的裂谷级很强", "zh", "en"));
    }

    [Fact]
    public void SpacedLanguageShipNameStillNeedsWholeWordBoundaries()
    {
        Assert.Equal("Rifteurs", TermProtector.Protect("Rifteurs", "fr", "en", database).Text);
    }

    [Fact]
    public void ShipAndSystemInOneLineShareOneItemList()
    {
        var line = TermProtector.Protect("Rifter in Jita https://a.b 3", "en", "de", database);

        Assert.Equal("⟦1⟧ in ⟦2⟧ ⟦0⟧ 3", line.Text);
        Assert.Equal(["https://a.b", "Rifter", "Jita"], line.Items);
    }

    [Fact]
    public void DamagedNameTokenFollowsTheRepairAndBadResponseRules()
    {
        var line = TermProtector.Protect("Rifter in Jita", "en", "fr", database);

        Assert.Equal("Rifteur in Jita", TermProtector.Restore(line, "[[0]] in ⟦ 1 ⟧").Text ?? string.Empty);
        Assert.False(TermProtector.Restore(line, "⟦0⟧ in Jita").IsSuccess);
    }

    [Fact]
    public void NoReferenceDatabaseLeavesNamesUntouched()
    {
        Assert.Equal("Rifter in Jita", TermProtector.Protect("Rifter in Jita", "en", "fr").Text);
    }

    [Fact]
    public void MissingDatabaseFileLeavesNamesUntouched()
    {
        var missing = new ReferenceDatabase(Path.Combine(directory, "none.db"));

        Assert.Equal("Rifter in Jita", TermProtector.Protect("Rifter in Jita", "en", "fr", missing).Text);
    }

    [Theory]
    [InlineData("ja", "ジタで戦う", "⟦0⟧で戦う", "ジタ")]
    [InlineData("ja", "私のジタ", "私の⟦0⟧", "ジタ")]
    [InlineData("ja", "ジタ、逃げろ", "⟦0⟧、逃げろ", "ジタ")]
    [InlineData("zh", "我在吉他很强", "我在⟦0⟧很强", "吉他")]
    [InlineData("zh", "吉他，攻击", "⟦0⟧，攻击", "吉他")]
    [InlineData("ko", "我们去지타到", "我们去⟦0⟧到", "지타")]
    [InlineData("ko", "지타 도착", "⟦0⟧ 도착", "지타")]
    [InlineData("en", "Jita到了", "⟦0⟧到了", "Jita")]
    public void SystemNameGluedToUnspacedScriptTextIsProtectedAndRestoredAsWritten(string source, string text, string protectedText, string name)
    {
        var line = TermProtector.Protect(text, source, "de", database);

        Assert.Equal(protectedText, line.Text);
        Assert.Equal([name], line.Items);
        Assert.Equal(text, Translate(text, source, "de"));
    }

    [Theory]
    [InlineData("ja", "ジタpls")]
    [InlineData("ja", "XジタX")]
    [InlineData("ja", "ジタ3隻")]
    [InlineData("zh", "吉他_")]
    [InlineData("ko", "지타에서")]
    [InlineData("ko", "내지타")]
    [InlineData("en", "到Jitaaa")]
    public void SystemNameGluedToSpacedScriptLetterOrDigitIsNotProtected(string source, string text)
    {
        Assert.Equal(text, TermProtector.Protect(text, source, "de", database).Text);
    }

    [Fact]
    public void MultiWordSystemNameInsideALongerNameIsNotProtected()
    {
        Assert.Equal("NewEden Neu Edenx", TermProtector.Protect("NewEden Neu Edenx", "de", "en", database).Text);
    }

    private static readonly Dictionary<string, string[]> MentionTemplates = new()
    {
        ["ja"] = ["{0}で攻撃", "{0}が来た", "私の{0}", "{0}、逃げろ", "{0} 攻撃", "{0}。", "敵の{0}を撃て", "{0}3隻", "{0}pls"],
        ["zh"] = ["{0}在这里", "我的{0}", "{0}，攻击", "{0}很强", "{0} 攻击", "{0}。", "敌人的{0}来了", "{0}3艘", "{0}pls"],
        ["ko"] = ["{0}에서 만나자", "{0}로 가자", "내 {0}", "{0}, 가자", "{0} 도착", "{0}。", "{0}3척", "{0}pls"],
    };

    private const string AnyLetterOrDigit = @"[\p{L}\p{M}\p{Nd}_]";

    [Fact]
    public async Task Measure_LiveSdeSystemNames_GluedMentionsAndFalseMatches()
    {
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var livePath = Path.Combine(directory, "live-systems.db");
        new SdeImporter(new DirectorySdeSource(cache!)).Import(livePath, TestContext.Current.CancellationToken);
        var live = new ReferenceDatabase(livePath);
        var systemSet = live.ClientLanguages().SelectMany(live.SystemNames).Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wholeWord = new Regex("(?<!" + AnyLetterOrDigit + ")(?:" + string.Join("|", systemSet.OrderByDescending(name => name.Length).Select(Regex.Escape)) + ")(?!" + AnyLetterOrDigit + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var shipIds = live.ShipNames("en").Select(entry => entry.TypeId).ToHashSet();
        var itemNames = ReadPublishedNonShipNames(cache!, shipIds);
        Report($"SYS source: SDE build {live.SdeVersion}; {systemSet.Count} distinct system names across client languages");

        foreach (var (language, templates) in MentionTemplates)
        {
            var names = live.SystemNames(language).Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var mentions = names.SelectMany(name => templates.Select(template => (Name: name, Text: string.Format(template, name)))).ToList();
            var baits = live.ShipNames(language).Select(entry => entry.Name)
                .Concat(itemNames.Where(map => map.ContainsKey(language)).Select(map => map[language]))
                .Where(text => text.Length > 0 && !systemSet.Contains(text.Trim()))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var shippedMisses = mentions.Count(mention => !TermProtector.Protect(mention.Text, language, "de", live).Items.Contains(mention.Name, StringComparer.OrdinalIgnoreCase));
            var wholeWordMisses = mentions.Count(mention => !wholeWord.Matches(mention.Text).Any(match => string.Equals(match.Value, mention.Name, StringComparison.OrdinalIgnoreCase)));
            var shippedFalse = baits.Count(bait => TermProtector.Protect(bait, language, "de", live).Items.Any(systemSet.Contains));
            var wholeWordFalse = baits.Count(bait => wholeWord.IsMatch(bait));
            Report($"SYS {language}: {names.Count} system names; {mentions.Count} glued mention lines ({templates.Length} templates); {baits.Count} bait names (ship and published non-ship item names)");
            Report($"SYS {language} option-C: misses {shippedMisses}/{mentions.Count}; false matches {shippedFalse}/{baits.Count}");
            Report($"SYS {language} whole-word: misses {wholeWordMisses}/{mentions.Count}; false matches {wholeWordFalse}/{baits.Count}");
            Assert.True(mentions.Count > 0 && baits.Count > 0);
        }
    }

    private static void Report(string line)
    {
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("SAYWHAT_SYS_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static List<Dictionary<string, string>> ReadPublishedNonShipNames(string cache, HashSet<int> shipTypeIds)
    {
        var result = new List<Dictionary<string, string>>();
        using var file = File.OpenRead(Path.Combine(cache, "types.jsonl"));
        var gzipped = file.ReadByte() == 0x1F && file.ReadByte() == 0x8B;
        file.Position = 0;
        using Stream content = gzipped ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(content);
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

    [Fact]
    public async Task LiveDatabase_ProtectsRealSystemAndShipNames()
    {
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var livePath = Path.Combine(directory, "live.db");
        new SdeImporter(new DirectorySdeSource(cache!)).Import(livePath, TestContext.Current.CancellationToken);
        var line = TermProtector.Protect("Rifter in Jita and Amarr", "en", "de", new ReferenceDatabase(livePath));

        Assert.DoesNotContain("Jita", line.Text);
        Assert.DoesNotContain("Rifter", line.Text);
        Assert.DoesNotContain("Amarr", line.Text);
    }
}
