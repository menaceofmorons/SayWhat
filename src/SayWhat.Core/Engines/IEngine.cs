namespace SayWhat.Core.Engines;

public interface IEngine
{
    EngineDescription Describe();

    Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation);

    Task<TranslationResult> Translate(
        string protectedText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellation);

    Task Release();
}
