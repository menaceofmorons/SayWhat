namespace SayWhat.Engines.Lite;

public sealed class LiteInference
{
    public const int DefaultMaxLength = 512;

    private readonly LiteTokeniser tokeniser;
    private readonly ISeq2SeqModel model;
    private readonly int maxLength;

    public LiteInference(LiteTokeniser tokeniser, ISeq2SeqModel model, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(tokeniser);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        this.tokeniser = tokeniser;
        this.model = model;
        this.maxLength = maxLength;
    }

    public string Translate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return tokeniser.Decode(GenerateIds(tokeniser.Encode(text)));
    }

    public IReadOnlyList<int> GenerateIds(IReadOnlyList<int> inputIds)
    {
        var generated = new List<int>();
        using var decoding = model.Encode(inputIds);
        var current = tokeniser.PadId;
        while (generated.Count < maxLength)
        {
            var next = ArgMaxExcludingPad(decoding.Step(current).Span);
            if (next == tokeniser.EndOfSentenceId)
            {
                break;
            }

            generated.Add(next);
            current = next;
        }

        return generated;
    }

    private int ArgMaxExcludingPad(ReadOnlySpan<float> logits)
    {
        var best = -1;
        var bestValue = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (i != tokeniser.PadId && logits[i] > bestValue)
            {
                best = i;
                bestValue = logits[i];
            }
        }

        return best >= 0 ? best : throw new InvalidOperationException("Decoder returned no logits");
    }
}
