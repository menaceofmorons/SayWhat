namespace SayWhat.Core.Lines;

public enum LineChangeKind
{
    Added,
    Updated,
}

public sealed record LineChange(LineChangeKind Kind, Line Line);

public enum LineUpdateResult
{
    Applied,
    NotFound,
    RefusedFinal,
    DiscardedAfterFailed,
    RefusedTransition,
}
