namespace SayWhat.Engines.Lite;

public sealed class LitePairTranslator : ILitePairTranslator
{
    private readonly OnnxSeq2SeqModel model;
    private readonly LiteInference inference;

    private LitePairTranslator(OnnxSeq2SeqModel model, LiteInference inference)
    {
        this.model = model;
        this.inference = inference;
    }

    public static LitePairTranslator Load(string folder)
    {
        var tokeniser = LiteTokeniser.Load(
            Path.Combine(folder, LiteEngine.SourceTokeniserFile),
            Path.Combine(folder, LiteEngine.TargetTokeniserFile),
            Path.Combine(folder, LiteEngine.VocabularyFile));
        var model = new OnnxSeq2SeqModel(
            Path.Combine(folder, LiteEngine.EncoderFile),
            Path.Combine(folder, LiteEngine.DecoderFile));
        return new LitePairTranslator(model, new LiteInference(tokeniser, model));
    }

    public string Translate(string text) => inference.Translate(text);

    public void Dispose() => model.Dispose();
}
