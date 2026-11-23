using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace SayWhat.Engines.Lite;

public sealed class LiteTokeniser
{
    private const string EndOfSentencePiece = "</s>";
    private const string PadPiece = "<pad>";
    private const string UnknownPiece = "<unk>";

    private readonly SentencePieceTokenizer sourceTokeniser;
    private readonly SentencePieceTokenizer targetTokeniser;
    private readonly IReadOnlyDictionary<string, int> pieceToId;
    private readonly IReadOnlyDictionary<int, string> idToPiece;

    public LiteTokeniser(
        SentencePieceTokenizer sourceTokeniser,
        SentencePieceTokenizer targetTokeniser,
        IReadOnlyDictionary<string, int> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(sourceTokeniser);
        ArgumentNullException.ThrowIfNull(targetTokeniser);
        ArgumentNullException.ThrowIfNull(vocabulary);
        this.sourceTokeniser = sourceTokeniser;
        this.targetTokeniser = targetTokeniser;
        pieceToId = vocabulary;
        idToPiece = vocabulary.ToDictionary(pair => pair.Value, pair => pair.Key);
        EndOfSentenceId = RequireId(EndOfSentencePiece);
        UnknownId = RequireId(UnknownPiece);
        PadId = RequireId(PadPiece);
    }

    public int EndOfSentenceId { get; }

    public int UnknownId { get; }

    public int PadId { get; }

    public static LiteTokeniser Load(string sourceModelPath, string targetModelPath, string vocabularyPath)
    {
        using var sourceStream = File.OpenRead(sourceModelPath);
        using var targetStream = File.OpenRead(targetModelPath);
        using var vocabularyStream = File.OpenRead(vocabularyPath);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabularyStream)
            ?? throw new InvalidDataException("vocab.json is empty");

        return new LiteTokeniser(
            SentencePieceTokenizer.Create(sourceStream, addBeginningOfSentence: false, addEndOfSentence: false),
            SentencePieceTokenizer.Create(targetStream, addBeginningOfSentence: false, addEndOfSentence: false),
            vocabulary);
    }

    public IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = new List<int>();
        if (text.Length > 0)
        {
            foreach (var token in sourceTokeniser.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false))
            {
                ids.Add(pieceToId.TryGetValue(token.Value, out var id) ? id : UnknownId);
            }
        }

        ids.Add(EndOfSentenceId);
        return ids;
    }

    public string Decode(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var targetIds = new List<int>();
        foreach (var id in ids)
        {
            if (id == EndOfSentenceId || id == PadId || !idToPiece.TryGetValue(id, out var piece))
            {
                continue;
            }

            targetIds.Add(targetTokeniser.Vocabulary.TryGetValue(piece, out var targetId) ? targetId : targetTokeniser.UnknownId);
        }

        return targetTokeniser.Decode(targetIds);
    }

    private int RequireId(string piece) =>
        pieceToId.TryGetValue(piece, out var id)
            ? id
            : throw new InvalidDataException($"vocab.json has no {piece}");
}
