using System.Globalization;
using SayWhat.Core.LogIngest;
using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Tests.LogIngest;

public static class LiveSwitch
{
    private const string SwitchVariable = "SAYWHAT_LIVE";
    private const string StepVariable = "SAYWHAT_LIVE_STEP";

    public static bool Enabled => Environment.GetEnvironmentVariable(SwitchVariable) == "1";

    public static bool RunningStep => Enabled && StepIs(null, "running");

    public static bool ClosedStep => Enabled && StepIs("closed");

    public static bool TypingStep => Enabled && StepIs("typing");

    public static bool JumpStep => Enabled && StepIs("jump");

    public static string? PilotFilter => Environment.GetEnvironmentVariable("SAYWHAT_LIVE_PILOT");

    public static int IntSetting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool StepIs(params string?[] accepted)
    {
        var step = Environment.GetEnvironmentVariable(StepVariable);
        return accepted.Contains(string.IsNullOrEmpty(step) ? null : step.ToLowerInvariant());
    }
}

public static class LiveReport
{
    private static readonly object Gate = new();

    public static string ReportPath =>
        Environment.GetEnvironmentVariable("SAYWHAT_LIVE_REPORT")
        ?? Path.Combine(Path.GetTempPath(), "saywhat-live-report.txt");

    public static string StatePath => Path.Combine(Path.GetTempPath(), "saywhat-live-state.txt");

    public static void Write(string test, string key, object? value)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} | {test} | {key} = {value}");
        lock (Gate)
        {
            File.AppendAllText(ReportPath, line + Environment.NewLine);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }
}

public static class LiveSession
{
    public static LogWatcher NewWatcher() => new(new WindowsEveLogFolder(), new WindowsLogLockProbe());

    public static LivePilot FindLivePilot()
    {
        using var watcher = NewWatcher();
        var pilots = watcher.ListLivePilots();
        var filter = LiveSwitch.PilotFilter;
        var chosen = filter is null
            ? pilots.FirstOrDefault()
            : pilots.FirstOrDefault(p => string.Equals(p.Listener, filter, StringComparison.OrdinalIgnoreCase));

        return chosen ?? throw new InvalidOperationException(
            $"No live pilot found ({pilots.Count} listed). Start the EVE client and log a character in, or set SAYWHAT_LIVE_PILOT.");
    }

    public static LiveChannel LocalOf(LivePilot pilot) =>
        pilot.Channels.First(c => c.FilePath == c.LocalLogPath);

    public static FileStream OpenWriteShared(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public static FileStream OpenReadOnlyShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public static IReadOnlyList<DecodedLine> ReadAllLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var data = bytes[..read];
        var encoding = LogTextDecoder.DetectEncoding(data);
        return LogTextDecoder.ReadCompleteLines(data, encoding.PreambleLength, encoding).Lines;
    }

    public static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
