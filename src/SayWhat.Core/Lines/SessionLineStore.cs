using SayWhat.Core.LogIngest;

namespace SayWhat.Core.Lines;

public sealed class SessionLineStore
{
    private readonly object _gate = new();
    private readonly List<Line> _lines = [];
    private readonly Dictionary<LineId, int> _indexById = [];

    public event Action<LineChange>? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    public IReadOnlyList<Line> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }

    public bool TryGet(LineId lineId, out Line line)
    {
        lock (_gate)
        {
            if (_indexById.TryGetValue(lineId, out var index))
            {
                line = _lines[index];
                return true;
            }
        }

        line = null!;
        return false;
    }

    public bool Add(Line line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (!_indexById.TryAdd(line.LineId, _lines.Count))
            {
                return false;
            }

            _lines.Add(line);
            Changed?.Invoke(new LineChange(LineChangeKind.Added, line));
            return true;
        }
    }

    public LineUpdateResult Update(LineId lineId, Func<Line, Line> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (!_indexById.TryGetValue(lineId, out var index))
            {
                return LineUpdateResult.NotFound;
            }

            var current = _lines[index];
            if (current.State == LineState.Failed)
            {
                return LineUpdateResult.DiscardedAfterFailed;
            }

            if (LineStates.IsFinal(current.State))
            {
                return LineUpdateResult.RefusedFinal;
            }

            var updated = change(current);
            if (updated.LineId != lineId || !LineStates.CanTransition(current.State, updated.State))
            {
                return LineUpdateResult.RefusedTransition;
            }

            _lines[index] = updated;
            Changed?.Invoke(new LineChange(LineChangeKind.Updated, updated));
            return LineUpdateResult.Applied;
        }
    }
}
