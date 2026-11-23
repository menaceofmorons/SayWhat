using System.Globalization;
using System.Text.RegularExpressions;
using SayWhat.Core.LogIngest;
using SayWhat.Platform.Windows.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed partial class LiveLogIngestTests
{
    private const char ByteOrderMarkChar = (char)0xFEFF;
    private const int HeaderScanBytes = 16384;
    private const int MaxFullParseBytes = 4 * 1024 * 1024;
    private const int DefaultScanSample = 2000;
    private const int DefaultTypedLines = 5;
    private const int DefaultWaitSeconds = 300;

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.RunningStep), SkipType = typeof(LiveSwitch))]
    public void Od006_ClientRunning_LocalLogProbeHeldAndReadOpenDeniedWriteShareOpenOk()
    {
        const string test = nameof(Od006_ClientRunning_LocalLogProbeHeldAndReadOpenDeniedWriteShareOpenOk);
        var pilot = LiveSession.FindLivePilot();
        var local = LiveSession.LocalOf(pilot);
        var probe = new WindowsLogLockProbe();

        var state = probe.Probe(local.FilePath);
        LiveReport.Write(test, "local.probe", state);
        Assert.Equal(LogLockState.Held, state);

        var failure = Assert.Throws<IOException>(() => LiveSession.OpenReadOnlyShared(local.FilePath).Dispose());
        LiveReport.Write(test, "local.read-open-denying-write-share", $"failed, win32 code {failure.HResult & 0xFFFF}");

        LiveSession.OpenWriteShared(local.FilePath).Dispose();
        LiveReport.Write(test, "local.write-share-open", "ok");

        LiveReport.Write(test, "live-pilots", pilot.Channels.Count > 0 ? 1 : 0);
        LiveReport.Write(test, "channels-listed-for-live-session", pilot.Channels.Count);

        var ordinal = 0;
        foreach (var channel in pilot.Channels)
        {
            ordinal++;
            var isLocal = channel.FilePath == channel.LocalLogPath;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(channel.FilePath);
            var member = channel.SessionStartUtc >= local.SessionStartUtc;
            LiveReport.Write(
                test,
                $"channel#{ordinal}",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"local={isLocal}, probe={probe.Probe(channel.FilePath)}, session-start>=local-session-start={member}, last-write-age-s={age.TotalSeconds:F0}"));
        }

        File.WriteAllText(LiveReport.StatePath, local.FilePath);
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.ClosedStep), SkipType = typeof(LiveSwitch))]
    public void Od006_ClientClosed_LocalLogProbeNotHeldAndOpenSucceeds()
    {
        const string test = nameof(Od006_ClientClosed_LocalLogProbeNotHeldAndOpenSucceeds);
        var path = File.ReadAllText(LiveReport.StatePath).Trim();
        var probe = new WindowsLogLockProbe();

        var state = probe.Probe(path);
        LiveReport.Write(test, "local.probe", state);
        Assert.Equal(LogLockState.NotHeld, state);

        LiveSession.OpenReadOnlyShared(path).Dispose();
        LiveReport.Write(test, "local.read-open-denying-write-share", "ok");
        LiveSession.OpenWriteShared(path).Dispose();
        LiveReport.Write(test, "local.write-share-open", "ok");

        var directory = Path.GetDirectoryName(path)!;
        var stillHeld = 0;
        var ordinal = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(50))
        {
            var fileState = probe.Probe(file);
            LiveReport.Write(test, $"file#{++ordinal}", fileState);
            if (fileState == LogLockState.Held)
            {
                stillHeld++;
            }
        }

        LiveReport.Write(test, "newest-50-files-still-held", stillHeld);
        Assert.Equal(0, stillHeld);
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.Enabled), SkipType = typeof(LiveSwitch))]
    public void Od007_SuppliedFolder_MatchesRealLogLocation()
    {
        const string test = nameof(Od007_SuppliedFolder_MatchesRealLogLocation);
        var supplied = new WindowsEveLogFolder().Path;
        LiveReport.Write(test, "supplied-folder-exists", Directory.Exists(supplied));
        Assert.True(Directory.Exists(supplied));

        var realFolder = Path.GetDirectoryName(LiveSession.LocalOf(LiveSession.FindLivePilot()).FilePath)!;
        var sameFolder = string.Equals(
            Path.GetFullPath(supplied).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(realFolder).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        LiveReport.Write(test, "supplied-folder-equals-live-local-log-folder", sameFolder);

        var probe = new WindowsLogLockProbe();
        var held = new List<string>();
        var ordinal = 0;
        foreach (var file in Directory.EnumerateFiles(supplied, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
        {
            var state = probe.Probe(file);
            if (ordinal++ < 20 || state != LogLockState.NotHeld)
            {
                LiveReport.Write(test, $"file#{ordinal}", $"{state}, name-prefix={Path.GetFileName(file).Split('_')[0]}");
            }

            if (state == LogLockState.Held)
            {
                held.Add(file);
            }
        }

        LiveReport.Write(test, "files-probed", ordinal);
        LiveReport.Write(test, "held-files-in-supplied-folder", held.Count);
        Assert.True(sameFolder);
        Assert.NotEmpty(held);
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.Enabled), SkipType = typeof(LiveSwitch))]
    public void Od007_RealLogs_EncodingFileNameHeaderAndFirstLineStructure()
    {
        const string test = nameof(Od007_RealLogs_EncodingFileNameHeaderAndFirstLineStructure);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var otherFirstSpeakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localFiles = new List<(string Path, long Length)>();
        var folder = new WindowsEveLogFolder().Path;
        var limit = LiveSwitch.IntSetting("SAYWHAT_LIVE_SCAN_LIMIT", DefaultScanSample);
        var all = Directory.EnumerateFiles(folder, "*.txt").Order(StringComparer.Ordinal).ToList();
        var stride = Math.Max(1, all.Count / Math.Max(1, limit));
        LiveReport.Write(test, "files-in-folder", all.Count);

        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        foreach (var path in all.Where((_, index) => index % stride == 0))
        {
            Count("files");
            ScanFile(path, Count, otherFirstSpeakers, localFiles);
        }

        foreach (var (key, value) in counts)
        {
            LiveReport.Write(test, key, value);
        }

        LiveReport.Write(test, "distinct-first-line-speakers-not-the-seed-system-sender", otherFirstSpeakers.Count);
        Assert.True(counts.GetValueOrDefault("files") > 0);
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.Enabled), SkipType = typeof(LiveSwitch))]
    public void Od007_RealLocalLog_UnparseableLineCountAndKind()
    {
        const string test = nameof(Od007_RealLocalLog_UnparseableLineCountAndKind);
        var folder = new WindowsEveLogFolder().Path;
        var probe = new WindowsLogLockProbe();
        var candidate = Directory.EnumerateFiles(folder, "Local_*.txt")
            .Select(p => new FileInfo(p))
            .Where(f => f.Length is > 0 and <= MaxFullParseBytes)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault(f => probe.Probe(f.FullName) == LogLockState.NotHeld);
        Assert.NotNull(candidate);

        var lines = LiveSession.ReadAllLines(candidate.FullName);
        var parser = new LogLineParser();
        var result = parser.Parse(lines);

        LiveReport.Write(test, "file-bytes-bucket", $"<= {Bucket(candidate.Length)}");
        LiveReport.Write(test, "decoded-lines", lines.Count);
        LiveReport.Write(test, "parsed-lines", result.Lines.Count);
        foreach (var kind in Enum.GetValues<LineKind>())
        {
            LiveReport.Write(test, $"kind.{kind}", result.Lines.Count(l => l.Kind == kind));
        }

        LiveReport.Write(test, "warnings", result.Warnings.Count);

        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in result.Lines.Where(l => l.Kind == LineKind.Unparseable))
        {
            var shape = ShapeOf(line.Text);
            shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
        }

        foreach (var (shape, count) in shapes)
        {
            LiveReport.Write(test, $"unparseable.{shape}", count);
        }

        Assert.NotNull(parser.Header);
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.TypingStep), SkipType = typeof(LiveSwitch))]
    public async Task Od017_TypedLocalLines_DetectionMinusEveTimestamp()
    {
        const string test = nameof(Od017_TypedLocalLines_DetectionMinusEveTimestamp);
        var wanted = LiveSwitch.IntSetting("SAYWHAT_LIVE_LINES", DefaultTypedLines);
        var wait = TimeSpan.FromSeconds(LiveSwitch.IntSetting("SAYWHAT_LIVE_WAIT_SECONDS", DefaultWaitSeconds));
        var pilot = LiveSession.FindLivePilot();
        var local = LiveSession.LocalOf(pilot);

        var parser = new LogLineParser();
        parser.Parse(LiveSession.ReadAllLines(local.FilePath));
        Assert.NotNull(parser.Header);

        var delays = new List<double>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = LiveSession.NewWatcher();
        watcher.LinesAvailable += lines =>
        {
            var detected = DateTimeOffset.UtcNow;
            var parsed = parser.Parse(lines);
            lock (delays)
            {
                foreach (var line in parsed.Lines.Where(l => l.Kind == LineKind.OwnLine))
                {
                    delays.Add((detected - line.TimestampUtc!.Value).TotalSeconds);
                }

                if (delays.Count >= wanted)
                {
                    done.TrySetResult();
                }
            }
        };

        watcher.Follow(local);
        LiveReport.Write(test, "waiting-for-typed-lines", wanted);
        var finished = await Task.WhenAny(done.Task, Task.Delay(wait, TestContext.Current.CancellationToken));
        watcher.Stop();

        List<double> sorted;
        lock (delays)
        {
            sorted = delays.Order().ToList();
        }

        LiveReport.Write(test, "lines-detected", sorted.Count);
        if (sorted.Count > 0)
        {
            LiveReport.Write(test, "delay-s-per-line", string.Join(", ", sorted.Select(d => d.ToString("F1", CultureInfo.InvariantCulture))));
            LiveReport.Write(test, "delay-s-min", sorted[0].ToString("F1", CultureInfo.InvariantCulture));
            LiveReport.Write(test, "delay-s-median", LiveSession.Median(sorted).ToString("F1", CultureInfo.InvariantCulture));
            LiveReport.Write(test, "delay-s-max", sorted[^1].ToString("F1", CultureInfo.InvariantCulture));
        }

        LiveReport.Write(test, "caveat", "detection time is the PC clock in UTC; the EVE timestamp is the client's UTC time at one-second resolution, so the PC clock offset and the timestamp rounding both fall inside the delay");
        Assert.True(finished == done.Task, "Fewer typed Local lines than expected arrived before the wait ended.");
    }

    [Fact(Skip = "Live validation: set SAYWHAT_LIVE=1 (and SAYWHAT_LIVE_STEP for interactive steps)", SkipUnless = nameof(LiveSwitch.JumpStep), SkipType = typeof(LiveSwitch))]
    public async Task Od007_SystemJump_SystemChangeLineDetected()
    {
        const string test = nameof(Od007_SystemJump_SystemChangeLineDetected);
        var wait = TimeSpan.FromSeconds(LiveSwitch.IntSetting("SAYWHAT_LIVE_WAIT_SECONDS", DefaultWaitSeconds));
        var pilot = LiveSession.FindLivePilot();
        var local = LiveSession.LocalOf(pilot);

        var header = new LogLineParser();
        header.Parse(LiveSession.ReadAllLines(local.FilePath));
        Assert.NotNull(header.Header);

        var seen = new TaskCompletionSource<ParsedLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = LiveSession.NewWatcher();
        watcher.LinesAvailable += lines =>
        {
            var system = header.Parse(lines).Lines.FirstOrDefault(l => l.Kind == LineKind.SystemMessage);
            if (system is not null)
            {
                seen.TrySetResult(system);
            }
        };

        watcher.Follow(local);
        LiveReport.Write(test, "waiting-for-system-jump", true);

        var finished = await Task.WhenAny(seen.Task, Task.Delay(wait, TestContext.Current.CancellationToken));
        watcher.Stop();
        Assert.True(finished == seen.Task, "No system line arrived in Local before the wait ended.");
        var jump = await seen.Task;

        var (prefix, systemName) = SplitSystemLine(jump.Text);
        LiveReport.Write(test, "system-sender-is-seed-name", string.Equals(jump.Speaker, LogLineParser.SeedSystemSender, StringComparison.OrdinalIgnoreCase));
        LiveReport.Write(test, "system-line-pattern", $"[ timestamp ] <system sender> > {prefix}{{system}}");
        LiveReport.Write(test, "system-line-kind", jump.Kind);

        var detecting = new LogLineParser(knownSolarSystems: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { systemName });
        var parsed = detecting.Parse(LiveSession.ReadAllLines(local.FilePath));
        var changes = parsed.SystemChanges.Count;
        LiveReport.Write(test, "system-changes-detected-in-local-log-for-that-name", changes);
        Assert.True(changes >= 1);
        Assert.Equal(LineKind.SystemMessage, jump.Kind);
    }

    private static (string Prefix, string SystemName) SplitSystemLine(string text)
    {
        var trimmed = text.TrimEnd();
        var cut = trimmed.LastIndexOf(':');
        if (cut < 0)
        {
            cut = trimmed.LastIndexOf(' ');
        }

        return cut < 0
            ? (string.Empty, trimmed)
            : (trimmed[..(cut + 1)] + " ", trimmed[(cut + 1)..].Trim());
    }

    private static void ScanFile(
        string path,
        Action<string> count,
        HashSet<string> otherFirstSpeakers,
        List<(string Path, long Length)> localFiles)
    {
        byte[] bytes;
        long length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            var buffer = new byte[HeaderScanBytes];
            bytes = buffer[..stream.Read(buffer, 0, buffer.Length)];
        }
        catch (IOException)
        {
            count("unreadable");
            return;
        }

        var nameMatch = FileNamePattern().Match(Path.GetFileName(path));
        count(nameMatch.Success
            ? nameMatch.Groups["id"].Success ? "name.channel_date_time_id" : "name.channel_date_time"
            : "name.no-match");

        var encoding = LogTextDecoder.DetectEncoding(bytes);
        count($"encoding.{(encoding.PreambleLength == 0 ? "no-bom-utf8" : encoding.Encoding.WebName)}.bom-bytes-{encoding.PreambleLength}");

        var lines = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding).Lines;
        var leadingBlank = lines.TakeWhile(l => l.Text.Trim(ByteOrderMarkChar).Trim().Length == 0).Count();
        count($"header.leading-blank-lines-{leadingBlank}");

        var parser = new LogLineParser();
        var parsed = parser.Parse(lines);
        if (parser.Header is not { } header)
        {
            count("header.unreadable");
            return;
        }

        count($"header.labels.{string.Join("|", HeaderLabels(lines))}");
        var isLocal = header.ChannelId.StartsWith("local", StringComparison.OrdinalIgnoreCase);
        count(isLocal ? "channel.local" : "channel.non-local");
        count($"channel-id-shape.{(isLocal ? "local-prefix" : header.ChannelId.All(char.IsAsciiDigit) ? "digits" : "other")}");
        if (isLocal)
        {
            localFiles.Add((path, length));
        }

        if (nameMatch.Success && TryNameStart(nameMatch, out var nameStart) && header.SessionStartUtc is { } headerStart)
        {
            count(nameStart == headerStart ? "session-start.header-equals-file-name" : "session-start.header-differs-from-file-name");
        }
        else
        {
            count("session-start.not-comparable");
        }

        var klass = isLocal ? "local" : "non-local";
        var first = parsed.Lines.FirstOrDefault();
        if (first is null)
        {
            count($"{klass}.header-only");
            return;
        }

        count($"{klass}.first-line.{first.Kind}");
        if (first.Kind == LineKind.OtherPilot && first.Speaker is { } speaker)
        {
            otherFirstSpeakers.Add(speaker);
        }

        if (first.Kind == LineKind.SystemMessage)
        {
            count($"{klass}.first-line.system-sender-is-seed-name");
        }

        var timestamped = lines.Select(l => l.Text).Where(t => t.TrimStart(ByteOrderMarkChar).StartsWith('[')).ToList();
        if (timestamped.Count > 0)
        {
            count(timestamped.All(t => t.StartsWith(ByteOrderMarkChar)) ? "lines.all-start-with-feff" : "lines.some-without-feff");
        }
    }

    private static List<string> HeaderLabels(IReadOnlyList<DecodedLine> lines)
    {
        var labels = new List<string>();
        var separators = 0;
        foreach (var raw in lines)
        {
            var text = raw.Text.Trim(ByteOrderMarkChar).Trim();
            if (text.Length >= 3 && text.All(c => c == '-'))
            {
                if (++separators == 2)
                {
                    break;
                }

                continue;
            }

            var colon = text.IndexOf(':');
            if (separators == 1 && colon > 0)
            {
                labels.Add(text[..colon].Trim());
            }
        }

        return labels;
    }

    private static bool TryNameStart(Match match, out DateTimeOffset start) =>
        DateTimeOffset.TryParseExact(
            match.Groups["date"].Value + match.Groups["time"].Value,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out start);

    private static string ShapeOf(string text)
    {
        var trimmed = text.Trim(ByteOrderMarkChar).Trim();
        if (trimmed.Length == 0)
        {
            return "blank";
        }

        return trimmed.StartsWith('[') ? "bracketed-bad-timestamp-or-no-sender-separator" : "no-leading-bracket";
    }

    private static long Bucket(long bytes)
    {
        long bucket = 1024;
        while (bucket < bytes)
        {
            bucket *= 2;
        }

        return bucket;
    }

    [GeneratedRegex(@"^(?<channel>.+)_(?<date>\d{8})_(?<time>\d{6})(?:_(?<id>\d+))?\.txt$")]
    private static partial Regex FileNamePattern();
}
