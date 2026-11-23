using System.Text.Json;
using Microsoft.ML.Tokenizers;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public class LiteTokeniserTests
{
    private static readonly string[] TokeniserFiles = ["source.spm", "target.spm", "vocab.json", "tokenizer.json"];

    private static readonly (string Piece, float Score)[] SyntheticSourcePieces =
    [
        ("<unk>", 0f), ("<s>", 0f), ("</s>", 0f),
        ("▁hello", -1f), ("▁world", -1f), ("▁h", -3f), ("e", -3f), ("llo", -3f),
        ("▁", -4f), ("w", -4f), ("o", -4f), ("r", -4f), ("l", -4f), ("d", -4f),
    ];

    private static readonly (string Piece, float Score)[] SyntheticTargetPieces =
    [
        ("<unk>", 0f), ("<s>", 0f), ("</s>", 0f),
        ("▁hi", -1f), ("▁there", -1f), ("!", -2f),
    ];

    public static TheoryData<string, string> FixedLines => new()
    {
        { "ru-en", "Привет" },
        { "ru-en", "Я" },
        { "ru-en", "Как дела? Сегодня очень хорошая погода." },
        { "ru-en", "Москва — столица России, а Санкт-Петербург — культурная столица." },
        { "zh-en", "你" },
        { "zh-en", "你好" },
        { "zh-en", "今天天气很好,我们去公园散步吧." },
        { "zh-en", "我想预订一间明天晚上的双人房" },
        { "ja-en", "あ" },
        { "ja-en", "こんにちは" },
        { "ja-en", "今日はとても良い天気ですね" },
        { "ja-en", "私は昨日、友達と一緒に映画を見に行きました" },
        { "ko-en", "네" },
        { "ko-en", "안녕하세요" },
        { "ko-en", "오늘 날씨가 정말 좋네요" },
        { "ko-en", "저는 어제 친구와 함께 영화를 보러 갔습니다" },
        { "de-en", "Ja" },
        { "de-en", "Guten Morgen" },
        { "de-en", "Das Wetter ist heute sehr schön, wir gehen spazieren." },
        { "de-en", "Übermorgen fahren wir nach Österreich, um die Großeltern zu besuchen." },
    };

    [Fact]
    public void Encode_maps_source_pieces_through_the_vocabulary_and_ends_with_end_of_sentence()
    {
        var tokeniser = Synthetic();

        var ids = tokeniser.Encode("hello world");

        Assert.Equal([10, 20, 0], ids);
    }

    [Fact]
    public void Encode_of_empty_text_is_only_end_of_sentence()
    {
        Assert.Equal([0], Synthetic().Encode(string.Empty));
    }

    [Fact]
    public void Encode_maps_a_piece_missing_from_the_vocabulary_to_unknown()
    {
        var tokeniser = Synthetic();

        var ids = tokeniser.Encode("hello z");

        Assert.Equal(10, ids[0]);
        Assert.Contains(tokeniser.UnknownId, ids.Skip(1));
        Assert.Equal(0, ids[^1]);
    }

    [Fact]
    public void Decode_joins_target_pieces_and_drops_end_of_sentence_and_padding()
    {
        var tokeniser = Synthetic();

        var text = tokeniser.Decode([30, 31, 32, 0, 99]);

        Assert.Equal("hi there!", text);
    }

    [Fact]
    public void Decode_of_only_end_of_sentence_is_empty()
    {
        Assert.Equal(string.Empty, Synthetic().Decode([0]));
    }

    [Fact]
    public void Load_rejects_a_vocabulary_without_the_special_pieces()
    {
        var source = Tokeniser(SyntheticSourcePieces);
        var target = Tokeniser(SyntheticTargetPieces);

        Assert.Throws<InvalidDataException>(() => new LiteTokeniser(source, target, new Dictionary<string, int> { ["a"] = 1 }));
    }

    [Theory]
    [MemberData(nameof(FixedLines))]
    public async Task Live_encoding_matches_the_repository_tokenizer_json_ids(string pair, string line)
    {
        var files = await ModelTestCache.TryGetAllAsync(pair, TokeniserFiles);
        Assert.SkipUnless(files is not null, "model files not in the local test cache");

        var tokeniser = LiteTokeniser.Load(files["source.spm"], files["target.spm"], files["vocab.json"]);
        var reference = new ReferenceUnigram(files["tokenizer.json"]);

        Assert.Equal(reference.Encode(line), tokeniser.Encode(line));
    }

    [Theory]
    [InlineData("ru-en", "Hello, how are you today?")]
    [InlineData("zh-en", "I would like to book a double room for tomorrow night.")]
    [InlineData("ja-en", "Yesterday I went to the cinema with a friend.")]
    [InlineData("ko-en", "The weather is really nice today.")]
    [InlineData("de-en", "Good morning, we are going for a walk.")]
    public async Task Live_decoding_round_trips_target_language_text(string pair, string line)
    {
        var files = await ModelTestCache.TryGetAllAsync(pair, TokeniserFiles);
        Assert.SkipUnless(files is not null, "model files not in the local test cache");

        var tokeniser = LiteTokeniser.Load(files["source.spm"], files["target.spm"], files["vocab.json"]);
        var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(files["vocab.json"]))!;
        using var targetStream = File.OpenRead(files["target.spm"]);
        var target = SentencePieceTokenizer.Create(targetStream, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = target.EncodeToTokens(line, out _, addBeginningOfSentence: false, addEndOfSentence: false)
            .Select(token => vocabulary[token.Value])
            .Append(tokeniser.EndOfSentenceId)
            .ToArray();

        Assert.Equal(line, tokeniser.Decode(ids));
    }

    private static LiteTokeniser Synthetic()
    {
        var vocabulary = new Dictionary<string, int>
        {
            ["</s>"] = 0,
            ["<unk>"] = 1,
            ["<pad>"] = 99,
            ["▁hello"] = 10,
            ["▁world"] = 20,
            ["▁hi"] = 30,
            ["▁there"] = 31,
            ["!"] = 32,
        };

        return new LiteTokeniser(Tokeniser(SyntheticSourcePieces), Tokeniser(SyntheticTargetPieces), vocabulary);
    }

    private static SentencePieceTokenizer Tokeniser((string Piece, float Score)[] pieces) =>
        SentencePieceTokenizer.Create(pieces, unkId: 0, addBeginningOfSentence: false, addEndOfSentence: false);

    private sealed class ReferenceUnigram
    {
        private const char SpaceMarker = '▁';

        private readonly Dictionary<string, (int Id, double Score)> pieces = new();
        private readonly Dictionary<int, string> byId = new();
        private readonly int unknownId;
        private readonly int endOfSentenceId;

        public ReferenceUnigram(string tokenizerJsonPath)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
            var model = document.RootElement.GetProperty("model");
            unknownId = model.GetProperty("unk_id").GetInt32();
            var index = 0;
            foreach (var entry in model.GetProperty("vocab").EnumerateArray())
            {
                var piece = entry[0].GetString()!;
                pieces[piece] = (index, entry[1].GetDouble());
                byId[index] = piece;
                index++;
            }

            endOfSentenceId = pieces["</s>"].Id;
        }

        public IReadOnlyList<int> Encode(string text)
        {
            var ids = new List<int>();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                ids.AddRange(EncodeWord(SpaceMarker + word));
            }

            ids.Add(endOfSentenceId);
            return ids;
        }

        public string Decode(IEnumerable<int> ids) =>
            string.Concat(ids.Where(id => id != endOfSentenceId && byId.ContainsKey(id)).Select(id => byId[id]))
                .Replace(SpaceMarker, ' ')
                .Trim();

        private List<int> EncodeWord(string word)
        {
            var best = new double[word.Length + 1];
            var from = new int[word.Length + 1];
            var id = new int[word.Length + 1];
            Array.Fill(best, double.NegativeInfinity);
            best[0] = 0;
            for (var end = 1; end <= word.Length; end++)
            {
                for (var start = 0; start < end; start++)
                {
                    if (double.IsNegativeInfinity(best[start]))
                    {
                        continue;
                    }

                    var candidate = word[start..end];
                    var (pieceId, score) = pieces.TryGetValue(candidate, out var known)
                        ? known
                        : end - start == 1 ? (unknownId, -10.0) : (-1, double.NegativeInfinity);
                    if (pieceId == -1 || best[start] + score <= best[end])
                    {
                        continue;
                    }

                    best[end] = best[start] + score;
                    from[end] = start;
                    id[end] = pieceId;
                }
            }

            var result = new List<int>();
            for (var position = word.Length; position > 0; position = from[position])
            {
                result.Add(id[position]);
            }

            result.Reverse();
            return result;
        }
    }
}
