using SayWhat.Core.Engines;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;
using SayWhat.Core.Models;
using SayWhat.Core.Pipeline;
using SayWhat.Core.ReferenceData;
using SayWhat.Core.TermProtection;
using SayWhat.Engines.Lite;
using SayWhat.Platform.Windows;

namespace SayWhat.Tests.Pipeline;

public sealed class TranslateStageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SessionLineStore store = new();
    private readonly StubEngine engine = new();
    private readonly List<TranslateOutcome> outcomes = [];
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-translate-" + Guid.NewGuid().ToString("N"));

    public TranslateStageTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private TranslateStage Stage()
    {
        var stage = new TranslateStage(store, engine);
        stage.Completed += outcomes.Add;
        return stage;
    }

    private PendingTranslation Pending(int sequence, string text = "hallo", TimeSpan? deadline = null)
    {
        var protectedLine = TermProtector.Protect(text, "de", "en");
        var line = new Line(
            new LineId(sequence, sequence * 100L),
            "2026.01.01 12:00:00",
            Now,
            "Pilot",
            LineKind.OtherPilot,
            text,
            "de",
            0.9,
            LineState.Pending,
            null,
            DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromMinutes(5)));
        store.Add(line);
        return new PendingTranslation(line, protectedLine, "de", "en");
    }

    private Line Stored(PendingTranslation pending)
    {
        Assert.True(store.TryGet(pending.Line.LineId, out var line));
        return line;
    }

    [Fact]
    public async Task Success_RestoresAndBackFillsInPlace()
    {
        var first = Pending(1, "hallo https://example.com/a");
        var second = Pending(2, "welt");
        engine.Respond = text => TranslationResult.Success(text.Replace("hallo", "hello").Replace("welt", "world"));
        var stage = Stage();

        stage.Submit(first);
        stage.Submit(second);
        await stage.DisposeAsync();

        var lines = store.Snapshot();
        Assert.Equal([1, 2], lines.Select(line => line.LineId.Sequence).ToArray());
        Assert.Equal(LineState.Translated, lines[0].State);
        Assert.Equal("hello https://example.com/a", lines[0].TranslatedText);
        Assert.Equal("world", lines[1].TranslatedText);
        Assert.Equal(["hallo [[0]]", "welt"], engine.Requests.ToArray());
        Assert.Equal([1, 2], outcomes.Select(outcome => outcome.LineId.Sequence).ToArray());
    }

    [Fact]
    public async Task RestoreBadResponse_FailsTheLine()
    {
        var pending = Pending(1, "hallo https://example.com/a");
        engine.Respond = _ => TranslationResult.Success("hello without placeholder");
        var stage = Stage();

        stage.Submit(pending);
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(pending).State);
        Assert.Null(Stored(pending).TranslatedText);
        Assert.Equal(EngineErrorCategory.BadResponse, Assert.Single(outcomes).Error);
    }

    [Theory]
    [InlineData(EngineErrorCategory.Unavailable)]
    [InlineData(EngineErrorCategory.Quota)]
    [InlineData(EngineErrorCategory.Cap)]
    [InlineData(EngineErrorCategory.NoModel)]
    [InlineData(EngineErrorCategory.Authentication)]
    [InlineData(EngineErrorCategory.BadResponse)]
    [InlineData(EngineErrorCategory.Timeout)]
    public async Task EveryEngineErrorCategory_FailsTheLine(EngineErrorCategory category)
    {
        var pending = Pending(1);
        engine.Respond = _ => TranslationResult.Failure(category, "x");
        var stage = Stage();

        stage.Submit(pending);
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(pending).State);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(LineState.Failed, outcome.State);
        Assert.Equal(category, outcome.Error);
    }

    [Fact]
    public async Task PrepareNoModel_FailsWithNoModelAndNeverTranslates()
    {
        var pending = Pending(1);
        engine.Prepared = new PrepareResult.NoModel();
        var stage = Stage();

        stage.Submit(pending);
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(pending).State);
        Assert.Equal(EngineErrorCategory.NoModel, Assert.Single(outcomes).Error);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task DeadlinePassedWhileTranslating_FailsTheLineCancelsAndDiscardsTheLateResult()
    {
        var late = new TaskCompletionSource<TranslationResult>();
        engine.Pending = late;
        var slow = Pending(1, deadline: TimeSpan.FromMilliseconds(150));
        var next = Pending(2);
        var stage = Stage();

        stage.Submit(slow);
        stage.Submit(next);
        await WaitUntil(() => Stored(slow).State == LineState.Failed);
        engine.Pending = null;
        late.SetResult(TranslationResult.Success("late"));
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(slow).State);
        Assert.Null(Stored(slow).TranslatedText);
        Assert.True(engine.CancelledRequests >= 1);
        Assert.Equal(LineState.Translated, Stored(next).State);
        Assert.Equal(EngineErrorCategory.Timeout, outcomes[0].Error);
    }

    [Fact]
    public async Task DeadlineAlreadyPassed_FailsWithoutCallingTheEngine()
    {
        var pending = Pending(1, deadline: TimeSpan.FromSeconds(-1));
        var stage = Stage();

        stage.Submit(pending);
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(pending).State);
        Assert.Empty(engine.Requests);
        Assert.Equal(0, engine.Prepares);
    }

    [Fact]
    public async Task ResultAfterTheLineFailed_NeverChangesIt()
    {
        var pending = Pending(1);
        var gate = new TaskCompletionSource<TranslationResult>();
        engine.Pending = gate;
        var stage = Stage();

        stage.Submit(pending);
        await WaitUntil(() => engine.Requests.Count == 1);
        store.Update(pending.Line.LineId, line => line with { State = LineState.Failed });
        gate.SetResult(TranslationResult.Success("hello"));
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(pending).State);
        Assert.Null(Stored(pending).TranslatedText);
        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task ReleaseIsCalledOnceOnDispose()
    {
        var stage = Stage();

        await stage.DisposeAsync();
        await stage.DisposeAsync();

        Assert.Equal(1, engine.Releases);
    }

    [Fact]
    public async Task EngineException_RaisesFaultFailsTheLineAndKeepsGoing()
    {
        var first = Pending(1);
        var second = Pending(2);
        var calls = 0;
        engine.Respond = text => ++calls == 1 ? throw new InvalidOperationException() : TranslationResult.Success(text);
        var faults = new List<SessionFault>();
        var stage = Stage();
        stage.Faulted += faults.Add;

        stage.Submit(first);
        stage.Submit(second);
        await stage.DisposeAsync();

        Assert.Equal(LineState.Failed, Stored(first).State);
        Assert.Equal(LineState.Translated, Stored(second).State);
        var fault = Assert.Single(faults);
        Assert.Equal(TranslateStage.Component, fault.Component);
    }

    [Fact]
    public async Task Live_FrontAndTranslateStagesWithTheLiteEngineTranslateAGermanLineKeepingASystemName()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SAYWHAT_FETCH_TEST_MODELS") == "1", "live checks are opt-in");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SAYWHAT_FETCH_TEST_SDE") == "1", "live checks are opt-in");
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent");
        using var live = await LiveModelFolder.CreateAsync("saywhat-translate-live-", ["de-en"]);
        Assert.SkipUnless(live is not null, "model files not in the local test cache");

        var databasePath = Path.Combine(directory, "live.db");
        new SdeImporter(new DirectorySdeSource(cache!)).Import(databasePath, TestContext.Current.CancellationToken);
        var reference = new ReferenceDatabase(databasePath);

        var detectorPath = Path.Combine(directory, "lid.176.ftz");
        using (var client = new HttpClient())
        {
            await File.WriteAllBytesAsync(
                detectorPath,
                await client.GetByteArrayAsync("https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.ftz", TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }

        using var httpClient = new HttpClient();
        using var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
        var liteEngine = new LiteEngine(
            new ModelFolder(live.Root, LiteEngine.RequiredFiles),
            new XenovaModelCatalogue(httpClient),
            () => host,
            LitePairTranslator.Load,
            TimeSpan.FromSeconds(120));

        var liveStore = new SessionLineStore();
        var translate = new TranslateStage(liveStore, liteEngine);
        var front = new PipelineFront(
            liveStore,
            new FastTextLanguageDetector(detectorPath),
            reference,
            "en",
            true,
            pending => translate.Submit(pending));
        var parsed = new ParsedLine(
            new LineId(1, 100),
            "2026.01.01 12:00:00",
            Now,
            "Pilot",
            LineKind.OtherPilot,
            "Wir treffen uns heute Abend in Jita am Sammelpunkt und fliegen gemeinsam zum Ziel");

        front.Submit(parsed);
        await front.DisposeAsync();
        await translate.DisposeAsync();

        var line = Assert.Single(liveStore.Snapshot());
        Assert.Equal(LineState.Translated, line.State);
        Assert.Contains("Jita", line.TranslatedText);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class StubEngine : IEngine
    {
        private int releases;
        private int prepares;
        private int cancelled;

        public Func<string, TranslationResult> Respond { get; set; } = text => TranslationResult.Success(text);

        public PrepareResult Prepared { get; set; } = new PrepareResult.Ready();

        public TaskCompletionSource<TranslationResult>? Pending { get; set; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

        public int Releases => releases;

        public int Prepares => prepares;

        public int CancelledRequests => cancelled;

        public EngineDescription Describe() => throw new NotSupportedException();

        public Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            Interlocked.Increment(ref prepares);
            return Task.FromResult(Prepared);
        }

        public async Task<TranslationResult> Translate(string protectedText, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            Requests.Enqueue(protectedText);
            var gate = Pending;
            if (gate is not null)
            {
                cancellation.Register(() => Interlocked.Increment(ref cancelled));
                return await gate.Task;
            }

            return Respond(protectedText);
        }

        public Task Release()
        {
            Interlocked.Increment(ref releases);
            return Task.CompletedTask;
        }
    }
}
