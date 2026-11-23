using System.Collections.Concurrent;
using System.Text;
using SayWhat.Core.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed class LogWatcherTests : IDisposable
{
    private const string Rule = "---------------------------------------------------------------";
    private const string Prefix = "SayWhat_";
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "saywhat-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly SwitchProbe probe = new();
    private readonly List<LogWatcher> watchers = [];

    public LogWatcherTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        foreach (var w in watchers)
        {
            w.Dispose();
        }

        Directory.Delete(dir, recursive: true);
    }

    private sealed class SwitchProbe : ILogLockProbe
    {
        private readonly ConcurrentDictionary<string, LogLockState> states = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string path, LogLockState state) => states[path] = state;

        public LogLockState Probe(string path) => states.GetValueOrDefault(path, LogLockState.NotHeld);
    }

    private static string[] Header(string channelId, string channel, string listener, string start) =>
    [
        Rule,
        $"  Channel ID:      {channelId}",
        $"  Channel Name:    {channel}",
        $"  Listener:        {listener}",
        $"  Session started: {start}",
        Rule,
    ];

    private static byte[] Encode(string text, string encodingName) => encodingName switch
    {
        "utf16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        "utf16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        "utf8bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        _ => Encoding.UTF8.GetBytes(text),
    };

    private string Create(string name, string[] header, string encodingName = "utf16le", params string[] lines)
    {
        var path = Path.Combine(dir, name);
        var text = string.Join("\r\n", header.Concat(lines)) + "\r\n";
        File.WriteAllBytes(path, Encode(text, encodingName));
        return path;
    }

    private string CreateLocal(string listener, string start, string file) =>
        Create(file, Header("local", "Local", listener, start));

    private string CreateChannel(string channelId, string listener, string start, string file) =>
        Create(file, Header(channelId, channelId, listener, start));

    private LogWatcher NewWatcher(bool notifications = true)
    {
        var w = new LogWatcher(new FakeEveLogFolder(dir), probe, new LogWatcherOptions(Prefix, Fast, notifications));
        watchers.Add(w);
        return w;
    }

    private static void Append(string path, string encodingName, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = encodingName switch
        {
            "utf16le" => Encoding.Unicode.GetBytes(text),
            "utf16be" => Encoding.BigEndianUnicode.GetBytes(text),
            _ => Encoding.UTF8.GetBytes(text),
        };
        stream.Write(bytes);
    }

    [Fact]
    public void ListLivePilots_ReadsOnlyNewestFilePerChannelAndPilotNamed()
    {
        var current = CreateLocal("Pilot One", "2026.11.23 10:00:00", "Local_20261123_100000_111.txt");
        var stale = CreateLocal("Ghost", "2026.11.01 10:00:00", "Local_20261101_100000_111.txt");
        probe.Set(current, LogLockState.Held);
        probe.Set(stale, LogLockState.Held);

        var pilots = NewWatcher().ListLivePilots();

        Assert.Equal(["Pilot One"], pilots.Select(p => p.Listener));
    }

    [Fact]
    public void ListLivePilots_KeepsNewestFilePerPilotIdWhenNamesMatchThePattern()
    {
        var one = CreateLocal("Pilot One", "2026.11.23 10:00:00", "Local_20261123_100000_111.txt");
        var two = CreateLocal("Pilot Two", "2026.11.23 10:00:00", "Local_20261123_100000_222.txt");
        probe.Set(one, LogLockState.Held);
        probe.Set(two, LogLockState.Held);

        var pilots = NewWatcher().ListLivePilots();

        Assert.Equal(["Pilot One", "Pilot Two"], pilots.Select(p => p.Listener).Order());
    }

    private static List<string> Collect(LogWatcher watcher)
    {
        var received = new List<string>();
        watcher.LinesAvailable += lines =>
        {
            lock (received)
            {
                received.AddRange(lines.Select(l => l.Text));
            }
        };
        return received;
    }

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

    private static int Count(List<string> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private void FollowLocal(LogWatcher watcher, string localPath)
    {
        probe.Set(localPath, LogLockState.Held);
        var channel = watcher.ListLivePilots().Single().Channels.Single(c => c.FilePath == localPath);
        watcher.Follow(channel);
    }

    [Fact]
    public void NewestFilePerPilotAndChannel_IsKept()
    {
        var older = CreateLocal("Pilot One", "2026.11.20 08:00:00", "old.txt");
        var newer = CreateLocal("Pilot One", "2026.11.23 10:00:00", "new.txt");
        probe.Set(newer, LogLockState.Held);
        probe.Set(older, LogLockState.Held);

        var pilot = NewWatcher().ListLivePilots().Single();

        Assert.Equal(newer, pilot.Channels.Single().FilePath);
    }

    [Fact]
    public void SayWhatPrefixedFiles_AreIgnored()
    {
        var local = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        CreateLocal("Pilot One", "2026.11.23 11:00:00", Prefix + "session.txt");
        probe.Set(local, LogLockState.Held);

        var pilot = NewWatcher().ListLivePilots().Single();

        Assert.Equal(local, pilot.Channels.Single().FilePath);
    }

    [Fact]
    public void LiveAndNotLivePilots_AreSeparated()
    {
        var one = CreateLocal("Pilot One", "2026.11.23 10:00:00", "one.txt");
        CreateLocal("Pilot Two", "2026.11.23 10:00:00", "two.txt");
        probe.Set(one, LogLockState.Held);

        var pilots = NewWatcher().ListLivePilots();

        Assert.Equal("Pilot One", Assert.Single(pilots).Listener);
    }

    [Fact]
    public void UnavailableLocal_IsNotLive()
    {
        var one = CreateLocal("Pilot One", "2026.11.23 10:00:00", "one.txt");
        probe.Set(one, LogLockState.Unavailable);

        Assert.Empty(NewWatcher().ListLivePilots());
    }

    [Fact]
    public void NoLivePilots_GivesEmptyList()
    {
        CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");

        Assert.Empty(NewWatcher().ListLivePilots());
    }

    [Fact]
    public void MissingFolder_GivesEmptyList()
    {
        var w = new LogWatcher(new FakeEveLogFolder(Path.Combine(dir, "absent")), probe);

        Assert.Empty(w.ListLivePilots());
    }

    [Fact]
    public void ChannelList_FollowsSessionRule()
    {
        var local = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var current = CreateChannel("corp", "Pilot One", "2026.11.23 10:05:00", "corp.txt");
        CreateChannel("alliance", "Pilot One", "2026.11.23 09:00:00", "alliance.txt");
        CreateChannel("corp", "Pilot Two", "2026.11.23 10:05:00", "corp2.txt");
        probe.Set(local, LogLockState.Held);

        var channels = NewWatcher().ListLivePilots().Single().Channels;

        Assert.Equal(2, channels.Count);
        Assert.Contains(channels, c => c.FilePath == local);
        Assert.Contains(channels, c => c.FilePath == current);
    }

    [Fact]
    public void UnreadableHeader_IsSkipped()
    {
        var local = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        File.WriteAllText(Path.Combine(dir, "junk.txt"), "no header here\r\n");
        probe.Set(local, LogLockState.Held);

        Assert.Single(NewWatcher().ListLivePilots().Single().Channels);
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8bom")]
    [InlineData("utf8")]
    public void Tail_StartsAtEnd_AndDeliversAppendedCompleteLines(string encoding)
    {
        var path = Create("local.txt", Header("local", "Local", "Pilot One", "2026.11.23 10:00:00"), encoding, "[ 2026.11.23 10:00:01 ] A > old");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        Append(path, encoding, "[ 2026.11.23 10:00:02 ] A > new one\r\n[ 2026.11.23 10:00:03 ] A > new two\r\n");

        Assert.True(WaitFor(() => Count(received) == 2));
        lock (received)
        {
            Assert.Equal("[ 2026.11.23 10:00:02 ] A > new one", received[0]);
            Assert.Equal("[ 2026.11.23 10:00:03 ] A > new two", received[1]);
        }
    }

    [Fact]
    public void PartialLine_WaitsForNextRead()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        Append(path, "utf16le", "[ 2026.11.23 10:00:02 ] A > hal");
        Thread.Sleep(200);
        Assert.Equal(0, Count(received));

        Append(path, "utf16le", "f\r\n");

        Assert.True(WaitFor(() => Count(received) == 1));
        lock (received)
        {
            Assert.EndsWith("half", received[0]);
        }
    }

    [Fact]
    public void LineOffsets_AreFileByteOffsets()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var length = new FileInfo(path).Length;
        var watcher = NewWatcher();
        var offsets = new ConcurrentBag<long>();
        watcher.LinesAvailable += lines =>
        {
            foreach (var l in lines)
            {
                offsets.Add(l.ByteOffset);
            }
        };
        FollowLocal(watcher, path);

        Append(path, "utf16le", "x\r\n");

        Assert.True(WaitFor(() => !offsets.IsEmpty));
        Assert.Equal(length, offsets.Single());
    }

    [Fact]
    public void PollTimerAlone_DeliversLines()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher(notifications: false);
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        Append(path, "utf16le", "one\r\n");

        Assert.True(WaitFor(() => Count(received) == 1));
    }

    [Fact]
    public void SessionEnd_IsRaisedOnce_AndNothingFollows()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        var ended = 0;
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        FollowLocal(watcher, path);

        probe.Set(path, LogLockState.NotHeld);

        Assert.True(WaitFor(() => Volatile.Read(ref ended) == 1));
        Append(path, "utf16le", "late\r\n");
        Thread.Sleep(200);
        Assert.Equal(1, Volatile.Read(ref ended));
        Assert.Equal(0, Count(received));
    }

    [Fact]
    public void SessionEnd_IsDetectedWhileFollowingAnotherChannel()
    {
        var local = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var corp = CreateChannel("corp", "Pilot One", "2026.11.23 10:05:00", "corp.txt");
        probe.Set(local, LogLockState.Held);
        var watcher = NewWatcher();
        var ended = 0;
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        watcher.Follow(watcher.ListLivePilots().Single().Channels.Single(c => c.FilePath == corp));

        probe.Set(local, LogLockState.NotHeld);

        Assert.True(WaitFor(() => Volatile.Read(ref ended) == 1));
    }

    [Fact]
    public void AnotherProcess_CanStillAppend_WhileWatcherReads()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.Unicode.GetBytes("a\r\n"));
        writer.Flush();

        Assert.True(WaitFor(() => Count(received) == 1));
    }

    [Fact]
    public void Watcher_LeavesFilesUnchanged()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var watcher = NewWatcher();
        FollowLocal(watcher, path);
        Thread.Sleep(150);
        watcher.Stop();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    private static void AppendRaw(string path, params byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    private static void AssertCompletesWithin(Action action)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(Wait), "call did not return (deadlock)");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Stop_FromSessionEndedHandler_DoesNotDeadlock()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var returned = new ManualResetEventSlim();
        watcher.SessionEnded += () =>
        {
            watcher.Stop();
            returned.Set();
        };
        FollowLocal(watcher, path);

        probe.Set(path, LogLockState.NotHeld);

        Assert.True(returned.Wait(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Follow_FromSessionEndedHandler_DoesNotDeadlock()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var other = CreateChannel("corp", "Pilot One", "2026.11.23 10:05:00", "corp.txt");
        var watcher = NewWatcher();
        var returned = new ManualResetEventSlim();
        probe.Set(path, LogLockState.Held);
        var channels = watcher.ListLivePilots().Single().Channels;
        var handled = 0;
        watcher.SessionEnded += () =>
        {
            if (Interlocked.Exchange(ref handled, 1) == 1)
            {
                return;
            }

            watcher.Follow(channels.Single(c => c.FilePath == other));
            returned.Set();
        };
        watcher.Follow(channels.Single(c => c.FilePath == path));

        probe.Set(path, LogLockState.NotHeld);

        Assert.True(returned.Wait(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Stop_FromLinesAvailableHandler_DoesNotDeadlock()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var returned = new ManualResetEventSlim();
        watcher.LinesAvailable += _ =>
        {
            watcher.Stop();
            returned.Set();
        };
        FollowLocal(watcher, path);

        Append(path, "utf16le", "one\r\n");

        Assert.True(returned.Wait(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Follow_FromLinesAvailableHandler_DoesNotDeadlock()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var other = CreateChannel("corp", "Pilot One", "2026.11.23 10:05:00", "corp.txt");
        var watcher = NewWatcher();
        var returned = new ManualResetEventSlim();
        probe.Set(path, LogLockState.Held);
        var channels = watcher.ListLivePilots().Single().Channels;
        watcher.LinesAvailable += _ =>
        {
            watcher.Follow(channels.Single(c => c.FilePath == other));
            returned.Set();
        };
        watcher.Follow(channels.Single(c => c.FilePath == path));

        Append(path, "utf16le", "one\r\n");

        Assert.True(returned.Wait(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotStopDeliveryOrSessionEnd()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        watcher.LinesAvailable += _ => throw new InvalidOperationException();
        var received = Collect(watcher);
        var ended = 0;
        watcher.SessionEnded += () => throw new InvalidOperationException();
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        FollowLocal(watcher, path);

        Append(path, "utf16le", "one\r\n");
        Assert.True(WaitFor(() => Count(received) == 1));
        Append(path, "utf16le", "two\r\n");
        Assert.True(WaitFor(() => Count(received) == 2));
        probe.Set(path, LogLockState.NotHeld);

        Assert.True(WaitFor(() => Volatile.Read(ref ended) == 1));
    }

    [Fact]
    public void LockedRegion_IsTreatedAsNoNewData_ThenRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        var ended = 0;
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        FollowLocal(watcher, path);

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var start = writer.Length;
        var bytes = Encoding.Unicode.GetBytes("locked\r\n");
        writer.Write(bytes);
        writer.Flush();
        writer.Lock(start, bytes.Length);
        Thread.Sleep(200);
        Assert.Equal(0, Count(received));
        writer.Unlock(start, bytes.Length);

        Assert.True(WaitFor(() => Count(received) == 1));
        Assert.Equal(0, Volatile.Read(ref ended));
        probe.Set(path, LogLockState.NotHeld);
        Assert.True(WaitFor(() => Volatile.Read(ref ended) == 1));
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8")]
    public void Follow_StartedMidLine_DeliversTheWholeLineOnceComplete(string encoding)
    {
        var path = Create("local.txt", Header("local", "Local", "Pilot One", "2026.11.23 10:00:00"), encoding);
        Append(path, encoding, "[ 2026.11.23 10:00:02 ] A > par");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        Append(path, encoding, "tial\r\n");

        Assert.True(WaitFor(() => Count(received) == 1));
        lock (received)
        {
            Assert.Equal("[ 2026.11.23 10:00:02 ] A > partial", received[0]);
        }
    }

    [Fact]
    public void Follow_StartedMidCodeUnit_KeepsUtf16Aligned()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        Append(path, "utf16le", "[ 2026.11.23 10:00:02 ] A > par");
        AppendRaw(path, 0x74);
        var watcher = NewWatcher();
        var received = Collect(watcher);
        FollowLocal(watcher, path);

        AppendRaw(path, 0x00);
        Append(path, "utf16le", "\r\nnext\r\n");

        Assert.True(WaitFor(() => Count(received) == 2));
        lock (received)
        {
            Assert.Equal("[ 2026.11.23 10:00:02 ] A > part", received[0]);
            Assert.Equal("next", received[1]);
        }
    }

    [Fact]
    public void Unavailable_DoesNotEndTheSession()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        var ended = 0;
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        FollowLocal(watcher, path);

        probe.Set(path, LogLockState.Unavailable);
        Thread.Sleep(200);
        Append(path, "utf16le", "still\r\n");

        Assert.True(WaitFor(() => Count(received) == 1));
        Assert.Equal(0, Volatile.Read(ref ended));
    }

    [Fact]
    public void SessionEnd_ReleasesTheFile()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var ended = 0;
        watcher.SessionEnded += () => Interlocked.Increment(ref ended);
        FollowLocal(watcher, path);

        probe.Set(path, LogLockState.NotHeld);

        Assert.True(WaitFor(() => Volatile.Read(ref ended) == 1));
        Assert.True(WaitFor(() =>
        {
            try
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }));
    }

    [Fact]
    public void Follow_OfMissingFile_Throws_AndLeavesNothingFollowed()
    {
        var path = CreateLocal("Pilot One", "2026.11.23 10:00:00", "local.txt");
        var watcher = NewWatcher();
        var received = Collect(watcher);
        probe.Set(path, LogLockState.Held);
        var channel = watcher.ListLivePilots().Single().Channels.Single();
        File.Delete(path);

        AssertCompletesWithin(() => Assert.ThrowsAny<IOException>(() => watcher.Follow(channel)));
        AssertCompletesWithin(watcher.Stop);
        Assert.Equal(0, Count(received));
    }

    [Fact]
    public void PollInterval_IsTheDesignValue()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), LogWatcher.DefaultPollInterval);
    }
}
