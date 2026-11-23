using System.Text.RegularExpressions;

namespace SayWhat.Core.LogIngest;

public sealed record LiveChannel(
    string Listener,
    string ChannelId,
    string ChannelName,
    string FilePath,
    string LocalLogPath,
    DateTimeOffset? SessionStartUtc);

public sealed record LivePilot(string Listener, IReadOnlyList<LiveChannel> Channels);

public sealed record LogWatcherOptions(
    string SayWhatLogPrefix = "",
    TimeSpan? PollInterval = null,
    bool UseFileNotifications = true);

public sealed partial class LogWatcher : IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private const string LocalChannelIdPrefix = "local";
    private const int HeaderReadBytes = 4096;

    private readonly IEveLogFolder folder;
    private readonly ILogLockProbe probe;
    private readonly LogWatcherOptions options;
    private readonly object gate = new();
    private Tail? tail;

    public LogWatcher(IEveLogFolder folder, ILogLockProbe probe, LogWatcherOptions? options = null)
    {
        this.folder = folder;
        this.probe = probe;
        this.options = options ?? new LogWatcherOptions();
    }

    public event Action<IReadOnlyList<DecodedLine>>? LinesAvailable;

    public event Action? SessionEnded;

    public IReadOnlyList<LivePilot> ListLivePilots()
    {
        var newest = ScanNewestPerPilotAndChannel();
        var pilots = new List<LivePilot>();

        foreach (var byPilot in newest.GroupBy(e => e.Header.Listener, StringComparer.OrdinalIgnoreCase))
        {
            var local = byPilot
                .Where(e => IsLocal(e.Header))
                .OrderByDescending(SortKey)
                .FirstOrDefault();

            if (local is null || probe.Probe(local.Path) != LogLockState.Held)
            {
                continue;
            }

            var channels = byPilot
                .Where(e => e == local || StartedWithinSession(e, local))
                .OrderBy(e => e.Header.ChannelName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new LiveChannel(
                    e.Header.Listener,
                    e.Header.ChannelId,
                    e.Header.ChannelName,
                    e.Path,
                    local.Path,
                    e.Header.SessionStartUtc))
                .ToList();

            pilots.Add(new LivePilot(byPilot.Key, channels));
        }

        return pilots;
    }

    public void Follow(LiveChannel channel)
    {
        Stop();
        var started = new Tail(channel, probe, options, OnLines, OnSessionEnded);
        lock (gate)
        {
            tail = started;
        }

        started.Start();
    }

    public void Stop()
    {
        Tail? current;
        lock (gate)
        {
            current = tail;
            tail = null;
        }

        current?.Dispose();
    }

    public void Dispose() => Stop();

    private void OnLines(IReadOnlyList<DecodedLine> lines) => LinesAvailable?.Invoke(lines);

    private void OnSessionEnded() => SessionEnded?.Invoke();

    private static bool IsLocal(LogHeader header) =>
        header.ChannelId.StartsWith(LocalChannelIdPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool StartedWithinSession(Entry entry, Entry local) =>
        entry.Header.SessionStartUtc is { } start
        && local.Header.SessionStartUtc is { } localStart
        && start >= localStart;

    private static DateTimeOffset SortKey(Entry entry) =>
        entry.Header.SessionStartUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(entry.Path), TimeSpan.Zero);

    private List<Entry> ScanNewestPerPilotAndChannel()
    {
        var entries = new List<Entry>();
        if (!Directory.Exists(folder.Path))
        {
            return entries;
        }

        foreach (var path in NewestPerNameKey(Directory.EnumerateFiles(folder.Path, "*.txt").Where(p => !IsSayWhatFile(p))))
        {
            var header = TryReadHeader(path);
            if (header is not null)
            {
                entries.Add(new Entry(path, header));
            }
        }

        return entries
            .GroupBy(e => (Listener: e.Header.Listener.ToUpperInvariant(), Channel: e.Header.ChannelId.ToUpperInvariant()))
            .Select(g => g.OrderByDescending(SortKey).First())
            .ToList();
    }

    private static IEnumerable<string> NewestPerNameKey(IEnumerable<string> paths)
    {
        var newest = new Dictionary<(string Channel, string Pilot), (string Stamp, string Path)>();
        foreach (var path in paths)
        {
            var match = FileNamePattern().Match(Path.GetFileName(path));
            if (!match.Success)
            {
                yield return path;
                continue;
            }

            var key = (match.Groups["channel"].Value.ToUpperInvariant(), match.Groups["id"].Value);
            var stamp = match.Groups["date"].Value + match.Groups["time"].Value;
            if (!newest.TryGetValue(key, out var current) || string.CompareOrdinal(stamp, current.Stamp) > 0)
            {
                newest[key] = (stamp, path);
            }
        }

        foreach (var (_, path) in newest.Values)
        {
            yield return path;
        }
    }

    [GeneratedRegex(@"^(?<channel>.+)_(?<date>\d{8})_(?<time>\d{6})(?:_(?<id>\d+))?\.txt$")]
    private static partial Regex FileNamePattern();

    private bool IsSayWhatFile(string path) =>
        options.SayWhatLogPrefix.Length > 0
        && Path.GetFileName(path).StartsWith(options.SayWhatLogPrefix, StringComparison.OrdinalIgnoreCase);

    private static LogHeader? TryReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeaderReadBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            var bytes = buffer[..read];
            var encoding = LogTextDecoder.DetectEncoding(bytes);
            var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);
            var parser = new LogLineParser();
            parser.Parse(chunk.Lines);
            return parser.Header;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Entry(string Path, LogHeader Header);

    private sealed class Tail : IDisposable
    {
        private readonly LiveChannel channel;
        private readonly ILogLockProbe probe;
        private readonly LogWatcherOptions options;
        private readonly Action<IReadOnlyList<DecodedLine>> onLines;
        private readonly Action onSessionEnded;
        private readonly CancellationTokenSource cancel = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private FileStream? stream;
        private FileSystemWatcher? watcher;
        private LogEncoding? encoding;
        private long position;
        private Task? loop;

        public Tail(
            LiveChannel channel,
            ILogLockProbe probe,
            LogWatcherOptions options,
            Action<IReadOnlyList<DecodedLine>> onLines,
            Action onSessionEnded)
        {
            this.channel = channel;
            this.probe = probe;
            this.options = options;
            this.onLines = onLines;
            this.onSessionEnded = onSessionEnded;
        }

        public void Start()
        {
            stream = new FileStream(channel.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[3];
            var headRead = stream.Read(head, 0, head.Length);
            encoding = LogTextDecoder.DetectEncoding(head.AsSpan(0, headRead));
            position = stream.Length;

            if (options.UseFileNotifications)
            {
                StartWatcher();
            }

            loop = Task.Run(RunAsync);
        }

        public void Dispose()
        {
            cancel.Cancel();
            try
            {
                loop?.Wait();
            }
            catch (AggregateException)
            {
            }

            watcher?.Dispose();
            stream?.Dispose();
            cancel.Dispose();
            wake.Dispose();
        }

        private void StartWatcher()
        {
            var directory = Path.GetDirectoryName(channel.FilePath)!;
            watcher = new FileSystemWatcher(directory, Path.GetFileName(channel.FilePath))
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Changed += (_, _) => Signal();
            watcher.EnableRaisingEvents = true;
        }

        private void Signal()
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync()
        {
            var interval = options.PollInterval ?? DefaultPollInterval;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    await wake.WaitAsync(interval, cancel.Token).ConfigureAwait(false);
                    ReadNewLines();

                    if (probe.Probe(channel.LocalLogPath) == LogLockState.NotHeld)
                    {
                        onSessionEnded();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ReadNewLines()
        {
            var length = stream!.Length;
            if (length <= position)
            {
                return;
            }

            var buffer = new byte[length - position];
            stream.Position = position;
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var chunk = LogTextDecoder.ReadCompleteLines(buffer[..read], 0, encoding!);
            if (chunk.Lines.Count == 0)
            {
                return;
            }

            var lines = chunk.Lines.Select(l => l with { ByteOffset = l.ByteOffset + position }).ToList();
            position += chunk.NextOffset;
            onLines(lines);
        }
    }
}
