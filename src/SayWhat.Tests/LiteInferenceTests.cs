using Microsoft.ML.Tokenizers;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public class LiteInferenceTests
{
    private const int EndId = 0;
    private const int PadId = 99;
    private const int VocabularySize = 100;

    private static readonly string[] ModelFiles =
    [
        "source.spm",
        "target.spm",
        "vocab.json",
        "onnx/encoder_model_quantized.onnx",
        "onnx/decoder_model_merged_quantized.onnx",
    ];

    public static TheoryData<string, string, string[]> FixedLines => new()
    {
        { "ru-en", "Привет, как дела?", ["hello", "hi", "how"] },
        { "ru-en", "Сегодня очень хорошая погода.", ["weather"] },
        { "zh-en", "你好,今天天气很好.", ["hello", "day"] },
        { "zh-en", "我想预订一间明天晚上的双人房.", ["room"] },
        { "de-en", "Das Wetter ist heute sehr schön.", ["weather"] },
        { "de-en", "Wir gehen morgen ins Kino.", ["cinema", "movie"] },
        { "en-de", "Good morning, how are you?", ["Morgen", "Guten"] },
        { "en-de", "The weather is very nice today.", ["Wetter"] },
    };

    [Fact]
    public void Greedy_decoding_follows_the_highest_logit_until_end_of_sentence()
    {
        var model = new StubModel([30, 31, 32, EndId]);
        var inference = new LiteInference(Tokeniser(), model);

        Assert.Equal([30, 31, 32], inference.GenerateIds([10, EndId]));
        Assert.Equal([PadId, 30, 31, 32], model.StepInputs);
    }

    [Fact]
    public void Greedy_decoding_never_selects_the_pad_token()
    {
        var model = new StubModel([PadId, EndId], padIsHighest: true);
        var inference = new LiteInference(Tokeniser(), model);

        Assert.Empty(inference.GenerateIds([EndId]));
    }

    [Fact]
    public void Greedy_decoding_stops_at_the_maximum_length()
    {
        var model = new StubModel(Enumerable.Repeat(30, 50).ToArray());
        var inference = new LiteInference(Tokeniser(), model, maxLength: 5);

        Assert.Equal(Enumerable.Repeat(30, 5), inference.GenerateIds([EndId]));
    }

    [Fact]
    public void Translate_detokenises_the_generated_ids()
    {
        var model = new StubModel([30, 31, 32, EndId]);
        var inference = new LiteInference(Tokeniser(), model);

        Assert.Equal("hi there!", inference.Translate("hello"));
    }

    [Fact]
    public void Decoding_is_released_after_generation()
    {
        var model = new StubModel([EndId]);
        new LiteInference(Tokeniser(), model).GenerateIds([EndId]);

        Assert.True(model.Disposed);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_maximum_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteInference(Tokeniser(), new StubModel([EndId]), maxLength: 0));
    }

    [Fact]
    public void Model_construction_fails_cleanly_for_unreadable_model_files()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var encoderPath = Path.Combine(directory.FullName, "encoder.onnx");
            var decoderPath = Path.Combine(directory.FullName, "decoder.onnx");
            File.WriteAllText(encoderPath, "not a model");
            File.WriteAllText(decoderPath, "not a model");

            Assert.ThrowsAny<Exception>(() => new OnnxSeq2SeqModel(encoderPath, decoderPath));
            Assert.ThrowsAny<Exception>(() => new OnnxSeq2SeqModel(Path.Combine(directory.FullName, "missing.onnx"), decoderPath));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [MemberData(nameof(FixedLines))]
    public async Task Live_translation_is_deterministic_and_contains_the_expected_words(string pair, string line, string[] expected)
    {
        var files = await ModelTestCache.TryGetAllAsync(pair, ModelFiles);
        Assert.SkipUnless(files is not null, "model files not in the local test cache");

        var tokeniser = LiteTokeniser.Load(files["source.spm"], files["target.spm"], files["vocab.json"]);
        using var model = new OnnxSeq2SeqModel(
            files["onnx/encoder_model_quantized.onnx"],
            files["onnx/decoder_model_merged_quantized.onnx"]);
        var inference = new LiteInference(tokeniser, model);

        var first = inference.Translate(line);
        var second = inference.Translate(line);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        Assert.True(expected.Any(word => first.Contains(word, StringComparison.OrdinalIgnoreCase)), $"{pair}: {first}");
    }

    private static LiteTokeniser Tokeniser()
    {
        (string Piece, float Score)[] source =
        [
            ("<unk>", 0f), ("<s>", 0f), ("</s>", 0f), ("▁hello", -1f), ("▁", -4f),
        ];
        (string Piece, float Score)[] target =
        [
            ("<unk>", 0f), ("<s>", 0f), ("</s>", 0f), ("▁hi", -1f), ("▁there", -1f), ("!", -2f),
        ];
        var vocabulary = new Dictionary<string, int>
        {
            ["</s>"] = EndId,
            ["<unk>"] = 1,
            ["<pad>"] = PadId,
            ["▁hello"] = 10,
            ["▁hi"] = 30,
            ["▁there"] = 31,
            ["!"] = 32,
        };

        return new LiteTokeniser(Create(source), Create(target), vocabulary);
    }

    private static SentencePieceTokenizer Create((string Piece, float Score)[] pieces) =>
        SentencePieceTokenizer.Create(pieces, unkId: 0, addBeginningOfSentence: false, addEndOfSentence: false);

    private sealed class StubModel(int[] script, bool padIsHighest = false) : ISeq2SeqModel
    {
        public int[] Script { get; } = script;

        public bool PadIsHighest { get; } = padIsHighest;
        public List<int> StepInputs { get; } = [];

        public bool Disposed { get; private set; }

        public ISeq2SeqDecoding Encode(IReadOnlyList<int> inputIds) => new Decoding(this);

        private sealed class Decoding(StubModel owner) : ISeq2SeqDecoding
        {
            public ReadOnlyMemory<float> Step(int tokenId)
            {
                var logits = new float[VocabularySize];
                var target = owner.Script[owner.StepInputs.Count];
                owner.StepInputs.Add(tokenId);
                logits[target] = 1f;
                if (owner.PadIsHighest)
                {
                    logits[PadId] = 5f;
                }

                return logits;
            }

            public void Dispose() => owner.Disposed = true;
        }
    }
}
