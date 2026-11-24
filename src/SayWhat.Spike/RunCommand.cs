using SayWhat.Core.LogIngest;
using SayWhat.Core.Models;
using SayWhat.Core.ReferenceData;
using SayWhat.Engines.Lite;
using SayWhat.Platform.Windows;
using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Spike;

public static class RunCommand
{
    private const string PrepareHint = "run `prepare` first";
    private const long ModelMemoryCeilingBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan ModelIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EngineRequestTimeout = TimeSpan.FromSeconds(60);

    public static int Execute(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        if (!TryParse(args, out var folder, out var options, out var problem))
        {
            error.WriteLine($"run: {problem}");
            return 2;
        }

        var databasePath = Path.Combine(folder, PrepareCommand.ReferenceDatabaseFile);
        var detectorPath = Path.Combine(folder, PrepareCommand.DetectorFile);
        var missing = new[] { databasePath, detectorPath }.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            error.WriteLine($"run: missing {string.Join(", ", missing.Select(Path.GetFileName))} in {folder}; {PrepareHint}");
            return 1;
        }

        var detector = new FastTextLanguageDetector(detectorPath);
        if (!detector.IsReady)
        {
            detector.Dispose();
            error.WriteLine($"run: {PrepareCommand.DetectorFile} could not be loaded; {PrepareHint}");
            return 1;
        }

        using var client = new HttpClient();
        using var engineHost = new ModelHost(ModelIdleTimeout, ModelMemoryCeilingBytes, TimeProvider.System);
        var engine = new LiteEngine(
            new ModelFolder(folder, LiteEngine.RequiredFiles),
            new XenovaModelCatalogue(client),
            () => engineHost,
            LitePairTranslator.Load,
            EngineRequestTimeout);
        var watcher = new LogWatcher(new WindowsEveLogFolder(), new WindowsLogLockProbe());
        var run = new HostRun(watcher, detector, new ReferenceDatabase(databasePath), engine, output);
        return run.RunAsync(options, stop).GetAwaiter().GetResult();
    }

    private static bool TryParse(string[] args, out string folder, out HostRunOptions options, out string problem)
    {
        folder = PrepareOptions.DefaultModelFolder;
        options = null!;
        problem = string.Empty;
        string? pilot = null;
        var channel = HostRunOptions.DefaultChannel;
        var target = PrepareOptions.DefaultTarget;
        var translation = true;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                problem = $"{args[index]} needs a value";
                return false;
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--pilot":
                    pilot = value;
                    break;
                case "--channel":
                    channel = value;
                    break;
                case "--target":
                    target = value;
                    break;
                case "--models":
                    folder = value;
                    break;
                case "--translation" when value is "on" or "off":
                    translation = value == "on";
                    break;
                case "--translation":
                    problem = "--translation takes on or off";
                    return false;
                default:
                    problem = $"unknown option {args[index]}";
                    return false;
            }
        }

        options = new HostRunOptions(pilot, channel, target, translation);
        return true;
    }
}
