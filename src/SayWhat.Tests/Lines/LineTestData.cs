using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;

namespace SayWhat.Tests.Lines;

internal static class LineTestData
{
    public static Line Make(int sequence, LineState state = LineState.Pending, string? language = "ru") =>
        new(
            new LineId(sequence, sequence * 100L),
            "2026.01.01 12:00:00",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            "Pilot",
            LineKind.OtherPilot,
            "original",
            language,
            0.9,
            state,
            null,
            new DateTimeOffset(2026, 1, 1, 12, 0, 30, TimeSpan.Zero));
}
