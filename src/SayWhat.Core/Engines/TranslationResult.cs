namespace SayWhat.Core.Engines;

public sealed record EngineUsage(string Unit, long Amount);

public sealed record EngineError(EngineErrorCategory Category, string? Detail = null);

public sealed record TranslationResult
{
    private TranslationResult(string? text, EngineUsage? usage, EngineError? error)
    {
        Text = text;
        Usage = usage;
        Error = error;
    }

    public string? Text { get; }

    public EngineUsage? Usage { get; }

    public EngineError? Error { get; }

    public bool IsSuccess => Error is null;

    public static TranslationResult Success(string text, EngineUsage? usage = null) =>
        new(text, usage, null);

    public static TranslationResult Failure(EngineErrorCategory category, string? detail = null) =>
        new(null, null, new EngineError(category, detail));
}
