using SayWhat.Core.Engines;
using SayWhat.Core.ReferenceData;
using SayWhat.Core.TermProtection;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public sealed class TermProtectionIntegrationTests : IDisposable
{
    private const string RunVariable = "SAYWHAT_RUN_INTEGRATION";
    private const string Url = "https://example.com/fleet?id=7";
    private const string Link = "<url=showinfo:587//90000001>Rifter</url>";

    private static readonly string[] EnglishLines =
    [
        "{0} is full of traders.",
        "Check this link: {3}",
        "Meet me in {0} with the {1}.",
        "I lost 12 ships in {0} and 3 more near {2}, see {3}.",
        "Please read {4} and the {1} will be at {2} in 45 minutes.",
    ];

    private static readonly string[] GermanLines =
    [
        "{0} ist voller Händler.",
        "Schau dir diesen Link an: {3}",
        "Triff mich in {0} mit der {1}.",
        "Ich habe 12 Schiffe in {0} und 3 weitere bei {2} verloren, siehe {3}.",
        "Bitte lies {4}; die {1} wird in 45 Minuten bei {2} sein.",
    ];

    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-integration-" + Guid.NewGuid().ToString("N"));
    private readonly ReferenceDatabase database;

    public TermProtectionIntegrationTests()
    {
        Directory.CreateDirectory(directory);
        var sde = SyntheticSde.Create();
        sde.Set(
            "types",
            false,
            SyntheticSde.ShipLine(600, 25, code => code switch { "en" => "Rifter", "ja" => "リフター", "de" => "Rifter", "fr" => "Rifteur", _ => $"Rifter-{code}" }),
            SyntheticSde.ShipLine(602, 25, code => code switch { "en" => "Hawk", "ja" => "ホーク", "fr" => "Faucon", _ => $"Hawk-{code}" }),
            SyntheticSde.ShipLine(603, 25, code => $"Vargur-{code}"));
        sde.Set(
            "mapSolarSystems",
            false,
            System(30000001, code => code switch { "en" => "Jita", "ja" => "ジタ", _ => "Jita" }),
            System(30000002, code => code == "en" ? "Tanoo" : "Tanoo-" + code),
            System(30000003, code => code == "en" ? "Amarr" : "Amarr-" + code));
        var path = Path.Combine(directory, "reference.db");
        new SdeImporter(sde, new SdeImportOptions { MinimumShipTypes = 3, MinimumSolarSystems = 3 }).Import(path);
        database = new ReferenceDatabase(path);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static string System(int id, Func<string, string> name) =>
        $"{{\"solarSystemID\":{id},\"name\":{SyntheticSde.NameMap(name)}}}";

    [Fact]
    public void Direct_pair_protects_every_kind_and_restores_ship_names_in_the_target_language()
    {
        var text = $"Meet me in Jita with the Rifter at 21:30, see {Url} and {Link}, 12 ships.";
        var line = TermProtector.Protect(text, "en", "fr", database);

        Assert.DoesNotContain("Jita", line.Text);
        Assert.DoesNotContain("Rifter", line.Text);
        Assert.DoesNotContain("example.com", line.Text);
        Assert.DoesNotContain("showinfo", line.Text);
        Assert.Equal(["21", "30", "12"], line.DigitSequences);

        var restored = TermProtector.Restore(line, "Translated: " + line.Text);

        Assert.True(restored.IsSuccess, restored.Error?.Detail);
        Assert.Equal($"Translated: Meet me in Jita with the Rifteur at 21:30, see {Url} and {Link}, 12 ships.", restored.Text);
    }

    [Fact]
    public void Pivot_pair_restores_after_two_legs_with_names_from_the_reference_database()
    {
        var line = TermProtector.Protect("リフター ジタ ホーク 45 分", "ja", "fr", database);

        Assert.DoesNotContain("リフター", line.Text);
        Assert.DoesNotContain("ジタ", line.Text);

        var afterFirstLeg = "Rifter waits for " + line.Text;
        var afterSecondLeg = "Rifteur attend " + afterFirstLeg;
        var restored = TermProtector.Restore(line, afterSecondLeg);

        Assert.True(restored.IsSuccess, restored.Error?.Detail);
        Assert.Equal("Rifteur attend Rifter waits for Rifteur ジタ Faucon 45 分", restored.Text);
    }

    [Fact]
    public void Non_client_target_uses_the_caller_ship_name_language()
    {
        var line = TermProtector.Protect("the Hawk in Tanoo", "en", "pl", database, shipNameLanguage: "fr");
        var restored = TermProtector.Restore(line, line.Text);

        Assert.True(restored.IsSuccess, restored.Error?.Detail);
        Assert.Equal("the Faucon in Tanoo", restored.Text);
    }

    [Fact]
    public void Dropped_placeholder_is_a_bad_response_and_a_damaged_one_is_repaired()
    {
        var line = TermProtector.Protect("Jita and Amarr", "en", "de", database);

        var dropped = TermProtector.Restore(line, line.Text.Replace("[[1]]", string.Empty));
        Assert.False(dropped.IsSuccess);
        Assert.Equal(EngineErrorCategory.BadResponse, dropped.Error!.Category);

        var damaged = TermProtector.Restore(line, line.Text.Replace("[[1]]", "[1]"));
        Assert.True(damaged.IsSuccess, damaged.Error?.Detail);
        Assert.Equal("Jita and Amarr", damaged.Text);
    }

    [Fact]
    public void Digits_are_restored_in_order_when_the_count_matches_and_a_missing_one_is_a_bad_response()
    {
        var line = TermProtector.Protect("Jita in 45 minutes", "en", "de", database);

        var altered = TermProtector.Restore(line, line.Text.Replace("45", "54"));
        Assert.True(altered.IsSuccess, altered.Error?.Detail);
        Assert.Equal("Jita in 45 minutes", altered.Text);

        var dropped = TermProtector.Restore(line, line.Text.Replace("45", string.Empty));
        Assert.False(dropped.IsSuccess);
        Assert.Equal(EngineErrorCategory.BadResponse, dropped.Error!.Category);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("ru")]
    [InlineData("zh")]
    public void Identity_round_trip_is_exact_for_system_url_link_and_digits_in_every_client_language(string language)
    {
        var text = $"Jita {Url} {Link} 3 Tanoo 12:45";
        var line = TermProtector.Protect(text, language, "de", database);
        var restored = TermProtector.Restore(line, line.Text);

        Assert.True(restored.IsSuccess, restored.Error?.Detail);
        Assert.Equal(text, restored.Text);
    }

    [Fact]
    public async Task Live_sde_and_Lite_keep_names_urls_links_and_digits_for_a_direct_and_a_pivot_pair()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(RunVariable) == "1", "integration run not requested");
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var livePath = Path.Combine(directory, "live.db");
        new SdeImporter(new DirectorySdeSource(cache!)).Import(livePath, TestContext.Current.CancellationToken);
        var live = new ReferenceDatabase(livePath);

        var routes = new[]
        {
            new LiveRoute("en", "de", ["en-de"], EnglishLines),
            new LiveRoute("de", "fr", ["de-en", "en-fr"], GermanLines),
        };

        var translators = new Dictionary<string, ILitePairTranslator>();
        try
        {
            foreach (var pair in routes.SelectMany(route => route.Models).Distinct())
            {
                var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
                Assert.SkipUnless(files is not null, $"model cache lacks {pair}; set SAYWHAT_FETCH_TEST_MODELS=1 to fetch it.");
                translators[pair] = LitePairTranslator.Load(Path.GetDirectoryName(files![LiteEngine.SourceTokeniserFile])!);
            }

            foreach (var route in routes)
            {
                var restoredCount = 0;
                foreach (var template in route.Lines)
                {
                    var text = string.Format(template, "Jita", "Rifter", "Amarr", Url, Link);
                    var line = TermProtector.Protect(text, route.Source, route.Target, live);

                    Assert.NotEmpty(line.Items);
                    foreach (var item in line.Items)
                    {
                        Assert.DoesNotContain(item, line.Text);
                    }

                    var translated = route.Models.Aggregate(line.Text, (current, model) => translators[model].Translate(current));
                    var restored = TermProtector.Restore(line, translated);
                    if (!restored.IsSuccess)
                    {
                        Assert.Equal(EngineErrorCategory.BadResponse, restored.Error!.Category);
                        continue;
                    }

                    restoredCount++;
                    foreach (var item in line.Items)
                    {
                        Assert.Contains(item, restored.Text);
                    }

                    foreach (var digits in line.DigitSequences)
                    {
                        Assert.Contains(digits, restored.Text);
                    }

                    Assert.DoesNotContain("[[", restored.Text);
                }

                Assert.True(restoredCount > 0, $"no line restored for {route.Source}>{route.Target}");
            }
        }
        finally
        {
            foreach (var translator in translators.Values)
            {
                translator.Dispose();
            }
        }
    }

    private sealed record LiveRoute(string Source, string Target, string[] Models, string[] Lines);
}
