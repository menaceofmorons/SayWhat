using System.Threading.Channels;
using SayWhat.Core.LanguageDetection;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;
using SayWhat.Core.ReferenceData;
using SayWhat.Core.TermProtection;

namespace SayWhat.Core.Pipeline;

public sealed class PipelineFront : IAsyncDisposable
{
    public const string Component = "PipelineFront";

    public const string ShipNameLanguage = ReferenceDatabase.DefaultShipLanguage;

    public static readonly TimeSpan QueueWaitMax = TimeSpan.FromSeconds(30);

    private readonly SessionLineStore store;
    private readonly ILanguageDetector detector;
    private readonly ReferenceDatabase? reference;
    private readonly string targetLanguage;
    private readonly bool translationEnabled;
    private readonly Action<PendingTranslation> handOff;
    private readonly TimeProvider clock;
    private readonly ShipNameMatcher shipNames;
    private readonly Channel<ParsedLine> channel = Channel.CreateUnbounded<ParsedLine>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private bool disposed;

    public PipelineFront(
        SessionLineStore store,
        ILanguageDetector detector,
        ReferenceDatabase? reference,
        string targetLanguage,
        bool translationEnabled,
        Action<PendingTranslation> handOff,
        TimeProvider? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.reference = reference;
        this.targetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));
        this.translationEnabled = translationEnabled;
        this.handOff = handOff ?? throw new ArgumentNullException(nameof(handOff));
        this.clock = clock ?? TimeProvider.System;
        shipNames = new ShipNameMatcher(LoadShipNames(reference));
        worker = Task.Run(RunAsync);
    }

    public event Action<SessionFault>? Faulted;

    public bool Submit(ParsedLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return channel.Writer.TryWrite(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        channel.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        (detector as IDisposable)?.Dispose();
    }

    private static List<string> LoadShipNames(ReferenceDatabase? reference)
    {
        if (reference is null || !reference.Exists)
        {
            return [];
        }

        return reference.ClientLanguages()
            .SelectMany(language => reference.ShipNames(language))
            .Select(entry => entry.Name)
            .ToList();
    }

    private async Task RunAsync()
    {
        await foreach (var parsed in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Process(parsed);
            }
            catch (Exception exception)
            {
                Recover(parsed);
                RaiseFault(exception);
            }
        }
    }

    private void Recover(ParsedLine parsed)
    {
        try
        {
            if (!store.TryGet(parsed.LineId, out _))
            {
                store.Add(Line.FromParsed(parsed, LineState.LanguageUnknown));
                return;
            }

            store.Update(parsed.LineId, line => line.State == LineState.Pending ? line with { State = LineState.Failed } : line);
        }
        catch (Exception)
        {
            return;
        }
    }

    private void Process(ParsedLine parsed)
    {
        if (parsed.Kind != LineKind.OtherPilot)
        {
            store.Add(Line.FromParsed(parsed, LineState.PassThrough));
            return;
        }

        var outcome = DetectionRunner.Run(
            parsed.Text,
            targetLanguage,
            detector,
            translationEnabled,
            shipNames,
            DetectionRunner.DefaultConfidenceCutoff);

        switch (outcome.Kind)
        {
            case DetectionOutcomeKind.NotRun:
                store.Add(Line.FromParsed(parsed, LineState.PassThroughTranslationOff));
                break;
            case DetectionOutcomeKind.Unknown:
                store.Add(Line.FromParsed(parsed, LineState.LanguageUnknown));
                break;
            case DetectionOutcomeKind.SameLanguage:
                store.Add(Line.FromParsed(parsed, LineState.SameLanguage) with
                {
                    LanguageCode = outcome.LanguageCode,
                    LanguageConfidence = outcome.Confidence,
                });
                break;
            default:
                Enqueue(parsed, outcome);
                break;
        }
    }

    private void Enqueue(ParsedLine parsed, DetectionOutcome outcome)
    {
        var language = outcome.LanguageCode!;
        var line = Line.FromParsed(parsed, LineState.Pending) with
        {
            LanguageCode = language,
            LanguageConfidence = outcome.Confidence,
            Deadline = clock.GetUtcNow() + QueueWaitMax,
        };

        if (!store.Add(line))
        {
            return;
        }

        var protectedLine = TermProtector.Protect(parsed.Text, language, targetLanguage, reference, ShipNameLanguage);
        handOff(new PendingTranslation(line, protectedLine, language, targetLanguage));
    }

    private void RaiseFault(Exception exception)
    {
        try
        {
            Faulted?.Invoke(new SessionFault(Component, exception.GetType().Name));
        }
        catch (Exception)
        {
            return;
        }
    }
}
