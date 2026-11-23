namespace SayWhat.Core.LogIngest;

public enum LineKind
{
    OtherPilot,
    OwnLine,
    SystemMessage,
    Unparseable,
}

public readonly record struct LineId(int Sequence, long ByteOffset);

public sealed record ParsedLine(
    LineId LineId,
    string? TimestampText,
    DateTimeOffset? TimestampUtc,
    string? Speaker,
    LineKind Kind,
    string Text);

public sealed record SystemChange(LineId LineId, string SolarSystem, DateTimeOffset TimestampUtc);

public enum ParseWarningKind
{
    HeaderUnreadable,
    LineUnparseable,
}

public sealed record ParseWarning(LineId? LineId, ParseWarningKind Kind);

public sealed record LogHeader(
    string ChannelId,
    string ChannelName,
    string Listener,
    string SessionStartText,
    DateTimeOffset? SessionStartUtc);

public sealed record ParseResult(
    IReadOnlyList<ParsedLine> Lines,
    IReadOnlyList<SystemChange> SystemChanges,
    IReadOnlyList<ParseWarning> Warnings);
