using SayWhat.Core.Lines;

namespace SayWhat.Spike;

public sealed class OrderedPrinter : IDisposable
{
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMilliseconds(250);

    private readonly object gate = new();
    private readonly List<Entry> waiting = [];
    private readonly TextWriter output;
    private readonly TimeSpan waitMax;
    private readonly TimeProvider clock;
    private readonly SessionLineStore store;
    private readonly ITimer timer;
    private bool disposed;

    public OrderedPrinter(SessionLineStore store, TextWriter output, TimeSpan waitMax, TimeProvider? clock = null)
    {
        this.store = store;
        this.output = output;
        this.waitMax = waitMax;
        this.clock = clock ?? TimeProvider.System;
        store.Changed += OnChanged;
        timer = this.clock.CreateTimer(_ => Print(flushAll: false), null, SweepPeriod, SweepPeriod);
    }

    public void Complete() => Print(flushAll: true);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        store.Changed -= OnChanged;
        timer.Dispose();
    }

    private void OnChanged(LineChange change)
    {
        lock (gate)
        {
            if (change.Kind == LineChangeKind.Added)
            {
                waiting.Add(new Entry(change.Line, clock.GetUtcNow()));
            }
            else
            {
                var index = waiting.FindIndex(entry => entry.Line.LineId == change.Line.LineId);
                if (index >= 0)
                {
                    waiting[index] = waiting[index] with { Line = change.Line };
                }
            }

            PrintLocked(flushAll: false);
        }
    }

    private void Print(bool flushAll)
    {
        lock (gate)
        {
            PrintLocked(flushAll);
        }
    }

    private void PrintLocked(bool flushAll)
    {
        var now = clock.GetUtcNow();
        while (waiting.Count > 0)
        {
            var head = waiting[0];
            if (!flushAll && !LineStates.IsFinal(head.Line.State) && now - head.AddedAt < waitMax)
            {
                return;
            }

            waiting.RemoveAt(0);
            output.WriteLine(LineLayout.Format(head.Line));
        }
    }

    private sealed record Entry(Line Line, DateTimeOffset AddedAt);
}
