using System.IO.Compression;
using System.Text.Json;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests;

public sealed class SdeSurveyTests : IDisposable
{
    private const int ExampleCount = 12;
    private const string English = "en";

    private static readonly string[] NonCjkLanguages = ["de", "es", "fr", "ru"];

    private readonly string workFolder = Path.Combine(Path.GetTempPath(), "saywhat-sde-survey-" + Guid.NewGuid().ToString("N"));

    public SdeSurveyTests() => Directory.CreateDirectory(workFolder);

    public void Dispose()
    {
        if (Directory.Exists(workFolder))
        {
            Directory.Delete(workFolder, recursive: true);
        }
    }

    [Fact]
    public async Task Survey_LiveSde_ShipAndSystemNamesByLanguage()
    {
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var databasePath = Path.Combine(workFolder, "reference.db");
        var result = new SdeImporter(new DirectorySdeSource(cache!)).Import(databasePath, TestContext.Current.CancellationToken);
        var database = new ReferenceDatabase(databasePath);
        var languages = database.ClientLanguages();
        var others = languages.Where(language => language != English).ToArray();
        Report($"SURVEY source: SDE build {result.SdeVersion}; languages {string.Join(",", languages)}");

        var shipIds = database.ShipNames(English).Select(entry => entry.TypeId).ToHashSet();
        Report($"SURVEY ships: {shipIds.Count} types in category 6; stored ship names {result.ShipNames}");

        var shipRecords = ReadRecords(cache!, "types").Where(record => shipIds.Contains(record.GetProperty("typeID").GetInt32())).ToList();
        ReportFields("ship", shipRecords);
        var shipNames = shipRecords.ToDictionary(record => record.GetProperty("typeID").GetInt32(), NameMap);
        ReportDifferences("ship", shipNames, others);
        ReportCollisions("ship", shipNames, languages);
        ReportPublished(shipRecords, shipNames);

        var systemRecords = ReadRecords(cache!, "mapSolarSystems").ToList();
        ReportFields("system", systemRecords);
        var systemNames = systemRecords.ToDictionary(record => record.GetProperty("solarSystemID").GetInt32(), NameMap);
        ReportDifferences("system", systemNames, others);
        ReportCollisions("system", systemNames, languages);
        ReportCrossLanguageClashes(systemNames, languages);

        Assert.Equal(shipIds.Count, shipNames.Count);
        Assert.Equal(result.SolarSystems, systemNames.Count);
    }

    private static void Report(string line)
    {
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("SAYWHAT_SURVEY_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static Dictionary<string, string> NameMap(JsonElement record) =>
        record.GetProperty("name").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);

    private static bool Differs(Dictionary<string, string> map, string language) =>
        map.TryGetValue(language, out var name) && name != map[English];

    private static void ReportFields(string kind, List<JsonElement> records)
    {
        var languageFields = new SortedDictionary<string, int>();
        foreach (var record in records)
        {
            foreach (var property in record.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty(English, out _))
                {
                    languageFields[property.Name] = languageFields.GetValueOrDefault(property.Name) + 1;
                }
            }
        }

        var languageSets = records
            .Select(record => string.Join(",", record.GetProperty("name").EnumerateObject().Select(property => property.Name).Order()))
            .GroupBy(set => set)
            .Select(group => $"[{group.Key}] x{group.Count()}");
        Report($"SURVEY {kind} per-language fields (records carrying each): {string.Join("; ", languageFields.Select(pair => $"{pair.Key}={pair.Value}"))} of {records.Count}");
        Report($"SURVEY {kind} name language sets: {string.Join("; ", languageSets)}");
    }

    private static void ReportDifferences(string kind, Dictionary<int, Dictionary<string, string>> names, string[] others)
    {
        Report($"SURVEY {kind} differing from English (exact / ignoring case), of {names.Count}:");
        var differingLanguages = new List<string>();
        foreach (var language in others)
        {
            var exact = names.Values.Count(map => Differs(map, language));
            var caseless = names.Values.Count(map => map.TryGetValue(language, out var name) && !string.Equals(name, map[English], StringComparison.OrdinalIgnoreCase));
            var missing = names.Values.Count(map => !map.ContainsKey(language));
            Report($"SURVEY   {kind} {language}: differs {exact} (ignoring case {caseless}), missing {missing}");
            if (exact > 0)
            {
                differingLanguages.Add(language);
            }
        }

        var identicalEverywhere = names.Values.Count(map => others.All(language => map.TryGetValue(language, out var name) && name == map[English]));
        Report($"SURVEY   {kind} identical in every language: {identicalEverywhere}; differing in at least one: {names.Count - identicalEverywhere}; languages with any difference: {string.Join(",", differingLanguages)}");

        foreach (var (id, map) in names.Where(pair => others.Any(language => Differs(pair.Value, language))).Take(ExampleCount))
        {
            Report($"SURVEY   {kind} example {id}: {string.Join(" | ", map.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }

        var nonCjkDiffering = names.Where(pair => NonCjkLanguages.Any(language => Differs(pair.Value, language))).ToList();
        Report($"SURVEY   {kind} records where any of de/es/fr/ru differs from English: {nonCjkDiffering.Count}");
        foreach (var (id, map) in nonCjkDiffering.Take(ExampleCount))
        {
            Report($"SURVEY   {kind} non-CJK difference {id}: {string.Join(" | ", map.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }
    }

    private static void ReportCollisions(string kind, Dictionary<int, Dictionary<string, string>> names, IReadOnlyList<string> languages)
    {
        foreach (var language in languages)
        {
            var collisions = names
                .Where(pair => pair.Value.ContainsKey(language))
                .GroupBy(pair => NameNormaliser.Normalise(pair.Value[language]))
                .Count(group => group.Count() > 1);
            Report($"SURVEY   {kind} {language}: normalised names shared by more than one record {collisions}");
            foreach (var group in names.Where(pair => pair.Value.ContainsKey(language)).GroupBy(pair => NameNormaliser.Normalise(pair.Value[language])).Where(group => group.Count() > 1).Take(3))
            {
                Report($"SURVEY     {kind} {language} shared name '{group.Key}': ids {string.Join(",", group.Select(pair => pair.Key))}; English {string.Join(" / ", group.Select(pair => pair.Value[English]))}");
            }
        }
    }

    private static void ReportPublished(List<JsonElement> records, Dictionary<int, Dictionary<string, string>> names)
    {
        var published = records.Where(record => record.TryGetProperty("published", out var flag) && flag.GetBoolean()).Select(record => record.GetProperty("typeID").GetInt32()).ToHashSet();
        Report($"SURVEY ship published: {published.Count} of {names.Count}");
        foreach (var language in new[] { "de", "es", "fr", "ru", "ja", "ko", "zh" })
        {
            Report($"SURVEY   published ship {language}: differs {names.Count(pair => published.Contains(pair.Key) && Differs(pair.Value, language))}");
        }

        foreach (var (id, map) in names.Where(pair => published.Contains(pair.Key) && NonCjkLanguages.Any(language => Differs(pair.Value, language))).Take(ExampleCount))
        {
            Report($"SURVEY   published non-CJK difference {id}: {string.Join(" | ", map.Select(pair => $"{pair.Key}={pair.Value}"))}");
        }

        foreach (var (id, map) in names.Where(pair => !pair.Value.ContainsKey("ja")))
        {
            Report($"SURVEY   ship without translations {id}: {map[English]}");
        }
    }

    private static void ReportCrossLanguageClashes(Dictionary<int, Dictionary<string, string>> systems, IReadOnlyList<string> languages)
    {
        var englishIndex = systems.ToDictionary(pair => NameNormaliser.Normalise(pair.Value[English]), pair => pair.Key);
        foreach (var language in languages.Where(language => language != English))
        {
            var clashes = systems.Count(pair =>
                pair.Value.TryGetValue(language, out var name)
                && englishIndex.TryGetValue(NameNormaliser.Normalise(name), out var otherId)
                && otherId != pair.Key);
            Report($"SURVEY   system {language} name equals the English name of a different system: {clashes}");
        }
    }

    private static IEnumerable<JsonElement> ReadRecords(string cache, string table)
    {
        using var file = File.OpenRead(Path.Combine(cache, table + ".jsonl"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }
}
