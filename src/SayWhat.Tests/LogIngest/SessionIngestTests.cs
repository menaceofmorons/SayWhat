using System.Collections.Concurrent;
using System.Text;
using SayWhat.Core.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed class SessionIngestTests : IDisposable
{
    private const string Rule = "---------------------------------------------------------------";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "saywhat-ingest-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLogLockProbe probe = new();
    private readonly List<IDisposable> disposables = [];

    public SessionIngestTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        foreach (var d in disposables)
        {
            d.Dispose();
        }

        Directory.Delete(dir, recursive: true);
    }

    private static byte[] Encode(string text, string encodingName) => encodingName switch
    {
        "utf16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "utf16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        "utf8bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        _ => Encoding.UTF8.GetBytes(text),
    };

    private static byte[] Raw(string text, string encodingName) => encodingName switch
    {
        "utf16le" => Encoding.Unicode.GetBytes(text),
        "utf16be" => Encoding.BigEndianUnicode.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    private static string Line(string speaker, string text) => $"[ 2026.11.24 10:00:00 ] {speaker} > {text}\r\n";

    private LiveChannel CreateLocal(string encodingName, params string[] bodyLines)
    {
        var header = string.Join("\r\n", Rule, "  Channel ID:      local", "  Channel Name:    Local", "  Listener:        Pilot One", "  Session started: 2026.11.24 09:00:00", Rule) + "\r\n";
        var path = Path.Combine(dir, "Local_20261124_090000_1.txt");
        File.WriteAllBytes(path, Encode(header + string.Concat(bodyLines), encodingName));
        probe.Set(path, LogLockState.Held);
        return new LiveChannel("Pilot One", "local", "Local", path, path, null);
    }

    private static void Append(string path, string encodingName, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Raw(text, encodingName));
    }

    private SessionIngest Start(LiveChannel channel, Action<ParsedLine> consumer, Action<ParseWarning>? warning = null)
    {
        var watcher = new LogWatcher(new FakeEveLogFolder(dir), probe, new LogWatcherOptions("SayWhat_", Fast, false));
        var ingest = new SessionIngest(watcher, channel, consumer, warning);
        disposables.Add(ingest);
        disposables.Add(watcher);
        ingest.Start();
        return ingest;
    }

    private static void WaitFor(Func<bool> condition)
    {
        SpinWait.SpinUntil(condition, Wait);
        Assert.True(condition(), "condition not met in time");
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8bom")]
    [InlineData("utf8")]
    public void HeaderAndBacklogAreNotEmittedAndLiveLinesAre(string encodingName)
    {
        var channel = CreateLocal(encodingName, Line("Other", "backlog one"), Line("Other", "backlog two"));
        var lines = new ConcurrentQueue<ParsedLine>();
        var ingest = Start(channel, lines.Enqueue);

        Assert.Equal("Pilot One", ingest.Header?.Listener);
        Assert.Empty(lines);

        Append(channel.FilePath, encodingName, Line("Other", "live one") + Line("Pilot One", "live two"));
        WaitFor(() => lines.Count == 2);

        var got = lines.ToArray();
        Assert.Equal(["live one", "live two"], got.Select(l => l.Text));
        Assert.Equal(LineKind.OwnLine, got[1].Kind);
        Assert.True(got[0].LineId.Sequence < got[1].LineId.Sequence);
    }

    [Fact]
    public void ByteOffsetsEqualFileOffsets()
    {
        var channel = CreateLocal("utf16le", Line("Other", "backlog"));
        var lines = new ConcurrentQueue<ParsedLine>();
        Start(channel, lines.Enqueue);

        var first = new FileInfo(channel.FilePath).Length;
        Append(channel.FilePath, "utf16le", Line("Other", "first"));
        var second = new FileInfo(channel.FilePath).Length;
        Append(channel.FilePath, "utf16le", Line("Other", "second"));
        WaitFor(() => lines.Count == 2);

        Assert.Equal([first, second], lines.Select(l => l.LineId.ByteOffset));
    }

    [Fact]
    public void PartialLastLineIsWithheldUntilComplete()
    {
        var channel = CreateLocal("utf8");
        var lines = new ConcurrentQueue<ParsedLine>();
        Start(channel, lines.Enqueue);

        Append(channel.FilePath, "utf8", "[ 2026.11.24 10:00:00 ] Other > par");
        Thread.Sleep(200);
        Assert.Empty(lines);

        Append(channel.FilePath, "utf8", "tial\r\n");
        WaitFor(() => lines.Count == 1);
        Assert.Equal("partial", lines.Single().Text);
    }

    [Fact]
    public void ParseWarningsAreForwarded()
    {
        var channel = CreateLocal("utf8");
        var warnings = new ConcurrentQueue<ParseWarning>();
        Start(channel, _ => { }, warnings.Enqueue);

        Append(channel.FilePath, "utf8", "garbage without brackets\r\n");
        WaitFor(() => warnings.Count == 1);
        Assert.Equal(ParseWarningKind.LineUnparseable, warnings.Single().Kind);
    }

    [Fact]
    public void SessionEndFiresOnceOnLockRelease()
    {
        var channel = CreateLocal("utf8");
        var ingest = Start(channel, _ => { });
        var count = 0;
        ingest.SessionEnded += () => Interlocked.Increment(ref count);

        probe.Set(channel.LocalLogPath, LogLockState.NotHeld);
        WaitFor(() => Volatile.Read(ref count) == 1);
        Thread.Sleep(150);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void ConsumerExceptionRaisesFaultAndLaterLinesStillArrive()
    {
        var channel = CreateLocal("utf8");
        var seen = new ConcurrentQueue<string>();
        var faults = new ConcurrentQueue<SessionFault>();
        var ingest = Start(channel, line =>
        {
            seen.Enqueue(line.Text);
            if (line.Text == "bad")
            {
                throw new InvalidOperationException("boom");
            }
        });
        ingest.Faulted += faults.Enqueue;

        Append(channel.FilePath, "utf8", Line("Other", "bad"));
        WaitFor(() => faults.Count == 1);
        Append(channel.FilePath, "utf8", Line("Other", "after"));
        WaitFor(() => seen.Count == 2);

        var fault = faults.Single();
        Assert.Equal(SessionIngest.ConsumerComponent, fault.Component);
        Assert.Equal(nameof(InvalidOperationException), fault.ExceptionType);
        Assert.Equal(["bad", "after"], seen);
    }

    [Fact]
    public void ThrowingFaultSubscriberDoesNotStopSession()
    {
        var channel = CreateLocal("utf8");
        var seen = new ConcurrentQueue<string>();
        var ingest = Start(channel, line =>
        {
            seen.Enqueue(line.Text);
            throw new InvalidOperationException();
        });
        ingest.Faulted += _ => throw new InvalidOperationException();

        Append(channel.FilePath, "utf8", Line("Other", "a") + Line("Other", "b"));
        WaitFor(() => seen.Count == 2);
    }

    [Fact]
    public void MissingFileRaisesFaultInsteadOfThrowing()
    {
        var missing = Path.Combine(dir, "missing.txt");
        var channel = new LiveChannel("P", "local", "Local", missing, missing, null);
        var watcher = new LogWatcher(new FakeEveLogFolder(dir), probe);
        var faults = new List<SessionFault>();
        var ingest = new SessionIngest(watcher, channel, _ => { });
        ingest.Faulted += faults.Add;
        disposables.Add(ingest);
        disposables.Add(watcher);

        ingest.Start();

        Assert.Equal(SessionIngest.IngestComponent, faults.Single().Component);
    }
}
