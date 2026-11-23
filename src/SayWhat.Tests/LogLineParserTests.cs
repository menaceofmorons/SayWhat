using SayWhat.Core.LogIngest;

namespace SayWhat.Tests;

public class LogLineParserTests
{
    private const string Rule = "---------------------------------------------------------------";

    private static readonly string[] EnglishHeader =
    [
        Rule,
        "  Channel ID:      local_123",
        "  Channel Name:    Local",
        "  Listener:        Pilot One",
        "  Session started: 2026.11.23 10:00:00",
        Rule,
    ];

    private static readonly string[] ForeignHeader =
    [
        Rule,
        "  Kanal-ID:        local_9",
        "  Kanalname:       Lokal",
        "  Zuhörer:         Pilot One",
        "  Sitzung gestartet: 2026.11.23 10:00:00",
        Rule,
    ];

    private static List<DecodedLine> Decode(IEnumerable<string> lines) =>
        lines.Select((t, i) => new DecodedLine(t, i * 100L)).ToList();

    private static ParseResult Run(IEnumerable<string> lines, LogLineParser? parser = null) =>
        (parser ?? new LogLineParser()).Parse(Decode(lines));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Header_IsReadByPosition(bool english)
    {
        var parser = new LogLineParser();

        parser.Parse(Decode(english ? EnglishHeader : ForeignHeader));

        Assert.NotNull(parser.Header);
        Assert.Equal(english ? "local_123" : "local_9", parser.Header.ChannelId);
        Assert.Equal(english ? "Local" : "Lokal", parser.Header.ChannelName);
        Assert.Equal("Pilot One", parser.Header.Listener);
        Assert.Equal("2026.11.23 10:00:00", parser.Header.SessionStartText);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), parser.Header.SessionStartUtc);
    }

    [Fact]
    public void HeaderOnlyLog_YieldsNoLinesAndNoWarning()
    {
        var result = Run(["﻿" + EnglishHeader[0], .. EnglishHeader[1..]]);

        Assert.Empty(result.Lines);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("[ 2026.11.23 10:00:01 ] Other Pilot > hello")]
    [InlineData("[2026.11.23 10:00:01] Other Pilot > hello")]
    [InlineData("[  2026.11.23   10:00:01  ]   Other Pilot   >   hello")]
    [InlineData("﻿[ 2026.11.23 10:00:01 ] Other Pilot > hello")]
    public void SpacingVariants_ParseToSameFields(string raw)
    {
        var line = Assert.Single(Run([.. EnglishHeader, raw]).Lines);

        Assert.Equal("Other Pilot", line.Speaker);
        Assert.Equal(LineKind.OtherPilot, line.Kind);
        Assert.Equal("hello", line.Text.Trim());
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 1, TimeSpan.Zero), line.TimestampUtc);
    }

    [Fact]
    public void TimestampText_IsPreservedAsWritten()
    {
        var line = Assert.Single(Run([.. EnglishHeader, "[ 2026.11.23 10:00:01 ] A > b"]).Lines);

        Assert.Equal("2026.11.23 10:00:01", line.TimestampText);
    }

    [Fact]
    public void Classification_UsesListenerSystemSendersAndOthers()
    {
        var parser = new LogLineParser(["EVE System", "EVE-System"]);
        var result = Run(
            [
                .. EnglishHeader,
                "[ 2026.11.23 10:00:01 ] Pilot One > mine",
                "[ 2026.11.23 10:00:02 ] EVE System > Channel MOTD",
                "[ 2026.11.23 10:00:03 ] EVE-System > Kanalwechsel",
                "[ 2026.11.23 10:00:04 ] Someone Else > hi",
            ],
            parser);

        Assert.Equal(
            [LineKind.OwnLine, LineKind.SystemMessage, LineKind.SystemMessage, LineKind.OtherPilot],
            result.Lines.Select(l => l.Kind));
    }

    [Fact]
    public void UnlistedSystemSender_IsOtherPilot()
    {
        var line = Assert.Single(Run([.. EnglishHeader, "[ 2026.11.23 10:00:01 ] EVE-System > x"]).Lines);

        Assert.Equal(LineKind.OtherPilot, line.Kind);
    }

    [Fact]
    public void SystemChange_IsEmittedForKnownSolarSystemOnly()
    {
        var parser = new LogLineParser(null, new HashSet<string> { "Jita", "Amarr" });
        var result = Run(
            [
                .. EnglishHeader,
                "[ 2026.11.23 10:00:01 ] EVE System > Channel changed to Local : Jita",
                "[ 2026.11.23 10:00:02 ] EVE System > Channel changed to Local : Nowhere",
                "[ 2026.11.23 10:00:03 ] Other > Local : Amarr",
                "[ 2026.11.23 10:00:04 ] EVE System > Jita is busy",
            ],
            parser);

        var change = Assert.Single(result.SystemChanges);
        Assert.Equal("Jita", change.SolarSystem);
        Assert.Equal(result.Lines[0].LineId, change.LineId);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 1, TimeSpan.Zero), change.TimestampUtc);
    }

    [Fact]
    public void LineWithoutLeadingTimestamp_PassesThroughWithWarning()
    {
        var result = Run([.. EnglishHeader, "continuation text", "[ not a stamp ] A > b", "[ 2026.11.23 10:00:01 ] no marker"]);

        Assert.All(result.Lines, l => Assert.Equal(LineKind.Unparseable, l.Kind));
        Assert.Equal(["continuation text", "[ not a stamp ] A > b", "[ 2026.11.23 10:00:01 ] no marker"], result.Lines.Select(l => l.Text));
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal(ParseWarningKind.LineUnparseable, w.Kind));
        Assert.Equal(result.Lines.Select(l => (LineId?)l.LineId), result.Warnings.Select(w => w.LineId));
    }

    [Theory]
    [InlineData("no header at all")]
    [InlineData("---------------")]
    public void UnreadableHeader_WarnsOnceAndLinesPassThrough(string firstLine)
    {
        var lines = firstLine.StartsWith('-')
            ? new[] { firstLine, "  only: one", firstLine }
            : [firstLine];

        var result = Run([.. lines, "[ 2026.11.23 10:00:01 ] A > b"]);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ParseWarningKind.HeaderUnreadable, warning.Kind);
        Assert.Null(warning.LineId);
        Assert.Equal("[ 2026.11.23 10:00:01 ] A > b", Assert.Single(result.Lines, l => l.Text.StartsWith('[')).Text);
    }

    [Fact]
    public void NonAsciiSpeakerAndText_AreKept()
    {
        var parser = new LogLineParser();
        var line = Assert.Single(Run([.. ForeignHeader, "[ 2026.11.23 10:00:01 ] Jörg Ünal > Привет 你好"], parser).Lines);

        Assert.Equal("Jörg Ünal", line.Speaker);
        Assert.Equal("Привет 你好", line.Text);
    }

    [Fact]
    public void LineIds_AreUniqueAndIncreasingAcrossCalls()
    {
        var parser = new LogLineParser();
        parser.Parse(Decode(EnglishHeader));
        var first = parser.Parse([new DecodedLine("[ 2026.11.23 10:00:01 ] A > 1", 500), new DecodedLine("junk", 540)]);
        var second = parser.Parse([new DecodedLine("[ 2026.11.23 10:00:02 ] A > 2", 600)]);

        var ids = first.Lines.Concat(second.Lines).Select(l => l.LineId).ToList();

        Assert.Equal([1, 2, 3], ids.Select(i => i.Sequence));
        Assert.Equal([500L, 540L, 600L], ids.Select(i => i.ByteOffset));
    }

    [Fact]
    public void ParsedLine_CarriesExactlyTheContractFields()
    {
        var names = typeof(ParsedLine).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(["Kind", "LineId", "Speaker", "Text", "TimestampText", "TimestampUtc"], names);
    }
}
