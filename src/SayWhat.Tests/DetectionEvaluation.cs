using System.Globalization;
using System.Text;
using SayWhat.Core.LanguageDetection;

namespace SayWhat.Tests;

public sealed record EvaluationLine(string Source, string Label, string Category, string Text)
{
    public bool IsMixed => Label.StartsWith("mixed", StringComparison.Ordinal);

    public string Expected => Label.StartsWith("zh", StringComparison.Ordinal) ? "zh" : Label;
}

public sealed record EvaluationRow(
    EvaluationLine Line,
    ScriptCheckKind Script,
    DetectionOutcome Without,
    DetectionOutcome With,
    DetectionResult? Raw)
{
    public bool IsStatistical => Script is ScriptCheckKind.Latin or ScriptCheckKind.SharedScript;

    public string LengthBucket => Line.Text.Trim().Length switch
    {
        <= 8 => "1-8 chars",
        <= 24 => "9-24 chars",
        _ => "25+ chars",
    };

    public bool IsCorrect(DetectionOutcome outcome) =>
        outcome.Kind is DetectionOutcomeKind.Detected or DetectionOutcomeKind.SameLanguage
        && string.Equals(outcome.LanguageCode, Line.Expected, StringComparison.Ordinal);
}

public static class DetectionEvaluation
{
    public const string TargetLanguage = "en";

    public static readonly string[] ShipNames =
    [
        "Vargur", "Rifter", "Megathron", "Hurricane", "Raven", "Tristan", "Catalyst", "Orca", "Tholos", "Jackdaw", "Condor",
    ];

    public static IReadOnlyList<EvaluationLine> Synthetic() => Load("synthetic-lines.txt", "synthetic", hasCategory: true);

    public static IReadOnlyList<EvaluationLine> Real() => Load("real-lines.txt", "real", hasCategory: false);

    public static IReadOnlyList<EvaluationRow> Evaluate(ILanguageDetector detector, IEnumerable<EvaluationLine> lines) =>
        lines.Select(line => Evaluate(detector, line)).ToList();

    public static string Report(IReadOnlyList<EvaluationRow> synthetic, IReadOnlyList<EvaluationRow> real)
    {
        var report = new StringBuilder();
        foreach (var (name, rows) in new[] { ("synthetic", synthetic), ("real", real) })
        {
            report.AppendLine($"## {name} lines (target language {TargetLanguage}, cutoff {DetectionRunner.DefaultConfidenceCutoff.ToString(CultureInfo.InvariantCulture)})");
            Accuracy(report, rows);
        }

        ShipEffect(report, synthetic, real);
        Chinese(report, synthetic);
        Confidence(report, synthetic.Concat(real).ToList());
        return report.ToString();
    }

    private static EvaluationRow Evaluate(ILanguageDetector detector, EvaluationLine line)
    {
        var without = DetectionRunner.Run(line.Text, TargetLanguage, detector, true);
        var with = DetectionRunner.Run(line.Text, TargetLanguage, detector, true, ShipNames);
        var setAside = SetAside.Apply(line.Text, ShipNames);
        var script = ScriptCheck.Check(setAside.Text);
        var raw = !setAside.HasNoLetters && detector.IsReady ? detector.Detect(setAside.Text) : null;
        return new EvaluationRow(line, script.Kind, without, with, raw);
    }

    private static List<EvaluationLine> Load(string file, string source, bool hasCategory)
    {
        var lines = new List<EvaluationLine>();
        foreach (var row in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", file), Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(row))
            {
                continue;
            }

            var parts = row.Split('\t', hasCategory ? 3 : 2);
            lines.Add(hasCategory
                ? new EvaluationLine(source, parts[0], parts[1], parts[2])
                : new EvaluationLine(source, parts[0], "line", parts[1]));
        }

        return lines;
    }

    private static string Cell(DetectionOutcome outcome) =>
        outcome.Kind switch
        {
            DetectionOutcomeKind.Unknown => "unknown",
            DetectionOutcomeKind.SameLanguage => $"same({outcome.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})",
            DetectionOutcomeKind.Detected => $"{outcome.LanguageCode}({outcome.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})",
            _ => "not-run",
        };

    private static void Accuracy(StringBuilder report, IReadOnlyList<EvaluationRow> rows)
    {
        var mixed = rows.Where(row => row.Line.IsMixed).ToList();
        var scored = rows.Where(row => !row.Line.IsMixed).ToList();
        report.AppendLine($"Scored {scored.Count}, mixed not scored {mixed.Count}.");
        report.AppendLine();
        report.AppendLine("By label (with ship-name set-aside): n / correct / wrong / unknown");
        foreach (var group in scored.GroupBy(row => row.Line.Expected).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"- {group.Key}: {Tally(group, row => row.With)}");
        }

        report.AppendLine();
        report.AppendLine("By length (with ship-name set-aside):");
        foreach (var group in scored.GroupBy(row => row.LengthBucket).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"- {group.Key}: {Tally(group, row => row.With)}");
        }

        report.AppendLine();
        report.AppendLine("Languages with fewer than 10 scored lines: " + string.Join(", ", scored.GroupBy(row => row.Line.Expected).Where(group => group.Count() < 10).Select(group => $"{group.Key}({group.Count()})")));
        report.AppendLine("Mixed lines (not scored): " + string.Join(" | ", mixed.Select(row => row.Line.Text)));
        report.AppendLine();
        report.AppendLine("Wrong or unknown lines (with set-aside): label, text, outcome");
        foreach (var row in scored.Where(row => !row.IsCorrect(row.With)))
        {
            report.AppendLine($"- {row.Line.Expected} | {row.Line.Text} | {Cell(row.With)}");
        }

        report.AppendLine();
    }

    private static string Tally(IEnumerable<EvaluationRow> rows, Func<EvaluationRow, DetectionOutcome> pick)
    {
        var list = rows.ToList();
        var correct = list.Count(row => row.IsCorrect(pick(row)));
        var unknown = list.Count(row => pick(row).Kind == DetectionOutcomeKind.Unknown);
        return $"{list.Count} / {correct} / {list.Count - correct - unknown} / {unknown}";
    }

    private static void ShipEffect(StringBuilder report, IReadOnlyList<EvaluationRow> synthetic, IReadOnlyList<EvaluationRow> real)
    {
        report.AppendLine("## Ship-name set-aside effect (without -> with)");
        foreach (var (name, rows) in new[] { ("synthetic", synthetic), ("real", real) })
        {
            var scored = rows.Where(row => !row.Line.IsMixed).ToList();
            report.AppendLine($"- {name} all scored: without {Tally(scored, row => row.Without)}; with {Tally(scored, row => row.With)}");
            var changed = rows.Where(row => Cell(row.Without) != Cell(row.With)).ToList();
            report.AppendLine($"- {name} lines whose result changed: {changed.Count}");
            foreach (var row in changed)
            {
                report.AppendLine($"  - [{row.Line.Expected}] {row.Line.Text} : {Cell(row.Without)} -> {Cell(row.With)}");
            }
        }

        report.AppendLine();
    }

    private static void Chinese(StringBuilder report, IReadOnlyList<EvaluationRow> synthetic)
    {
        report.AppendLine("## Simplified and Traditional Chinese (flow outcome; raw fastText top label)");
        foreach (var group in synthetic.Where(row => row.Line.Label.StartsWith("zh-", StringComparison.Ordinal)).GroupBy(row => row.Line.Label))
        {
            report.AppendLine($"- {group.Key}: flow {string.Join(", ", group.Select(row => Cell(row.With)).Distinct())}; raw {string.Join(", ", group.Select(row => row.Raw is null ? "none" : $"{row.Raw.LanguageCode}({row.Raw.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})"))}");
        }

        report.AppendLine();
    }

    private static void Confidence(StringBuilder report, IReadOnlyList<EvaluationRow> all)
    {
        var statistical = all.Where(row => row.IsStatistical && !row.Line.IsMixed && row.Raw is { IsUnknown: false }).ToList();
        report.AppendLine("## Raw detector confidence on Latin and shared-script scored lines (top label vs label)");
        foreach (var (name, rows) in new[]
        {
            ("short (<=24 chars)", statistical.Where(row => row.Line.Text.Trim().Length <= 24).ToList()),
            ("long (25+ chars)", statistical.Where(row => row.Line.Text.Trim().Length > 24).ToList()),
        })
        {
            var right = rows.Where(row => row.Raw!.LanguageCode == row.Line.Expected).Select(row => row.Raw!.Confidence).ToList();
            var wrong = rows.Where(row => row.Raw!.LanguageCode != row.Line.Expected).Select(row => row.Raw!.Confidence).ToList();
            report.AppendLine($"- {name}: correct n={right.Count} {Range(right)}; incorrect n={wrong.Count} {Range(wrong)}");
        }

        report.AppendLine();
        report.AppendLine("Cutoff sweep on short statistical lines: cutoff -> correct kept / wrong kept / unknown");
        var shortRows = statistical.Where(row => row.Line.Text.Trim().Length <= 24).ToList();
        foreach (var cutoff in new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
        {
            var kept = shortRows.Where(row => row.Raw!.Confidence >= cutoff).ToList();
            var correct = kept.Count(row => row.Raw!.LanguageCode == row.Line.Expected);
            report.AppendLine($"- {cutoff.ToString("0.0", CultureInfo.InvariantCulture)}: {correct} / {kept.Count - correct} / {shortRows.Count - kept.Count}");
        }
    }

    private static string Range(List<double> values) =>
        values.Count == 0
            ? "(none)"
            : $"min {values.Min().ToString("0.00", CultureInfo.InvariantCulture)} mean {values.Average().ToString("0.00", CultureInfo.InvariantCulture)} max {values.Max().ToString("0.00", CultureInfo.InvariantCulture)}";
}
