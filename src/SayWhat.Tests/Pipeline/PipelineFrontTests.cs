using SayWhat.Core.LanguageDetection;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;
using SayWhat.Core.Pipeline;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests.Pipeline;

public sealed class PipelineFrontTests : IDisposable
{
    private const string Russian = "Привет";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-front-" + Guid.NewGuid().ToString("N"));
    private readonly ReferenceDatabase database;
    private readonly SessionLineStore store = new();
    private readonly List<PendingTranslation> handedOff = [];
    private readonly StubDetector detector = new();

    public PipelineFrontTests()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "reference.db");
        new SdeImporter(SyntheticSde.Create(), new SdeImportOptions { MinimumShipTypes = 3, MinimumSolarSystems = 3 }).Import(path);
        database = new ReferenceDatabase(path);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static ParsedLine Parsed(int sequence, string text, LineKind kind = LineKind.OtherPilot) =>
        new(new LineId(sequence, sequence * 100L), "2026.01.01 12:00:00", Now, "Pilot", kind, text);

    private PipelineFront Front(bool translationEnabled = true, ReferenceDatabase? reference = null)
    {
        return new PipelineFront(store, detector, reference ?? database, "en", translationEnabled, handedOff.Add, new FixedClock());
    }

    private async Task<Line> RunOne(ParsedLine parsed, bool translationEnabled = true)
    {
        var front = Front(translationEnabled);
        front.Submit(parsed);
        await front.DisposeAsync();
        Assert.True(store.TryGet(parsed.LineId, out var line));
        return line;
    }

    [Theory]
    [InlineData(LineKind.OwnLine)]
    [InlineData(LineKind.SystemMessage)]
    [InlineData(LineKind.Unparseable)]
    public async Task NonPilotLinesPassThroughWithoutDetection(LineKind kind)
    {
        var line = await RunOne(Parsed(1, Russian, kind));

        Assert.Equal(LineState.PassThrough, line.State);
        Assert.Equal(0, detector.Calls);
        Assert.Empty(handedOff);
    }

    [Fact]
    public async Task TranslationOffPassesOtherPilotLinesThroughWithoutDetection()
    {
        var line = await RunOne(Parsed(1, Russian), translationEnabled: false);

        Assert.Equal(LineState.PassThroughTranslationOff, line.State);
        Assert.Equal(0, detector.Calls);
        Assert.Empty(handedOff);
    }

    [Fact]
    public async Task LowConfidenceLineIsLanguageUnknown()
    {
        detector.Result = new DetectionResult("ru", 0.1, TextScript.Cyrillic);

        var line = await RunOne(Parsed(1, Russian));

        Assert.Equal(LineState.LanguageUnknown, line.State);
        Assert.Equal("?", LineLayout.Marker(line.State));
        Assert.Empty(handedOff);
    }

    [Fact]
    public async Task TargetLanguageLineIsSameLanguage()
    {
        detector.Result = new DetectionResult("en", 0.95, TextScript.Latin);

        var line = await RunOne(Parsed(1, "hello there friend"));

        Assert.Equal(LineState.SameLanguage, line.State);
        Assert.Null(LineLayout.Badge(line));
        Assert.Null(LineLayout.Marker(line.State));
        Assert.Empty(handedOff);
    }

    [Fact]
    public async Task ForeignLanguageLineIsPendingWithBadgeDeadlineAndHandOff()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);

        var line = await RunOne(Parsed(1, Russian));

        Assert.Equal(LineState.Pending, line.State);
        Assert.Equal("ru", line.LanguageCode);
        Assert.Equal("RU", LineLayout.Badge(line));
        Assert.Equal("…", LineLayout.Marker(line.State));
        Assert.Equal(Now + PipelineFront.QueueWaitMax, line.Deadline);
        var pending = Assert.Single(handedOff);
        Assert.Equal(line.LineId, pending.Line.LineId);
        Assert.Equal("ru", pending.SourceLanguage);
        Assert.Equal("en", pending.TargetLanguage);
    }

    [Fact]
    public async Task HandedOffLineCarriesItsProtectedTokens()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);

        await RunOne(Parsed(1, "Привет Ship0-ru http://example.com/x"));

        var pending = Assert.Single(handedOff);
        Assert.Equal("Привет [[1]] [[0]]", pending.Protected.Text);
        Assert.Equal(2, pending.Protected.Items.Count);
        Assert.Contains("http://example.com/x", pending.Protected.Items);
    }

    [Fact]
    public async Task ShipNamesFromTheDatabaseAreKeptOutOfDetection()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);

        await RunOne(Parsed(1, "Привет Ship0-ru"));

        Assert.DoesNotContain("Ship0-ru", detector.Texts.Single());
    }

    [Fact]
    public async Task MissingReferenceDatabaseStillDetectsAndHandsOff()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);
        var front = Front(reference: new ReferenceDatabase(Path.Combine(directory, "none.db")));

        front.Submit(Parsed(1, Russian));
        await front.DisposeAsync();

        Assert.Equal(LineState.Pending, Assert.Single(store.Snapshot()).State);
        Assert.Single(handedOff);
    }

    [Fact]
    public async Task BurstKeepsArrivalOrder()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);
        var front = Front();
        LineKind[] kinds = [LineKind.OtherPilot, LineKind.OwnLine, LineKind.SystemMessage, LineKind.Unparseable];

        for (var i = 1; i <= 400; i++)
        {
            front.Submit(Parsed(i, Russian, kinds[i % kinds.Length]));
        }

        await front.DisposeAsync();

        Assert.Equal(Enumerable.Range(1, 400), store.Snapshot().Select(line => line.LineId.Sequence));
        Assert.Equal(
            store.Snapshot().Where(line => line.State == LineState.Pending).Select(line => line.LineId),
            handedOff.Select(pending => pending.Line.LineId));
    }

    [Fact]
    public async Task DetectorFaultIsReportedAndLaterLinesStillRun()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);
        detector.ThrowOnce = true;
        var faults = new List<SessionFault>();
        var front = Front();
        front.Faulted += faults.Add;

        front.Submit(Parsed(1, Russian));
        front.Submit(Parsed(2, Russian));
        await front.DisposeAsync();

        var fault = Assert.Single(faults);
        Assert.Equal(PipelineFront.Component, fault.Component);
        Assert.Equal(nameof(InvalidOperationException), fault.ExceptionType);
        var lines = store.Snapshot();
        Assert.Equal([1, 2], lines.Select(line => line.LineId.Sequence));
        Assert.Equal(LineState.LanguageUnknown, lines[0].State);
        Assert.Equal(LineState.Pending, lines[1].State);
    }

    [Fact]
    public async Task HandOffFaultFailsTheStrandedPendingLine()
    {
        detector.Result = new DetectionResult("ru", 0.9, TextScript.Cyrillic);
        var faults = new List<SessionFault>();
        var front = new PipelineFront(store, detector, database, "en", true, _ => throw new InvalidOperationException("hand-off"), new FixedClock());
        front.Faulted += faults.Add;

        front.Submit(Parsed(1, Russian));
        await front.DisposeAsync();

        Assert.Single(faults);
        Assert.Equal(LineState.Failed, Assert.Single(store.Snapshot()).State);
    }

    [Fact]
    public async Task DetectorIsDisposedAtShutdownOnly()
    {
        var front = Front();
        front.Submit(Parsed(1, "hello", LineKind.OwnLine));
        Assert.Equal(0, detector.Disposals);

        await front.DisposeAsync();
        await front.DisposeAsync();

        Assert.Equal(1, detector.Disposals);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubDetector : ILanguageDetector, IDisposable
    {
        private int calls;
        private int disposals;

        public DetectionResult Result { get; set; } = DetectionResult.Unknown();

        public bool ThrowOnce { get; set; }

        public int Calls => Volatile.Read(ref calls);

        public int Disposals => Volatile.Read(ref disposals);

        public List<string> Texts { get; } = [];

        public bool IsReady => true;

        public DetectionResult Detect(string text)
        {
            Interlocked.Increment(ref calls);
            Texts.Add(text);
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("stub");
            }

            return Result;
        }

        public void Dispose() => Interlocked.Increment(ref disposals);
    }
}
