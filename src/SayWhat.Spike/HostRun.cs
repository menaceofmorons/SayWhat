using SayWhat.Core.Engines;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;
using SayWhat.Core.Pipeline;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Spike;

public sealed record HostRunOptions(string? Pilot, string Channel, string Target, bool TranslationEnabled)
{
    public const string DefaultChannel = "Local";
}

public sealed class HostRun(
    LogWatcher watcher,
    ILanguageDetector detector,
    ReferenceDatabase? reference,
    IEngine engine,
    TextWriter output,
    TimeSpan? printWaitMax = null)
{
    public async Task<int> RunAsync(HostRunOptions options, CancellationToken stop)
    {
        var channel = SelectChannel(options);
        if (channel is null)
        {
            return 1;
        }

        var writer = TextWriter.Synchronized(output);
        var store = new SessionLineStore();
        using var printer = new OrderedPrinter(store, writer, printWaitMax ?? PipelineFront.QueueWaitMax);
        var stage = new TranslateStage(store, engine);
        var front = new PipelineFront(store, detector, reference, options.Target, options.TranslationEnabled, pending => stage.Submit(pending));
        using var ingest = new SessionIngest(
            watcher,
            channel,
            line => front.Submit(line),
            warning => writer.WriteLine($"[host] warning {warning.Kind}{LineSuffix(warning.LineId)}"));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void PrintFault(SessionFault fault) => writer.WriteLine($"[host] fault {fault.Component} {fault.ExceptionType}");
        ingest.Faulted += PrintFault;
        front.Faulted += PrintFault;
        stage.Faulted += PrintFault;
        ingest.SessionEnded += () => ended.TrySetResult();
        using var stopRegistration = stop.Register(() => ended.TrySetResult());

        var exitCode = 0;
        try
        {
            ingest.Start();
            if (ingest.Header is null)
            {
                writer.WriteLine($"[host] cannot read the {channel.ChannelName} log of {channel.Listener}");
                exitCode = 1;
            }
            else
            {
                writer.WriteLine($"[host] following {channel.ChannelName} of {channel.Listener}");
                await ended.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            ingest.Dispose();
            watcher.Dispose();
            await front.DisposeAsync().ConfigureAwait(false);
            await stage.DisposeAsync().ConfigureAwait(false);
            printer.Complete();
        }

        return exitCode;
    }

    private static string LineSuffix(LineId? lineId) => lineId is { } id ? $" line {id.Sequence}" : string.Empty;

    private LiveChannel? SelectChannel(HostRunOptions options)
    {
        var pilots = watcher.ListLivePilots();
        var matching = pilots
            .Where(pilot => options.Pilot is null || string.Equals(pilot.Listener, options.Pilot, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
        {
            output.WriteLine(options.Pilot is null
                ? "[host] no live EVE pilot found"
                : $"[host] pilot {options.Pilot} is not live");
            return null;
        }

        if (matching.Count > 1)
        {
            output.WriteLine($"[host] several live pilots: {string.Join(", ", matching.Select(pilot => pilot.Listener))}; pass --pilot");
            return null;
        }

        var channel = matching[0].Channels
            .FirstOrDefault(candidate => string.Equals(candidate.ChannelName, options.Channel, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            output.WriteLine($"[host] pilot {matching[0].Listener} has no live channel {options.Channel}");
        }

        return channel;
    }
}
