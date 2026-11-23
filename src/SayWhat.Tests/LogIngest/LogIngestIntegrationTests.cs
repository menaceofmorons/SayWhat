using System.Text;
using SayWhat.Core.LogIngest;
using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed class LogIngestIntegrationTests : IDisposable
{
    private const string Rule = "---------------------------------------------------------------";
    private const string Bom = "\uFEFF";
    private const string Pilot = "Pilot One";
    private const string OtherPilot = "Pilot Two";
    private const string LocalStart = "2026.11.23 10:00:00";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> KnownSystems = new(StringComparer.OrdinalIgnoreCase) { "Jita", "Perimeter" };

    private readonly string documents = Path.Combine(Path.GetTempPath(), "saywhat-integration-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> writers = [];
    private readonly List<LogWatcher> watchers = [];

    public LogIngestIntegrationTests() => Directory.CreateDirectory(ChatLogs);

    private string ChatLogs => Path.Combine(documents, "EVE", "logs", "Chatlogs");

    public void Dispose()
    {
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        foreach (var writer in writers)
        {
            writer.Dispose();
        }

        Directory.Delete(documents, recursive: true);
    }

    public static TheoryData<string, bool> Encodings => new()
    {
        { "utf16le", false },
        { "utf16be", false },
        { "utf8bom", false },
        { "utf8", false },
        { "utf8bom", true },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void LiveLocalSession_LinesReachTheParserInOrderAndLockReleaseEndsTheSession(string encoding, bool foreignLabels)
    {
        var local = StartClientLog("Local_20261123_100000_111.txt", "local_111", "Local", Pilot, LocalStart, encoding, foreignLabels,
            Line("10:00:01", "EVE System", "Channel changed to Local : Perimeter"),
            Line("10:00:05", OtherPilot, "o7"));
        StartClientLog("Local_20261101_090000_111.txt", "local_111", "Local", Pilot, "2026.11.01 09:00:00", encoding, foreignLabels);
        File.WriteAllBytes(
            Path.Combine(ChatLogs, "SayWhat_session.txt"),
            Encode(Text(Header("local_111", "Local", Pilot, LocalStart, foreignLabels)), encoding));
        Directory.CreateDirectory(Path.Combine(ChatLogs, "unrelated"));

        var watcher = NewWatcher();
        var pilot = Assert.Single(watcher.ListLivePilots());
        Assert.Equal(Pilot, pilot.Listener);
        var channel = Assert.Single(pilot.Channels);
        Assert.Equal(local.Name, channel.FilePath);

        using var session = Attach(watcher, channel);
        Assert.Equal(2, session.Backlog.Lines.Count);
        Assert.Equal(Pilot, session.Parser.Header!.Listener);

        Write(local, encoding, Line("10:01:00", OtherPilot, "first") + Line("10:01:02", Pilot, "second"));
        Write(local, encoding, Line("10:02:00", "EVE System", "Channel changed to Local : Jita") + "not a chat line\r\n");
        Write(local, encoding, "[ 2026.11.23 10:03:00 ] " + OtherPilot + " > spl");
        Thread.Sleep(100);
        Write(local, encoding, "it line\r\n");

        Assert.True(WaitFor(() => session.LineCount >= 5), "lines did not reach the parser");
        Assert.False(session.Ended.IsSet);

        local.Dispose();
        Assert.True(session.Ended.Wait(Wait, TestContext.Current.CancellationToken), "session did not end after the lock was released");

        var live = session.Live();
        Assert.Equal(
            [LineKind.OtherPilot, LineKind.OwnLine, LineKind.SystemMessage, LineKind.Unparseable, LineKind.OtherPilot],
            live.Select(l => l.Kind));
        Assert.Equal(
            Enumerable.Range(session.Backlog.Lines.Count + 1, live.Count),
            live.Select(l => l.LineId.Sequence));
        Assert.Equal("split line", live[4].Text);
        Assert.Equal("2026.11.23 10:03:00", live[4].TimestampText);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 3, 0, TimeSpan.Zero), live[4].TimestampUtc);
        Assert.Equal(OtherPilot, live[0].Speaker);
        Assert.Equal(Pilot, live[1].Speaker);

        var change = Assert.Single(session.Changes());
        Assert.Equal("Jita", change.SolarSystem);
        Assert.Equal(live[2].LineId, change.LineId);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 2, 0, TimeSpan.Zero), change.TimestampUtc);

        var warning = Assert.Single(session.Warnings());
        Assert.Equal(ParseWarningKind.LineUnparseable, warning.Kind);
        Assert.Equal(live[3].LineId, warning.LineId);

        var file = File.ReadAllBytes(local.Name);
        var enc = LogTextDecoder.DetectEncoding(file);
        var expectedOffsets = LogTextDecoder.ReadCompleteLines(file, enc.PreambleLength, enc).Lines
            .Where(l => l.ByteOffset >= session.TailStart)
            .Select(l => l.ByteOffset);
        Assert.Equal(expectedOffsets, live.Select(l => l.LineId.ByteOffset));
    }

    [Fact]
    public void LiveChannelSession_FollowsANonLocalChannelAndEndsWhenLocalIsReleased()
    {
        var local = StartClientLog("Local_20261123_100000_111.txt", "local_111", "Local", Pilot, LocalStart, "utf16le", false);
        var corp = StartClientLog("Corp_20261123_100500_111.txt", "corp_9", "Corp", Pilot, "2026.11.23 10:05:00", "utf16le", false,
            Line("10:05:01", "EVE System", "Channel changed to Corp"));
        StartClientLog("Corp_20261122_100500_111.txt", "corp_9", "Corp", Pilot, "2026.11.22 10:05:00", "utf16le", false);
        StartClientLog("Alliance_20261122_080000_111.txt", "alliance_3", "Alliance", Pilot, "2026.11.22 08:00:00", "utf16le", false);

        var watcher = NewWatcher();
        var pilot = Assert.Single(watcher.ListLivePilots());
        Assert.Equal(["Corp", "Local"], pilot.Channels.Select(c => c.ChannelName));
        var channel = pilot.Channels.Single(c => c.ChannelName == "Corp");
        Assert.Equal(corp.Name, channel.FilePath);
        Assert.Equal(local.Name, channel.LocalLogPath);

        using var session = Attach(watcher, channel);
        Write(corp, "utf16le", Line("10:06:00", OtherPilot, "hello corp"));
        Assert.True(WaitFor(() => session.LineCount >= 1), "channel line did not reach the parser");
        Assert.False(session.Ended.IsSet);

        corp.Dispose();
        Thread.Sleep(200);
        Assert.False(session.Ended.IsSet);

        local.Dispose();
        Assert.True(session.Ended.Wait(Wait, TestContext.Current.CancellationToken));
        Assert.Equal("hello corp", Assert.Single(session.Live()).Text);
        Assert.Equal("Corp", session.Parser.Header!.ChannelName);
    }

    [Fact]
    public void PilotWhoseLocalIsNotHeld_IsNotLive()
    {
        var held = StartClientLog("Local_20261123_100000_111.txt", "local_111", "Local", Pilot, LocalStart, "utf16le", false);
        StartClientLog("Local_20261123_100000_222.txt", "local_222", "Local", OtherPilot, LocalStart, "utf16le", false).Dispose();

        var pilots = NewWatcher().ListLivePilots();

        Assert.Equal(Pilot, Assert.Single(pilots).Listener);
        held.Dispose();
        Assert.Empty(NewWatcher().ListLivePilots());
    }

    [Fact]
    public void FolderSupply_PointsTheWatcherAtTheChatLogFolder()
    {
        var folder = new WindowsEveLogFolder(() => documents);

        Assert.Equal(ChatLogs, folder.Path);
    }

    [Fact]
    public void ParsedLineContract_CarriesTheRoadmapFields()
    {
        Assert.Equal(
            ["LineId", "TimestampText", "TimestampUtc", "Speaker", "Kind", "Text"],
            typeof(ParsedLine).GetConstructors().Single().GetParameters().Select(p => p.Name));
        Assert.Equal(["Sequence", "ByteOffset"], typeof(LineId).GetConstructors().Single().GetParameters().Select(p => p.Name));
        Assert.Equal(
            [nameof(LineKind.OtherPilot), nameof(LineKind.OwnLine), nameof(LineKind.SystemMessage), nameof(LineKind.Unparseable)],
            Enum.GetNames<LineKind>());
    }

    private static string Line(string time, string speaker, string text) =>
        $"{Bom}[ 2026.11.23 {time} ] {speaker} > {text}\r\n";

    private static string[] Header(string channelId, string channelName, string listener, string start, bool foreignLabels) =>
        foreignLabels
            ?
            [
                Rule,
                $"  Kanal-ID:        {channelId}",
                $"  Kanalname:       {channelName}",
                $"  Zuhörer:         {listener}",
                $"  Sitzung gestartet: {start}",
                Rule,
            ]
            :
            [
                Rule,
                $"  Channel ID:      {channelId}",
                $"  Channel Name:    {channelName}",
                $"  Listener:        {listener}",
                $"  Session started: {start}",
                Rule,
            ];

    private static string Text(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\r\n"));

    private static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "utf16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        "utf8bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        _ => Encoding.UTF8.GetBytes(text),
    };

    private static byte[] EncodeBody(string text, string encoding) => encoding switch
    {
        "utf16le" => Encoding.Unicode.GetBytes(text),
        "utf16be" => Encoding.BigEndianUnicode.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    private FileStream StartClientLog(
        string fileName,
        string channelId,
        string channelName,
        string listener,
        string start,
        string encoding,
        bool foreignLabels,
        params string[] body)
    {
        var path = Path.Combine(ChatLogs, fileName);
        var text = "\r\n\r\n\r\n\r\n" + Text(Header(channelId, channelName, listener, start, foreignLabels)) + string.Concat(body);
        var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encode(text, encoding);
        writer.Write(bytes);
        writer.Flush();
        writers.Add(writer);
        return writer;
    }

    private static void Write(FileStream writer, string encoding, string text)
    {
        writer.Write(EncodeBody(text, encoding));
        writer.Flush();
    }

    private LogWatcher NewWatcher()
    {
        var watcher = new LogWatcher(
            new WindowsEveLogFolder(() => documents),
            new WindowsLogLockProbe(),
            new LogWatcherOptions("SayWhat_", Fast));
        watchers.Add(watcher);
        return watcher;
    }

    private static Session Attach(LogWatcher watcher, LiveChannel channel) => new(watcher, channel);

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private sealed class Session : IDisposable
    {
        private readonly object gate = new();
        private readonly List<ParsedLine> lines = [];
        private readonly List<SystemChange> changes = [];
        private readonly List<ParseWarning> warnings = [];

        public Session(LogWatcher watcher, LiveChannel channel)
        {
            Parser = new LogLineParser(knownSolarSystems: KnownSystems);
            var bytes = ReadShared(channel.FilePath);
            var encoding = LogTextDecoder.DetectEncoding(bytes);
            var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);
            Backlog = Parser.Parse(chunk.Lines);
            TailStart = bytes.Length;
            watcher.LinesAvailable += OnLines;
            watcher.SessionEnded += Ended.Set;
            watcher.Follow(channel);
            Watcher = watcher;
        }

        public LogLineParser Parser { get; }

        public ParseResult Backlog { get; }

        public long TailStart { get; }

        public ManualResetEventSlim Ended { get; } = new(false);

        private LogWatcher Watcher { get; }

        public int LineCount
        {
            get
            {
                lock (gate)
                {
                    return lines.Count;
                }
            }
        }

        public List<ParsedLine> Live()
        {
            lock (gate)
            {
                return [.. lines];
            }
        }

        public List<SystemChange> Changes()
        {
            lock (gate)
            {
                return [.. changes];
            }
        }

        public List<ParseWarning> Warnings()
        {
            lock (gate)
            {
                return [.. warnings];
            }
        }

        public void Dispose()
        {
            Watcher.Stop();
            Ended.Dispose();
        }

        private static byte[] ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        private void OnLines(IReadOnlyList<DecodedLine> decoded)
        {
            lock (gate)
            {
                var result = Parser.Parse(decoded);
                lines.AddRange(result.Lines);
                changes.AddRange(result.SystemChanges);
                warnings.AddRange(result.Warnings);
            }
        }
    }
}
