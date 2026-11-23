using SayWhat.Core.Engines;
using SayWhat.Core.Models;

namespace SayWhat.Engines.Lite;

public sealed class LiteEngine : IEngine
{
    public const string SourceTokeniserFile = "source.spm";
    public const string TargetTokeniserFile = "target.spm";
    public const string VocabularyFile = "vocab.json";
    public const string EncoderFile = "onnx/encoder_model_quantized.onnx";
    public const string DecoderFile = "onnx/decoder_model_merged_quantized.onnx";
    public const int MaxParallelRequests = 1;

    private const string Pivot = "en";

    public static IReadOnlyList<string> RequiredFiles { get; } =
        [SourceTokeniserFile, TargetTokeniserFile, VocabularyFile, EncoderFile, DecoderFile];

    private readonly ModelFolder folder;
    private readonly ILiteModelCatalogue catalogue;
    private readonly Func<ModelHost> hostFactory;
    private readonly Func<string, ILitePairTranslator> loader;
    private readonly TimeSpan requestTimeout;
    private readonly object gate = new();
    private ModelHost? host;

    public LiteEngine(
        ModelFolder folder,
        ILiteModelCatalogue catalogue,
        Func<ModelHost> hostFactory,
        Func<string, ILitePairTranslator> loader,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(hostFactory);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        this.folder = folder;
        this.catalogue = catalogue;
        this.hostFactory = hostFactory;
        this.loader = loader;
        this.requestTimeout = requestTimeout;
    }

    public EngineDescription Describe() =>
        new("Lite", EngineLocality.Local, "characters", false, MaxParallelRequests);

    public async Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation)
    {
        var route = await ResolveRoute(sourceLanguage, targetLanguage, cancellation).ConfigureAwait(false);
        if (route is null)
        {
            return new PrepareResult.NoModel();
        }

        var missing = route.Where(leg => !leg.OnDisk).ToList();
        return missing.Count == 0
            ? new PrepareResult.Ready()
            : new PrepareResult.NeedsDownload(missing.Sum(leg => leg.DownloadBytes));
    }

    public async Task<TranslationResult> Translate(
        string protectedText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        IReadOnlyList<Leg>? route;
        using var routeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        routeTimeout.CancelAfter(requestTimeout);
        try
        {
            route = await ResolveRoute(sourceLanguage, targetLanguage, routeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return TranslationResult.Failure(EngineErrorCategory.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TranslationResult.Failure(EngineErrorCategory.Unavailable, exception.Message);
        }

        if (route is null)
        {
            return TranslationResult.Failure(EngineErrorCategory.NoModel);
        }

        if (route.Any(leg => !leg.OnDisk))
        {
            return TranslationResult.Failure(EngineErrorCategory.Unavailable, "Model not downloaded");
        }

        var modelHost = CurrentHost();
        foreach (var leg in route)
        {
            var path = folder.PathFor(leg.Pair);
            modelHost.Register(KeyFor(leg.Pair), SizeOnDisk(path), _ => Task.FromResult(new LoadedModel(loader(path))));
        }

        try
        {
            return await Task.Run(() => TranslateOnRoute(modelHost, route, protectedText, cancellation), CancellationToken.None)
                .WaitAsync(requestTimeout, cancellation)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return TranslationResult.Failure(EngineErrorCategory.Timeout);
        }
    }

    public Task Release()
    {
        ModelHost? released;
        lock (gate)
        {
            released = host;
            host = null;
        }

        released?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<TranslationResult> TranslateOnRoute(
        ModelHost modelHost,
        IReadOnlyList<Leg> route,
        string text,
        CancellationToken cancellation)
    {
        ModelChain chain;
        try
        {
            chain = await modelHost.AcquireChainAsync(route.Select(leg => KeyFor(leg.Pair)).ToArray(), cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TranslationResult.Failure(EngineErrorCategory.Unavailable, exception.Message);
        }

        using (chain)
        {
            try
            {
                var current = text;
                foreach (var lease in chain.Leases)
                {
                    current = ((ILitePairTranslator)lease.Session).Translate(current);
                }

                return text.Length > 0 && string.IsNullOrWhiteSpace(current)
                    ? TranslationResult.Failure(EngineErrorCategory.BadResponse, "Empty translation")
                    : TranslationResult.Success(current);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return TranslationResult.Failure(EngineErrorCategory.BadResponse, exception.Message);
            }
        }
    }

    private async Task<IReadOnlyList<Leg>?> ResolveRoute(string source, string target, CancellationToken cancellation)
    {
        var direct = await ResolveLeg(new LanguagePair(source, target), cancellation).ConfigureAwait(false);
        if (direct is not null)
        {
            return [direct];
        }

        if (source == Pivot || target == Pivot || source == target)
        {
            return null;
        }

        var first = await ResolveLeg(new LanguagePair(source, Pivot), cancellation).ConfigureAwait(false);
        var second = await ResolveLeg(new LanguagePair(Pivot, target), cancellation).ConfigureAwait(false);
        return first is null || second is null ? null : [first, second];
    }

    private async Task<Leg?> ResolveLeg(LanguagePair pair, CancellationToken cancellation)
    {
        if (folder.IsComplete(pair))
        {
            return new Leg(pair, true, 0);
        }

        var size = await catalogue.FindSizeAsync(pair, cancellation).ConfigureAwait(false);
        return size is null ? null : new Leg(pair, false, size.Value);
    }

    private ModelHost CurrentHost()
    {
        lock (gate)
        {
            return host ??= hostFactory();
        }
    }

    private static string KeyFor(LanguagePair pair) => $"lite/{pair.Source}-{pair.Target}";

    private static long SizeOnDisk(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private sealed record Leg(LanguagePair Pair, bool OnDisk, long DownloadBytes);
}
