using System.Threading.Channels;
using SayWhat.Core.Engines;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;
using SayWhat.Core.TermProtection;

namespace SayWhat.Core.Pipeline;

public sealed class TranslateStage : IAsyncDisposable
{
    public const string Component = "TranslateStage";

    private readonly SessionLineStore store;
    private readonly IEngine engine;
    private readonly TimeProvider clock;
    private readonly Channel<PendingTranslation> channel = Channel.CreateUnbounded<PendingTranslation>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task worker;
    private bool disposed;

    public TranslateStage(SessionLineStore store, IEngine engine, TimeProvider? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.clock = clock ?? TimeProvider.System;
        worker = Task.Run(RunAsync);
    }

    public event Action<SessionFault>? Faulted;

    public event Action<TranslateOutcome>? Completed;

    public bool Submit(PendingTranslation pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return channel.Writer.TryWrite(pending);
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

        try
        {
            await engine.Release().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RaiseFault(exception);
        }
    }

    private async Task RunAsync()
    {
        await foreach (var pending in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(pending).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseFault(exception);
                Finish(pending, null, EngineErrorCategory.Unavailable);
            }
        }
    }

    private async Task ProcessAsync(PendingTranslation pending)
    {
        var remaining = pending.Line.Deadline is { } deadline ? deadline - clock.GetUtcNow() : (TimeSpan?)null;
        if (remaining <= TimeSpan.Zero)
        {
            Finish(pending, null, EngineErrorCategory.Timeout);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var request = RequestAsync(pending, cancellation.Token);

        if (remaining is { } limit)
        {
            using var expiry = new CancellationTokenSource();
            var timer = Task.Delay(limit, clock, expiry.Token);
            if (await Task.WhenAny(request, timer).ConfigureAwait(false) != request)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                _ = request.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                Finish(pending, null, EngineErrorCategory.Timeout);
                return;
            }

            await expiry.CancelAsync().ConfigureAwait(false);
        }

        var (text, error) = await request.ConfigureAwait(false);
        Finish(pending, text, error);
    }

    private async Task<(string? Text, EngineErrorCategory? Error)> RequestAsync(PendingTranslation pending, CancellationToken token)
    {
        var prepared = await engine.Prepare(pending.SourceLanguage, pending.TargetLanguage, token).ConfigureAwait(false);
        switch (prepared)
        {
            case PrepareResult.NoModel:
                return (null, EngineErrorCategory.NoModel);
            case PrepareResult.NeedsDownload:
                return (null, EngineErrorCategory.Unavailable);
        }

        var result = await engine
            .Translate(pending.Protected.Text, pending.SourceLanguage, pending.TargetLanguage, token)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return (null, result.Error!.Category);
        }

        var restored = TermProtector.Restore(pending.Protected, result.Text);
        return restored.IsSuccess ? (restored.Text, null) : (null, restored.Error!.Category);
    }

    private void Finish(PendingTranslation pending, string? text, EngineErrorCategory? error)
    {
        var state = error is null ? LineState.Translated : LineState.Failed;
        var result = store.Update(pending.Line.LineId, line => line with
        {
            State = state,
            TranslatedText = error is null ? text : null,
        });

        if (result == LineUpdateResult.Applied)
        {
            RaiseCompleted(new TranslateOutcome(pending.Line.LineId, state, error));
        }
    }

    private void RaiseCompleted(TranslateOutcome outcome)
    {
        try
        {
            Completed?.Invoke(outcome);
        }
        catch (Exception exception)
        {
            RaiseFault(exception);
        }
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
