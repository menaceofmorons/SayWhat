using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SayWhat.Engines.Lite;

public sealed class OnnxSeq2SeqModel : ISeq2SeqModel, IDisposable
{
    private const string EncoderIdsInput = "input_ids";
    private const string AttentionMaskInput = "attention_mask";
    private const string EncoderStateOutput = "last_hidden_state";
    private const string DecoderIdsInput = "input_ids";
    private const string DecoderMaskInput = "encoder_attention_mask";
    private const string DecoderStateInput = "encoder_hidden_states";
    private const string CacheBranchInput = "use_cache_branch";
    private const string PastPrefix = "past_key_values.";
    private const string PresentPrefix = "present.";
    private const string LogitsOutput = "logits";
    private const string EncoderCacheMarker = ".encoder.";

    private readonly InferenceSession encoder;
    private readonly InferenceSession decoder;
    private readonly IReadOnlyList<CacheEntry> cacheLayout;

    public OnnxSeq2SeqModel(string encoderPath, string decoderPath)
    {
        using var options = new SessionOptions();
        encoder = new InferenceSession(encoderPath, options);
        decoder = new InferenceSession(decoderPath, options);
        cacheLayout = decoder.InputMetadata
            .Where(pair => pair.Key.StartsWith(PastPrefix, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CacheEntry(
                pair.Key,
                PresentPrefix + pair.Key[PastPrefix.Length..],
                pair.Value.Dimensions[1],
                pair.Value.Dimensions[3],
                pair.Key.Contains(EncoderCacheMarker, StringComparison.Ordinal)))
            .ToArray();
        if (cacheLayout.Count == 0 || cacheLayout.Any(entry => entry.Heads <= 0 || entry.HeadSize <= 0))
        {
            throw new InvalidDataException("Decoder cache inputs are not in the expected layout");
        }
    }

    public ISeq2SeqDecoding Encode(IReadOnlyList<int> inputIds)
    {
        ArgumentNullException.ThrowIfNull(inputIds);
        var length = inputIds.Count;
        var ids = new DenseTensor<long>(inputIds.Select(id => (long)id).ToArray(), [1, length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length]);
        using var results = encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor(EncoderIdsInput, ids),
            NamedOnnxValue.CreateFromTensor(AttentionMaskInput, mask),
        ]);
        var state = Copy(results.First(result => result.Name == EncoderStateOutput).AsTensor<float>());
        return new Decoding(this, mask, state);
    }

    public void Dispose()
    {
        encoder.Dispose();
        decoder.Dispose();
    }

    private static DenseTensor<float> Copy(Tensor<float> source) =>
        new(source.ToArray(), source.Dimensions.ToArray());

    private sealed record CacheEntry(string Past, string Present, int Heads, int HeadSize, bool FromEncoder);

    private sealed class Decoding(OnnxSeq2SeqModel owner, DenseTensor<long> mask, DenseTensor<float> state) : ISeq2SeqDecoding
    {
        private Dictionary<string, DenseTensor<float>>? cache;

        public ReadOnlyMemory<float> Step(int tokenId)
        {
            var useCache = cache is not null;
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(DecoderIdsInput, new DenseTensor<long>(new[] { (long)tokenId }, [1, 1])),
                NamedOnnxValue.CreateFromTensor(DecoderMaskInput, mask),
                NamedOnnxValue.CreateFromTensor(DecoderStateInput, state),
                NamedOnnxValue.CreateFromTensor(CacheBranchInput, new DenseTensor<bool>(new[] { useCache }, [1])),
            };
            foreach (var entry in owner.cacheLayout)
            {
                var tensor = cache is not null
                    ? cache[entry.Past]
                    : new DenseTensor<float>(new[] { 1, entry.Heads, 0, entry.HeadSize });
                inputs.Add(NamedOnnxValue.CreateFromTensor(entry.Past, tensor));
            }

            using var results = owner.decoder.Run(inputs);
            var next = new Dictionary<string, DenseTensor<float>>();
            foreach (var entry in owner.cacheLayout)
            {
                next[entry.Past] = useCache && entry.FromEncoder
                    ? cache![entry.Past]
                    : Copy(results.First(result => result.Name == entry.Present).AsTensor<float>());
            }

            cache = next;
            var logits = results.First(result => result.Name == LogitsOutput).AsTensor<float>();
            var vocabulary = logits.Dimensions[^1];
            var all = logits.ToArray();
            return all.AsMemory(all.Length - vocabulary);
        }

        public void Dispose()
        {
            cache = null;
        }
    }
}
