using System.Text;
using SayWhat.Core.Engines;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.LogIngest;
using SayWhat.Core.Models;
using SayWhat.Core.ReferenceData;
using SayWhat.Engines.Lite;
using SayWhat.Platform.Windows;
using SayWhat.Spike;
using SayWhat.Tests.LogIngest;

namespace SayWhat.Tests;

public sealed class HostRunTests : IDisposable
{
    private const string Rule = "---------------------------------------------------------------";
    private const string Russian = "Привет";
    private const string Stamp = "2026.11.24 10:00:00";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "saywhat-hostrun-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLogLockProbe probe = new();
    private readonly StringWriter output = new();
    private readonly StubDetector detector = new();
    private readonly StubEngine engine = new();

    public HostRunTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        engine.Open();
        Directory.Delete(dir, recursive: true);
    }

    public static bool LiveEnabled => Environment.GetEnvironmentVariable("SAYWHAT_RUN_INTEGRATION") == "1";

    private static byte[] Encode(string text, string encodingName) => encodingName switch
    {
        "utf16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "utf16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
    };

    private static byte[] Raw(string text, string encodingName) => encodingName switch
    {
        "utf16le" => Encoding.Unicode.GetBytes(text),
        "utf16be" => Encoding.BigEndianUnicode.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    private static string Line(string speaker, string text) => $"[ 2026.11.24 10:00:00 ] {speaker} > {text}\r\n";

    private string CreateLocal(string encodingName, string listener = "Pilot One")
    {
        var header = string.Join("\r\n", Rule, "  Channel ID:      local", "  Channel Name:    Local", $"  Listener:        {listener}", "  Session started: 2026.11.24 09:00:00", Rule) + "\r\n";
        var path = Path.Combine(dir, "Local_20261124_090000_1.txt");
        File.WriteAllBytes(path, Encode(header + Line("Other", "backlog line"), encodingName));
        probe.Set(path, LogLockState.Held);
        return path;
    }

    private static void Append(string path, string encodingName, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Raw(text, encodingName));
    }

    private LogWatcher Watcher() =>
        new(new FakeEveLogFolder(dir), probe, new LogWatcherOptions("SayWhat_", Fast, false));

    private Task<int> Start(HostRunOptions? options = null, TimeSpan? waitMax = null, CancellationToken stop = default)
    {
        var run = new HostRun(Watcher(), detector, null, engine, output, waitMax);
        return Task.Run(() => run.RunAsync(options ?? Options(), stop));
    }

    private static HostRunOptions Options(bool translation = true) =>
        new(null, HostRunOptions.DefaultChannel, "en", translation);

    private string[] PrintedLines() =>
        output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith("[host]", StringComparison.Ordinal)).ToArray();

    private void WaitForPrinted(int count)
    {
        SpinWait.SpinUntil(() => PrintedLines().Length >= count, Wait);
        Assert.True(PrintedLines().Length >= count, "printed lines: " + string.Join(" | ", PrintedLines()));
    }

    private void WaitForHost(string text)
    {
        SpinWait.SpinUntil(() => output.ToString().Contains(text, StringComparison.Ordinal), Wait);
        Assert.Contains(text, output.ToString());
    }

    private async Task<int> EndSession(string path, Task<int> run)
    {
        probe.Set(path, LogLockState.NotHeld);
        return await run.WaitAsync(Wait);
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8bom")]
    public async Task EveryStatePrintsInArrivalOrderInTheLayout(string encodingName)
    {
        var path = CreateLocal(encodingName);
        var run = Start();
        WaitForHost("following");

        Append(path, encodingName, Line("Other", Russian) + Line("Pilot One", "my own line") + Line("EVE System", "Channel changed") + Line("Other", "zzz") + Line("Other", "good morning"));
        WaitForPrinted(5);
        var code = await EndSession(path, run);

        Assert.Equal(0, code);
        Assert.Equal(
            [
                $"{Stamp} Other RU T:{Russian}",
                $"{Stamp} Pilot One my own line",
                $"{Stamp} EVE System Channel changed",
                $"{Stamp} Other ? zzz",
                $"{Stamp} Other good morning",
            ],
            PrintedLines());
    }

    [Fact]
    public async Task FailedLineShowsBadgeMarkerAndOriginalText()
    {
        engine.FailWith = EngineErrorCategory.Unavailable;
        var path = CreateLocal("utf16le");
        var run = Start();
        WaitForHost("following");

        Append(path, "utf16le", Line("Other", Russian));
        WaitForPrinted(1);
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other RU ✕ {Russian}"], PrintedLines());
    }

    [Fact]
    public async Task SlowEarlierLineHoldsLaterOnesUntilFinal()
    {
        engine.Hold();
        var path = CreateLocal("utf8bom");
        var run = Start();
        WaitForHost("following");

        Append(path, "utf8bom", Line("Other", Russian) + Line("Pilot One", "later line"));
        await Task.Delay(600);
        Assert.Empty(PrintedLines());

        engine.Open();
        WaitForPrinted(2);
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other RU T:{Russian}", $"{Stamp} Pilot One later line"], PrintedLines());
    }

    [Fact]
    public async Task HeldLinePrintsPendingAfterTheWaitLimit()
    {
        engine.Hold();
        var path = CreateLocal("utf8bom");
        var run = Start(waitMax: TimeSpan.FromMilliseconds(300));
        WaitForHost("following");

        Append(path, "utf8bom", Line("Other", Russian) + Line("Pilot One", "later line"));
        WaitForPrinted(2);
        engine.Open();
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other RU … {Russian}", $"{Stamp} Pilot One later line"], PrintedLines());
    }

    [Fact]
    public async Task SessionEndStopsTheRunAndReleasesTheEngineAndDetector()
    {
        var path = CreateLocal("utf16le");
        var run = Start();
        WaitForHost("following");

        var code = await EndSession(path, run);

        Assert.Equal(0, code);
        Assert.Equal(1, engine.ReleaseCalls);
        Assert.True(detector.Disposed);
    }

    [Fact]
    public async Task StopTokenEndsTheRunAndReleasesTheEngine()
    {
        CreateLocal("utf8bom");
        using var source = new CancellationTokenSource();
        var run = Start(stop: source.Token);
        WaitForHost("following");

        source.Cancel();
        var code = await run.WaitAsync(Wait);

        Assert.Equal(0, code);
        Assert.Equal(1, engine.ReleaseCalls);
        Assert.True(detector.Disposed);
    }

    [Fact]
    public async Task TranslationOffPrintsEverythingUnmarked()
    {
        var path = CreateLocal("utf16le");
        var run = Start(Options(translation: false));
        WaitForHost("following");

        Append(path, "utf16le", Line("Other", Russian) + Line("Other", "good morning"));
        WaitForPrinted(2);
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other {Russian}", $"{Stamp} Other good morning"], PrintedLines());
        Assert.Equal(0, engine.TranslateCalls);
    }

    [Fact]
    public async Task NoLivePilotStopsWithAMessage()
    {
        var code = await Start().WaitAsync(Wait);

        Assert.Equal(1, code);
        Assert.Contains("no live EVE pilot", output.ToString());
    }

    [Fact]
    public async Task UnknownPilotStopsWithAMessage()
    {
        CreateLocal("utf8bom");

        var code = await Start(new HostRunOptions("Nobody", HostRunOptions.DefaultChannel, "en", true)).WaitAsync(Wait);

        Assert.Equal(1, code);
        Assert.Contains("Nobody is not live", output.ToString());
    }

    [Fact]
    public void MissingAssetsStopTheHostNamingPrepare()
    {
        var error = new StringWriter();

        var code = RunCommand.Execute(["--models", dir], output, error, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("prepare", error.ToString());
        Assert.Contains("reference.db", error.ToString());
        Assert.Contains("lid.176.ftz", error.ToString());
    }

    [Theory]
    [InlineData("--translation", "maybe")]
    [InlineData("--bogus", "x")]
    [InlineData("--pilot")]
    public void BadArgumentsAreRejected(params string[] args)
    {
        var error = new StringWriter();

        Assert.Equal(2, RunCommand.Execute(args, output, error, CancellationToken.None));
        Assert.StartsWith("run:", error.ToString());
    }

    [Fact(Skip = "Set SAYWHAT_RUN_INTEGRATION=1 to run the host with the real assets.", SkipUnless = nameof(LiveEnabled), SkipType = typeof(HostRunTests))]
    public async Task Live_HostTranslatesAGermanLineWithTheRealAssets()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var models = Path.Combine(dir, "models");
        var prepare = new PrepareCommand(client, PrepareSources.Published, new StringWriter(), new StringWriter());
        var options = new PrepareOptions(models, "en", ["de"]);
        var code = await prepare.RunAsync(options, TestContext.Current.CancellationToken);
        if (code != 0)
        {
            code = await prepare.RunAsync(options, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, code);
        using var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
        var liveEngine = new LiteEngine(
            new ModelFolder(models, LiteEngine.RequiredFiles),
            new XenovaModelCatalogue(client),
            () => host,
            LitePairTranslator.Load,
            TimeSpan.FromSeconds(120));
        var path = CreateLocal("utf16le");
        var run = new HostRun(
            Watcher(),
            new FastTextLanguageDetector(Path.Combine(models, PrepareCommand.DetectorFile)),
            new ReferenceDatabase(Path.Combine(models, PrepareCommand.ReferenceDatabaseFile)),
            liveEngine,
            output);
        var task = Task.Run(() => run.RunAsync(Options(), CancellationToken.None));
        WaitForHost("following");

        Append(path, "utf16le", Line("Other", "Das Wetter ist heute sehr schön in Jita.") + Line("Pilot One", "o7"));
        WaitForPrinted(2);
        await EndSession(path, task);

        var printed = PrintedLines();
        Assert.Contains(" DE ", printed[0]);
        Assert.Contains("weather", printed[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jita", printed[0]);
        Assert.EndsWith("Pilot One o7", printed[1]);
    }

    private sealed class StubDetector : ILanguageDetector, IDisposable
    {
        public bool Disposed { get; private set; }

        public bool IsReady => true;

        public DetectionResult Detect(string text)
        {
            if (text.Contains("zzz", StringComparison.Ordinal))
            {
                return new DetectionResult("ru", 0.1, TextScript.Latin);
            }

            return text.Contains("good", StringComparison.Ordinal)
                ? new DetectionResult("en", 0.95, TextScript.Latin)
                : new DetectionResult("ru", 0.95, TextScript.Cyrillic);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class StubEngine : IEngine
    {
        private readonly object gate = new();
        private TaskCompletionSource? gateTask;
        private int translateCalls;
        private int releaseCalls;

        public EngineErrorCategory? FailWith { get; set; }

        public int TranslateCalls => Volatile.Read(ref translateCalls);

        public int ReleaseCalls => Volatile.Read(ref releaseCalls);

        public void Hold()
        {
            lock (gate)
            {
                gateTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Open()
        {
            lock (gate)
            {
                gateTask?.TrySetResult();
            }
        }

        public EngineDescription Describe() => new("Stub", EngineLocality.Local, "characters", false, 1);

        public Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation) =>
            Task.FromResult<PrepareResult>(new PrepareResult.Ready());

        public async Task<TranslationResult> Translate(string protectedText, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            Interlocked.Increment(ref translateCalls);
            Task? pending;
            lock (gate)
            {
                pending = gateTask?.Task;
            }

            if (pending is not null)
            {
                await pending.WaitAsync(cancellation);
            }

            return FailWith is { } category
                ? TranslationResult.Failure(category)
                : TranslationResult.Success("T:" + protectedText);
        }

        public Task Release()
        {
            Interlocked.Increment(ref releaseCalls);
            return Task.CompletedTask;
        }
    }
}
