using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SayWhat.Core.TermProtection;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public class PlaceholderSurvivalTests
{
    private const string RunVariable = "SAYWHAT_RUN_SURVIVAL";
    private const string OutputVariable = "SAYWHAT_MEASUREMENT_OUTPUT";

    private static readonly TokenForm[] Forms =
    [
        new("canonical", i => $"⟦{i}⟧", "⟦"),
        new("double-square", i => $"[[{i}]]", "[["),
        new("angle", i => $"<{i}>", "<"),
        new("at-sign", i => $"@{i}@", "@"),
    ];

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["en"] =
        [
            "{0} is full of traders.",
            "Check this link: {0}",
            "Meet me in {0} with the {1}.",
            "I lost 12 ships in {0} and 3 more near {1}.",
            "Yesterday at 21:30 we docked in {0}, then {1} jumped to {2} before anyone could warn the fleet about the gate camp.",
            "Please read {0} and {1}; the {2} will be at {3} in 45 minutes.",
        ],
        ["de"] =
        [
            "{0} ist voller Händler.",
            "Schau dir diesen Link an: {0}",
            "Triff mich in {0} mit der {1}.",
            "Ich habe 12 Schiffe in {0} und 3 weitere bei {1} verloren.",
            "Gestern um 21:30 haben wir in {0} angedockt, dann sprang {1} nach {2}, bevor jemand die Flotte vor dem Gate-Camp warnen konnte.",
            "Bitte lies {0} und {1}; die {2} wird in 45 Minuten bei {3} sein.",
        ],
        ["fr"] =
        [
            "{0} est plein de marchands.",
            "Regarde ce lien : {0}",
            "Retrouve-moi à {0} avec le {1}.",
            "J'ai perdu 12 vaisseaux à {0} et 3 de plus près de {1}.",
            "Hier à 21:30 nous avons accosté à {0}, puis {1} a sauté vers {2} avant que personne puisse prévenir la flotte du camp de porte.",
            "Veuillez lire {0} et {1} ; le {2} sera à {3} dans 45 minutes.",
        ],
        ["es"] =
        [
            "{0} está lleno de comerciantes.",
            "Mira este enlace: {0}",
            "Nos vemos en {0} con el {1}.",
            "Perdí 12 naves en {0} y 3 más cerca de {1}.",
            "Ayer a las 21:30 atracamos en {0}, luego {1} saltó a {2} antes de que nadie pudiera avisar a la flota del campamento de la puerta.",
            "Por favor lee {0} y {1}; el {2} estará en {3} en 45 minutos.",
        ],
        ["ru"] =
        [
            "{0} полон торговцев.",
            "Посмотри эту ссылку: {0}",
            "Встретимся в {0} на {1}.",
            "Я потерял 12 кораблей в {0} и ещё 3 возле {1}.",
            "Вчера в 21:30 мы пристыковались в {0}, затем {1} прыгнул в {2}, прежде чем кто-либо успел предупредить флот о засаде у врат.",
            "Пожалуйста, прочитай {0} и {1}; {2} будет в {3} через 45 минут.",
        ],
    };

    private static readonly Route[] Routes =
    [
        new("de", ["de-en"]),
        new("en", ["en-de"]),
        new("fr", ["fr-en"]),
        new("en", ["en-fr"]),
        new("es", ["es-en"]),
        new("en", ["en-es"]),
        new("ru", ["ru-en"]),
        new("en", ["en-ru"]),
        new("de", ["de-en", "en-fr"]),
        new("ru", ["ru-en", "en-es"]),
    ];

    [Fact]
    public async Task Placeholder_survival_through_Lite()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(RunVariable) == "1", "survival run not requested");
        var pairs = Routes.SelectMany(route => route.Models).Distinct().ToArray();
        var translators = new Dictionary<string, ILitePairTranslator>();
        var revisions = new Dictionary<string, string>();
        using var http = new HttpClient();
        try
        {
            foreach (var pair in pairs)
            {
                var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
                Assert.SkipUnless(files is not null, $"model cache lacks {pair}");
                translators[pair] = LitePairTranslator.Load(Path.GetDirectoryName(files![LiteEngine.SourceTokeniserFile])!);
                revisions[pair] = await RevisionAsync(http, pair);
            }

            var outcomes = new List<LineOutcome>();
            foreach (var route in Routes)
            {
                foreach (var form in Forms)
                {
                    foreach (var template in Lines[route.Source])
                    {
                        outcomes.Add(Measure(route, form, template, translators));
                    }
                }
            }

            Write(Report(outcomes, revisions));
        }
        finally
        {
            foreach (var translator in translators.Values)
            {
                translator.Dispose();
            }
        }
    }

    [Fact]
    public void Intact_tokens_in_order_are_intact()
    {
        var outcome = Classify(Forms[0], 2, "Hallo ⟦0⟧ und ⟦1⟧.", []);
        Assert.Equal([TokenState.Intact, TokenState.Intact], outcome.Tokens);
        Assert.False(outcome.Reordered);
    }

    [Fact]
    public void Reordered_tokens_are_flagged_but_intact()
    {
        var outcome = Classify(Forms[0], 2, "⟦1⟧ vor ⟦0⟧", []);
        Assert.Equal([TokenState.Intact, TokenState.Intact], outcome.Tokens);
        Assert.True(outcome.Reordered);
    }

    [Fact]
    public void Dropped_duplicated_and_bracket_changes_are_classified()
    {
        var outcome = Classify(Forms[0], 4, "a ⟦0⟧ ⟦0⟧ b [1] c", []);
        Assert.Equal([TokenState.Duplicated, TokenState.ChangedBrackets, TokenState.Dropped, TokenState.Dropped], outcome.Tokens);
    }

    [Fact]
    public void Translated_token_is_classified()
    {
        var outcome = Classify(Forms[0], 1, "a ⟦null⟧ b", []);
        Assert.Equal([TokenState.Translated], outcome.Tokens);
    }

    [Fact]
    public void Digit_sequences_are_compared_outside_tokens()
    {
        Assert.True(Classify(Forms[2], 1, "<0> 12 minutes", ["12"]).DigitsIntact);
        Assert.False(Classify(Forms[2], 1, "<0> 13 minutes", ["12"]).DigitsIntact);
    }

    [Fact]
    public void Canonical_damage_is_repaired_by_restore()
    {
        var line = new ProtectedLine("⟦0⟧ and ⟦1⟧", ["Jita", "Rifter"], []);
        Assert.True(TermProtector.Restore(line, "Jita ⟦0] und ⟦1⟧").IsSuccess);
        Assert.False(TermProtector.Restore(line, "Jita [0] und ⟦1⟧").IsSuccess);
        Assert.False(TermProtector.Restore(line, "⟦0⟧ nur").IsSuccess);
    }

    private static LineOutcome Measure(Route route, TokenForm form, string template, Dictionary<string, ILitePairTranslator> translators)
    {
        var tokenCount = Regex.Matches(template, @"\{\d\}").Select(match => match.Value).Distinct().Count();
        var source = Regex.Replace(template, @"\{(\d)\}", match => form.Format(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        var text = source;
        foreach (var model in route.Models)
        {
            text = translators[model].Translate(text);
        }

        var digits = Regex.Matches(Regex.Replace(template, @"\{\d\}", " "), @"\p{Nd}+").Select(match => match.Value).ToList();
        var outcome = Classify(form, tokenCount, text, digits);
        var repaired = false;
        var failed = false;
        if (form.Name == "canonical" && !outcome.AllIntact)
        {
            var line = new ProtectedLine(source, Enumerable.Range(0, tokenCount).Select(i => "item" + i).ToList(), digits);
            repaired = TermProtector.Restore(line, text).IsSuccess;
            failed = !repaired;
        }

        return outcome with { Route = route, Form = form.Name, Repaired = repaired, BadResponse = failed };
    }

    private static LineOutcome Classify(TokenForm form, int tokenCount, string output, IReadOnlyList<string> sourceDigits)
    {
        var states = new List<TokenState>();
        var positions = new List<int>();
        var stripped = output;
        for (var i = 0; i < tokenCount; i++)
        {
            var exact = form.Format(i);
            var count = Regex.Count(output, Regex.Escape(exact));
            stripped = stripped.Replace(exact, " ", StringComparison.Ordinal);
            if (count == 1)
            {
                states.Add(TokenState.Intact);
                positions.Add(output.IndexOf(exact, StringComparison.Ordinal));
            }
            else if (count > 1)
            {
                states.Add(TokenState.Duplicated);
            }
            else if (Regex.IsMatch(output, @"[^\p{L}\p{Nd}\s]{1,3}\s*" + i + @"\s*[^\p{L}\p{Nd}\s]{1,3}"))
            {
                states.Add(TokenState.ChangedBrackets);
            }
            else if (Regex.IsMatch(output, Regex.Escape(form.Open) + @"\s*\p{L}+"))
            {
                states.Add(TokenState.Translated);
            }
            else
            {
                states.Add(TokenState.Dropped);
            }
        }

        stripped = Regex.Replace(stripped, @"[^\p{L}\p{Nd}\s]{1,3}\s*\d\s*[^\p{L}\p{Nd}\s]{1,3}", " ");
        var outputDigits = Regex.Matches(stripped, @"\p{Nd}+").Select(match => match.Value).ToList();
        var reordered = positions.Count == tokenCount && !positions.SequenceEqual(positions.Order());
        return new LineOutcome(null, string.Empty, states, reordered, outputDigits.SequenceEqual(sourceDigits), false, false, output);
    }

    private static string Report(List<LineOutcome> outcomes, Dictionary<string, string> revisions)
    {
        var report = new StringBuilder();
        report.AppendLine("# Placeholder survival through Opus-MT (Lite only)").AppendLine();
        report.AppendLine("## Machine").AppendLine();
        report.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); {Environment.ProcessorCount} logical processors");
        report.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"- Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").AppendLine();
        report.AppendLine("## Model repository revisions (Xenova/opus-mt-*)").AppendLine();
        foreach (var (pair, sha) in revisions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"- {pair}: {sha}");
        }

        report.AppendLine().AppendLine($"## Line set: {Lines["en"].Length} lines per source language, {Routes.Length} routes ({string.Join(", ", Routes.Select(RouteName))})").AppendLine();
        report.AppendLine("## Token counts per form").AppendLine();
        report.AppendLine("| Form | Tokens | Intact | Duplicated | ChangedBrackets | Dropped | Translated | Lines | Lines all intact | Lines reordered | Lines digits intact | Lines repaired (canonical) | Lines bad response (canonical) |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var form in Forms)
        {
            var rows = outcomes.Where(outcome => outcome.Form == form.Name).ToList();
            var tokens = rows.SelectMany(row => row.Tokens).ToList();
            int Count(TokenState state) => tokens.Count(token => token == state);
            var canonical = form.Name == "canonical";
            var repaired = canonical ? rows.Count(row => row.Repaired).ToString(CultureInfo.InvariantCulture) : "n/a";
            var bad = canonical ? rows.Count(row => row.BadResponse).ToString(CultureInfo.InvariantCulture) : "n/a";
            report.AppendLine($"| {form.Name} | {tokens.Count} | {Count(TokenState.Intact)} | {Count(TokenState.Duplicated)} | {Count(TokenState.ChangedBrackets)} | {Count(TokenState.Dropped)} | {Count(TokenState.Translated)} | {rows.Count} | {rows.Count(row => row.AllIntact)} | {rows.Count(row => row.Reordered)} | {rows.Count(row => row.DigitsIntact)} | {repaired} | {bad} |");
        }

        report.AppendLine().AppendLine("## Per route (lines with all tokens intact / lines)").AppendLine();
        report.AppendLine("| Route | " + string.Join(" | ", Forms.Select(form => form.Name)) + " |").AppendLine("| --- | " + string.Join(" | ", Forms.Select(_ => "---")) + " |");
        foreach (var route in Routes)
        {
            var cells = Forms.Select(form =>
            {
                var rows = outcomes.Where(outcome => outcome.Route == route && outcome.Form == form.Name).ToList();
                return $"{rows.Count(row => row.AllIntact)}/{rows.Count}";
            });
            report.AppendLine($"| {RouteName(route)} | {string.Join(" | ", cells)} |");
        }

        report.AppendLine().AppendLine("## Sample outputs (first damaged line of each route and form)").AppendLine();
        foreach (var form in Forms)
        {
            foreach (var route in Routes)
            {
                var sample = outcomes.FirstOrDefault(outcome => outcome.Route == route && outcome.Form == form.Name && !outcome.AllIntact);
                if (sample is not null)
                {
                    report.AppendLine($"- {form.Name} {RouteName(route)}: {sample.Output}");
                }
            }
        }

        return report.ToString();
    }

    private static string RouteName(Route route) => route.Source + ":" + string.Join(">", route.Models);

    private static async Task<string> RevisionAsync(HttpClient http, string pair)
    {
        try
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync($"https://huggingface.co/api/models/Xenova/opus-mt-{pair}"));
            return document.RootElement.GetProperty("sha").GetString() ?? "unknown";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)
        {
            return "unknown (" + exception.GetType().Name + ")";
        }
    }

    private static void Write(string report)
    {
        var directory = Environment.GetEnvironmentVariable(OutputVariable) ?? Path.Combine(Path.GetTempPath(), "saywhat-measurement");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "placeholder-survival.md"), report);
    }

    private enum TokenState
    {
        Intact,
        Duplicated,
        ChangedBrackets,
        Dropped,
        Translated,
    }

    private sealed record TokenForm(string Name, Func<int, string> Format, string Open);

    private sealed record Route(string Source, string[] Models);

    private sealed record LineOutcome(Route? Route, string Form, IReadOnlyList<TokenState> Tokens, bool Reordered, bool DigitsIntact, bool Repaired, bool BadResponse, string Output = "")
    {
        public bool AllIntact => Tokens.All(token => token == TokenState.Intact);
    }
}
