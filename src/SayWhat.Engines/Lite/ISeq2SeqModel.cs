namespace SayWhat.Engines.Lite;

public interface ISeq2SeqModel
{
    ISeq2SeqDecoding Encode(IReadOnlyList<int> inputIds);
}

public interface ISeq2SeqDecoding : IDisposable
{
    ReadOnlyMemory<float> Step(int tokenId);
}
