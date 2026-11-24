namespace SayWhat.Core.LogIngest;

public sealed record SessionFault(string Component, string ExceptionType);

public sealed class SessionIngest : IDisposable
{
    public const string IngestComponent = "SessionIngest";
    public const string ConsumerComponent = "LineConsumer";
    public const string WarningComponent = "WarningConsumer";

    private readonly LogWatcher watcher;
    private readonly LiveChannel channel;
    private readonly Action<ParsedLine> onLine;
    private readonly Action<ParseWarning>? onWarning;
    private readonly object gate = new();
    private readonly LogLineParser parser = new();
    private int started;
    private int ended;
    private int disposed;

    public SessionIngest(
        LogWatcher watcher,
        LiveChannel channel,
        Action<ParsedLine> onLine,
        Action<ParseWarning>? onWarning = null)
    {
        this.watcher = watcher;
        this.channel = channel;
        this.onLine = onLine;
        this.onWarning = onWarning;
    }

    public event Action<SessionFault>? Faulted;

    public event Action? SessionEnded;

    public LogHeader? Header => parser.Header;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            return;
        }

        try
        {
            ReadBacklog();
            watcher.LinesAvailable += OnLines;
            watcher.SessionEnded += OnSessionEnded;
            watcher.Follow(channel);
        }
        catch (Exception ex)
        {
            watcher.LinesAvailable -= OnLines;
            watcher.SessionEnded -= OnSessionEnded;
            RaiseFault(IngestComponent, ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        watcher.LinesAvailable -= OnLines;
        watcher.SessionEnded -= OnSessionEnded;
        watcher.Stop();
    }

    private void ReadBacklog()
    {
        byte[] bytes;
        using (var stream = new FileStream(channel.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            bytes = bytes[..read];
        }

        var encoding = LogTextDecoder.DetectEncoding(bytes);
        var chunk = LogTextDecoder.ReadCompleteLines(bytes, encoding.PreambleLength, encoding);
        lock (gate)
        {
            parser.Parse(chunk.Lines);
        }
    }

    private void OnLines(IReadOnlyList<DecodedLine> decoded)
    {
        try
        {
            ParseResult result;
            lock (gate)
            {
                result = parser.Parse(decoded);
            }

            foreach (var warning in result.Warnings)
            {
                Deliver(WarningComponent, () => onWarning?.Invoke(warning));
            }

            foreach (var line in result.Lines)
            {
                Deliver(ConsumerComponent, () => onLine(line));
            }
        }
        catch (Exception ex)
        {
            RaiseFault(IngestComponent, ex);
        }
    }

    private void OnSessionEnded()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1)
        {
            return;
        }

        try
        {
            SessionEnded?.Invoke();
        }
        catch (Exception ex)
        {
            RaiseFault(IngestComponent, ex);
        }
    }

    private void Deliver(string component, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RaiseFault(component, ex);
        }
    }

    private void RaiseFault(string component, Exception exception)
    {
        try
        {
            Faulted?.Invoke(new SessionFault(component, exception.GetType().Name));
        }
        catch (Exception)
        {
        }
    }
}
