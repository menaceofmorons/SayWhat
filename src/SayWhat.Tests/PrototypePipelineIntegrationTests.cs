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

public sealed class PrototypePipelineIntegrationTests : IAsyncLifetime
{
    private const string Rule = "---------------------------------------------------------------";
    private const string Stamp = "2026.11.24 10:00:00";
    private const string LiveSentence = "Das Wetter ist heute sehr schön in Jita.";
    private const string RussianSentence = "Сегодня очень хорошая погода в Джите, приходите все.";
    private const string EnglishSentence = "Good morning everyone, the weather is very nice today.";
    private const string Gibberish = "zzz";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "saywhat-proto-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLogLockProbe probe = new();
    private readonly StringWriter output = new();
    private string? syntheticDatabase;

    public static bool LiveEnabled => Environment.GetEnvironmentVariable("SAYWHAT_RUN_INTEGRATION") == "1";

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(dir);
        syntheticDatabase = Path.Combine(dir, "synthetic.db");
        var sde = SyntheticSde.Create();
        sde.Set(
            "types",
            false,
            SyntheticSde.ShipLine(600, 25, code => code switch { "en" => "Rifter", "ru" => "Рифтер", _ => $"Rifter-{code}" }),
            SyntheticSde.ShipLine(601, 25, code => $"Hawk-{code}"),
            SyntheticSde.ShipLine(602, 25, code => $"Vargur-{code}"));
        sde.Set(
            "mapSolarSystems",
            false,
            SolarSystem(30000001, _ => "Jita"),
            SolarSystem(30000002, code => $"Tanoo-{code}"),
            SolarSystem(30000003, code => $"Amarr-{code}"));
        new SdeImporter(sde, new SdeImportOptions { MinimumShipTypes = 3, MinimumSolarSystems = 3 }).Import(syntheticDatabase);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EveryFinalStateIsPrintedInArrivalOrderWithRestoredTerms()
    {
        var engine = new ScriptedEngine();
        var path = CreateLocal();
        var run = Start(new ScriptedDetector(), new ReferenceDatabase(syntheticDatabase!), engine, Options());
        WaitForHost("following");

        Append(
            path,
            Line("Other", "Встретимся в Jita на Rifter") +
            Line("Pilot One", "my own line") +
            Line("EVE System", "Channel changed") +
            Line("Other", Gibberish) +
            Line("Other", EnglishSentence) +
            Line("Other", "FAIL это не получится") +
            Line("Other", "Привет еще раз"));
        WaitForPrinted(7);
        var code = await EndSession(path, run);

        Assert.Equal(0, code);
        Assert.Equal(
            [
                $"{Stamp} Other RU Meet in Jita on Rifter",
                $"{Stamp} Pilot One my own line",
                $"{Stamp} EVE System Channel changed",
                $"{Stamp} Other ? {Gibberish}",
                $"{Stamp} Other {EnglishSentence}",
                $"{Stamp} Other RU ✕ FAIL это не получится",
                $"{Stamp} Other RU Hello again",
            ],
            PrintedLines());
        Assert.Equal(1, engine.ReleaseCalls);
    }

    [Fact]
    public async Task ASlowTranslationPrintsPendingAndLaterLinesWaitForIt()
    {
        var engine = new ScriptedEngine { HoldRussian = true };
        var path = CreateLocal();
        var run = Start(new ScriptedDetector(), new ReferenceDatabase(syntheticDatabase!), engine, Options(), TimeSpan.FromMilliseconds(300));
        WaitForHost("following");

        Append(path, Line("Other", "Привет еще раз") + Line("Pilot One", "later line"));
        WaitForPrinted(2);
        engine.Open();
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other RU … Привет еще раз", $"{Stamp} Pilot One later line"], PrintedLines());
    }

    [Fact]
    public async Task TranslationOffPassesEveryLineThroughUnmarked()
    {
        var engine = new ScriptedEngine();
        var path = CreateLocal();
        var run = Start(new ScriptedDetector(), new ReferenceDatabase(syntheticDatabase!), engine, Options(translation: false));
        WaitForHost("following");

        Append(path, Line("Other", "Привет еще раз") + Line("Other", Gibberish) + Line("Pilot One", "my own line"));
        WaitForPrinted(3);
        await EndSession(path, run);

        Assert.Equal([$"{Stamp} Other Привет еще раз", $"{Stamp} Other {Gibberish}", $"{Stamp} Pilot One my own line"], PrintedLines());
        Assert.Equal(0, engine.TranslateCalls);
    }

    [Fact]
    public async Task LinesWrittenBeforeTheTailStartsAreNotPrinted()
    {
        var engine = new ScriptedEngine();
        var path = CreateLocal();
        var run = Start(new ScriptedDetector(), new ReferenceDatabase(syntheticDatabase!), engine, Options());
        WaitForHost("following");

        var code = await EndSession(path, run);

        Assert.Equal(0, code);
        Assert.Empty(PrintedLines());
    }

    [Fact(Skip = "Set SAYWHAT_RUN_INTEGRATION=1 to run the host with the real assets.", SkipUnless = nameof(LiveEnabled), SkipType = typeof(PrototypePipelineIntegrationTests))]
    public async Task Live_HostCoversEveryLineStateWithTheRealAssets()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var models = await PrepareAssets(client);

        var translated = await RunLiveSession(client, models, Options(), null, LiveSentence, Line("Pilot One", "my own line") + Line("EVE System", "Channel changed") + Line("Other", Gibberish) + Line("Other", EnglishSentence) + Line("Other", RussianSentence), 6);
        Assert.Contains(" DE ", translated[0]);
        Assert.Contains("weather", translated[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jita", translated[0]);
        Assert.EndsWith("Pilot One my own line", translated[1]);
        Assert.EndsWith("EVE System Channel changed", translated[2]);
        Assert.EndsWith($"Other ? {Gibberish}", translated[3]);
        Assert.EndsWith($"Other {EnglishSentence}", translated[4]);
        Assert.EndsWith($" Other RU ✕ {RussianSentence}", translated[5]);

        var pending = await RunLiveSession(client, models, Options(), TimeSpan.FromMilliseconds(1), LiveSentence, string.Empty, 1);
        Assert.Contains($" DE … {LiveSentence}", pending[0]);

        var off = await RunLiveSession(client, models, Options(translation: false), null, LiveSentence, string.Empty, 1);
        Assert.EndsWith($"Other {LiveSentence}", off[0]);
    }

    private static string SolarSystem(int id, Func<string, string> name) =>
        $"{{\"solarSystemID\":{id},\"name\":{SyntheticSde.NameMap(name)}}}";

    private static byte[] Encode(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    private static string Line(string speaker, string text) => $"[ 2026.11.24 10:00:00 ] {speaker} > {text}\r\n";

    private static HostRunOptions Options(bool translation = true) =>
        new(null, HostRunOptions.DefaultChannel, "en", translation);

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Encoding.Unicode.GetBytes(text));
    }

    private string CreateLocal()
    {
        var header = string.Join("\r\n", Rule, "  Channel ID:      local", "  Channel Name:    Local", "  Listener:        Pilot One", "  Session started: 2026.11.24 09:00:00", Rule) + "\r\n";
        var path = Path.Combine(dir, "Local_20261124_090000_1.txt");
        File.WriteAllBytes(path, Encode(header + Line("Other", "backlog line")));
        probe.Set(path, LogLockState.Held);
        return path;
    }

    private LogWatcher Watcher() =>
        new(new FakeEveLogFolder(dir), probe, new LogWatcherOptions("SayWhat_", Fast, false));

    private Task<int> Start(ILanguageDetector detector, ReferenceDatabase reference, IEngine engine, HostRunOptions options, TimeSpan? waitMax = null)
    {
        var run = new HostRun(Watcher(), detector, reference, engine, output, waitMax);
        return Task.Run(() => run.RunAsync(options, CancellationToken.None));
    }

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

    private static async Task<string> PrepareAssets(HttpClient client)
    {
        var models = Path.Combine(Path.GetTempPath(), "saywhat-proto-models-" + Guid.NewGuid().ToString("N"));
        var prepare = new PrepareCommand(client, PrepareSources.Published, new StringWriter(), new StringWriter());
        var options = new PrepareOptions(models, "en", ["de"]);
        var code = await prepare.RunAsync(options, TestContext.Current.CancellationToken);
        if (code != 0)
        {
            code = await prepare.RunAsync(options, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, code);
        return models;
    }

    private async Task<string[]> RunLiveSession(HttpClient client, string models, HostRunOptions options, TimeSpan? waitMax, string firstLine, string moreLines, int expected)
    {
        using var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
        var engine = new LiteEngine(
            new ModelFolder(models, LiteEngine.RequiredFiles),
            new XenovaModelCatalogue(client),
            () => host,
            LitePairTranslator.Load,
            TimeSpan.FromSeconds(120));
        var path = CreateLocal();
        var run = Start(
            new FastTextLanguageDetector(Path.Combine(models, PrepareCommand.DetectorFile)),
            new ReferenceDatabase(Path.Combine(models, PrepareCommand.ReferenceDatabaseFile)),
            engine,
            options,
            waitMax);
        WaitForHost("following");

        Append(path, Line("Other", firstLine) + moreLines);
        WaitForPrinted(expected);
        var code = await EndSession(path, run);
        Assert.Equal(0, code);
        var printed = PrintedLines();
        output.GetStringBuilder().Clear();
        probe.Set(path, LogLockState.NotHeld);
        File.Delete(path);
        return printed;
    }

    private sealed class ScriptedDetector : ILanguageDetector, IDisposable
    {
        public bool IsReady => true;

        public DetectionResult Detect(string text)
        {
            if (text.Contains(Gibberish, StringComparison.Ordinal))
            {
                return new DetectionResult("ru", 0.1, TextScript.Latin);
            }

            return text.Contains("Good morning", StringComparison.Ordinal)
                ? new DetectionResult("en", 0.95, TextScript.Latin)
                : new DetectionResult("ru", 0.95, TextScript.Cyrillic);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedEngine : IEngine
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int translateCalls;
        private int releaseCalls;

        public bool HoldRussian { get; init; }

        public int TranslateCalls => Volatile.Read(ref translateCalls);

        public int ReleaseCalls => Volatile.Read(ref releaseCalls);

        public void Open() => gate.TrySetResult();

        public EngineDescription Describe() => new("Scripted", EngineLocality.Local, "characters", false, 1);

        public Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation) =>
            Task.FromResult<PrepareResult>(new PrepareResult.Ready());

        public async Task<TranslationResult> Translate(string protectedText, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            Interlocked.Increment(ref translateCalls);
            if (HoldRussian)
            {
                await gate.Task.WaitAsync(cancellation);
            }

            if (protectedText.Contains("FAIL", StringComparison.Ordinal))
            {
                return TranslationResult.Failure(EngineErrorCategory.Unavailable);
            }

            return TranslationResult.Success(protectedText.Contains("Привет", StringComparison.Ordinal)
                ? "Hello again"
                : protectedText.Replace("Встретимся в", "Meet in", StringComparison.Ordinal).Replace(" на ", " on ", StringComparison.Ordinal));
        }

        public Task Release()
        {
            Interlocked.Increment(ref releaseCalls);
            return Task.CompletedTask;
        }
    }
}
