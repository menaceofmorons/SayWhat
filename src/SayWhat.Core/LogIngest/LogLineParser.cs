using System.Globalization;
using System.Text.RegularExpressions;

namespace SayWhat.Core.LogIngest;

public sealed partial class LogLineParser
{
    public const string SeedSystemSender = "EVE System";

    private const char ByteOrderMarkChar = '﻿';
    private const int HeaderFieldCount = 4;
    private static readonly string[] TimestampFormats = ["yyyy.MM.dd HH:mm:ss"];

    private readonly HashSet<string> systemSenders;
    private readonly IReadOnlySet<string> solarSystems;
    private readonly List<string> headerLines = [];
    private HeaderState headerState = HeaderState.Awaiting;
    private int sequence;

    public LogLineParser(IEnumerable<string>? systemSenderNames = null, IReadOnlySet<string>? knownSolarSystems = null)
    {
        systemSenders = new HashSet<string>(systemSenderNames ?? [SeedSystemSender], StringComparer.OrdinalIgnoreCase);
        solarSystems = knownSolarSystems ?? new HashSet<string>();
    }

    public LogHeader? Header { get; private set; }

    public ParseResult Parse(IEnumerable<DecodedLine> decodedLines)
    {
        var lines = new List<ParsedLine>();
        var changes = new List<SystemChange>();
        var warnings = new List<ParseWarning>();

        foreach (var decoded in decodedLines)
        {
            if (headerState == HeaderState.Awaiting)
            {
                ConsumeHeaderLine(decoded.Text, warnings);
                continue;
            }

            if (string.IsNullOrWhiteSpace(decoded.Text.Trim(ByteOrderMarkChar)))
            {
                continue;
            }

            var line = headerState == HeaderState.Failed
                ? Unparseable(decoded)
                : ParseLine(decoded);

            lines.Add(line);
            if (line.Kind == LineKind.Unparseable)
            {
                if (headerState == HeaderState.Ready)
                {
                    warnings.Add(new ParseWarning(line.LineId, ParseWarningKind.LineUnparseable));
                }
            }
            else if (line.Kind == LineKind.SystemMessage && TryFindSolarSystem(line.Text, out var system))
            {
                changes.Add(new SystemChange(line.LineId, system, line.TimestampUtc!.Value));
            }
        }

        return new ParseResult(lines, changes, warnings);
    }

    private void ConsumeHeaderLine(string raw, List<ParseWarning> warnings)
    {
        var text = raw.Trim(ByteOrderMarkChar).Trim();
        if (text.Length == 0 && headerLines.Count == 0)
        {
            return;
        }

        headerLines.Add(text);
        var isSeparator = IsSeparator(text);

        if (headerLines.Count == 1 && !isSeparator)
        {
            FailHeader(warnings);
            return;
        }

        if (headerLines.Count > 1 && isSeparator)
        {
            CompleteHeader(warnings);
        }
    }

    private void CompleteHeader(List<ParseWarning> warnings)
    {
        var values = headerLines
            .Skip(1)
            .Where(l => !IsSeparator(l) && l.Length > 0)
            .Select(FieldValue)
            .ToList();

        if (values.Count < HeaderFieldCount || values.Take(HeaderFieldCount).Any(v => v is null))
        {
            FailHeader(warnings);
            return;
        }

        var startText = values[3]!;
        Header = new LogHeader(values[0]!, values[1]!, values[2]!, startText, ParseTimestamp(startText));
        headerState = HeaderState.Ready;
    }

    private void FailHeader(List<ParseWarning> warnings)
    {
        headerState = HeaderState.Failed;
        warnings.Add(new ParseWarning(null, ParseWarningKind.HeaderUnreadable));
    }

    private static string? FieldValue(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? null : line[(colon + 1)..].Trim();
    }

    private static bool IsSeparator(string text) => text.Length >= 3 && text.All(c => c == '-');

    private ParsedLine ParseLine(DecodedLine decoded)
    {
        var text = decoded.Text.TrimStart(ByteOrderMarkChar);
        var match = LinePattern().Match(text);
        if (!match.Success)
        {
            return Unparseable(decoded);
        }

        var timestampText = match.Groups["ts"].Value;
        var timestampUtc = ParseTimestamp(timestampText);
        if (timestampUtc is null)
        {
            return Unparseable(decoded);
        }

        var speaker = match.Groups["speaker"].Value;
        return new ParsedLine(
            NextId(decoded),
            timestampText,
            timestampUtc,
            speaker,
            Classify(speaker),
            match.Groups["text"].Value);
    }

    private ParsedLine Unparseable(DecodedLine decoded) =>
        new(NextId(decoded), null, null, null, LineKind.Unparseable, decoded.Text);

    private LineId NextId(DecodedLine decoded) => new(++sequence, decoded.ByteOffset);

    private LineKind Classify(string speaker)
    {
        if (string.Equals(speaker, Header!.Listener, StringComparison.OrdinalIgnoreCase))
        {
            return LineKind.OwnLine;
        }

        return systemSenders.Contains(speaker) ? LineKind.SystemMessage : LineKind.OtherPilot;
    }

    private bool TryFindSolarSystem(string text, out string system)
    {
        var trimmed = text.TrimEnd();
        foreach (var name in solarSystems)
        {
            if (name.Length == 0 || !trimmed.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = trimmed.Length - name.Length - 1;
            if (before >= 0 && char.IsLetterOrDigit(trimmed[before]))
            {
                continue;
            }

            system = name;
            return true;
        }

        system = string.Empty;
        return false;
    }

    private static DateTimeOffset? ParseTimestamp(string text)
    {
        var collapsed = WhitespaceRun().Replace(text.Trim(), " ");
        return DateTimeOffset.TryParseExact(
            collapsed,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"^\[\s*(?<ts>[^\]]*?)\s*\]\s*(?<speaker>[^>]*?)\s*>\s?(?<text>.*)$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    private enum HeaderState
    {
        Awaiting,
        Ready,
        Failed,
    }
}
