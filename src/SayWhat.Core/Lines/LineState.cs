namespace SayWhat.Core.Lines;

public enum LineState
{
    PassThrough,
    PassThroughTranslationOff,
    LanguageUnknown,
    SameLanguage,
    Pending,
    Translated,
    Failed,
}

public static class LineStates
{
    public static bool IsFinal(LineState state) => state != LineState.Pending;

    public static bool CanTransition(LineState from, LineState to) =>
        from == LineState.Pending
        && to is LineState.Translated or LineState.Failed;
}
