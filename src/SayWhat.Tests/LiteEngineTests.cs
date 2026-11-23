using System.Net;
using System.Text;
using SayWhat.Core.Engines;
using SayWhat.Core.Models;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public sealed class LiteEngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "saywhat-lite-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCatalogue catalogue = new();
    private readonly List<ModelHost> hosts = [];
    private readonly ModelFolder folder;

    public LiteEngineTests()
    {
        folder = new ModelFolder(root, LiteEngine.RequiredFiles);
    }

    public void Dispose()
    {
        foreach (var host in hosts)
        {
            host.Dispose();
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Describe_reports_lite_local_characters_without_usage()
    {
        var description = Engine().Describe();

        Assert.Equal("Lite", description.EngineType);
        Assert.Equal(EngineLocality.Local, description.Locality);
        Assert.Equal("characters", description.UsageUnit);
        Assert.False(description.ReportsUsage);
        Assert.Equal(LiteEngine.MaxParallelRequests, description.MaxParallelRequests);
    }

    [Fact]
    public void Lite_exposes_exactly_the_interface_operations()
    {
        Assert.Equal(
            ["Describe", "Prepare", "Release", "Translate"],
            typeof(LiteEngine).GetMethods().Where(m => m.DeclaringType == typeof(LiteEngine) && !m.IsStatic && !m.IsSpecialName)
                .Select(m => m.Name).Order().ToArray());
    }

    [Fact]
    public async Task Prepare_is_ready_when_the_pair_is_on_disk()
    {
        Install("de-en");

        Assert.IsType<PrepareResult.Ready>(await Engine().Prepare("de", "en", default));
    }

    [Fact]
    public async Task Prepare_needs_download_with_the_publisher_size()
    {
        catalogue.Sizes["de-en"] = 1234;

        var result = Assert.IsType<PrepareResult.NeedsDownload>(await Engine().Prepare("de", "en", default));
        Assert.Equal(1234, result.SizeBytes);
    }

    [Fact]
    public async Task Prepare_is_no_model_when_the_pair_is_unpublished()
    {
        Assert.IsType<PrepareResult.NoModel>(await Engine().Prepare("ru", "ko", default));
    }

    [Fact]
    public async Task Prepare_pivot_needs_both_halves()
    {
        Install("de-en");
        Assert.IsType<PrepareResult.NoModel>(await Engine().Prepare("de", "ru", default));

        catalogue.Sizes["en-ru"] = 500;
        var result = Assert.IsType<PrepareResult.NeedsDownload>(await Engine().Prepare("de", "ru", default));
        Assert.Equal(500, result.SizeBytes);

        Install("en-ru");
        Assert.IsType<PrepareResult.Ready>(await Engine().Prepare("de", "ru", default));
    }

    [Fact]
    public async Task Prepare_english_source_without_a_direct_model_is_no_model()
    {
        Install("en-de");

        Assert.IsType<PrepareResult.NoModel>(await Engine().Prepare("en", "ko", default));
    }

    [Fact]
    public async Task Translate_uses_the_direct_pair()
    {
        Install("de-en");

        var result = await Engine().Translate("hallo", "de", "en", default);

        Assert.True(result.IsSuccess);
        Assert.Equal("hallo|de-en", result.Text);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Translate_pivots_through_english_with_both_models_resident()
    {
        Install("de-en");
        Install("en-ru");
        ModelHost? host = null;
        var residentDuringChain = new List<string>();
        var engine = Engine(hostOut: h => host = h, onTranslate: pair =>
        {
            residentDuringChain.AddRange(host!.LoadedKeys);
        });

        var result = await engine.Translate("hallo", "de", "ru", default);

        Assert.Equal("hallo|de-en|en-ru", result.Text);
        Assert.Contains("lite/de-en", residentDuringChain);
        Assert.Contains("lite/en-ru", residentDuringChain);
    }

    [Fact]
    public async Task Translate_without_a_route_is_no_model()
    {
        var result = await Engine().Translate("hi", "ru", "ko", default);

        Assert.Equal(EngineErrorCategory.NoModel, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_with_a_model_not_yet_downloaded_is_unavailable()
    {
        catalogue.Sizes["de-en"] = 10;

        var result = await Engine().Translate("hi", "de", "en", default);

        Assert.Equal(EngineErrorCategory.Unavailable, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_with_an_unreachable_catalogue_is_unavailable()
    {
        catalogue.Fails = true;

        var result = await Engine().Translate("hi", "de", "en", default);

        Assert.Equal(EngineErrorCategory.Unavailable, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_with_a_model_that_fails_to_load_is_unavailable()
    {
        Install("de-en");

        var result = await Engine(loadFails: true).Translate("hi", "de", "en", default);

        Assert.Equal(EngineErrorCategory.Unavailable, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_that_throws_in_inference_is_bad_response()
    {
        Install("de-en");

        var result = await Engine(onTranslate: _ => throw new InvalidOperationException("boom")).Translate("hi", "de", "en", default);

        Assert.Equal(EngineErrorCategory.BadResponse, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_that_returns_empty_text_is_bad_response()
    {
        Install("de-en");

        var result = await Engine(output: _ => "  ").Translate("hi", "de", "en", default);

        Assert.Equal(EngineErrorCategory.BadResponse, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_that_exceeds_the_request_timeout_is_timeout()
    {
        Install("de-en");
        using var gate = new ManualResetEventSlim();
        var engine = Engine(timeout: TimeSpan.FromMilliseconds(100), onTranslate: _ => gate.Wait(TimeSpan.FromSeconds(30)));

        var result = await engine.Translate("hi", "de", "en", default);
        gate.Set();

        Assert.Equal(EngineErrorCategory.Timeout, result.Error!.Category);
    }

    [Fact]
    public async Task Translate_honours_caller_cancellation()
    {
        Install("de-en");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Engine().Translate("hi", "de", "en", cts.Token));
    }

    [Fact]
    public async Task Release_frees_loaded_models_and_the_engine_still_works_afterwards()
    {
        Install("de-en");
        var disposed = new List<string>();
        var engine = Engine(onDispose: disposed.Add);
        await engine.Translate("a", "de", "en", default);

        await engine.Release();
        await engine.Release();

        Assert.Single(disposed);
        Assert.True((await engine.Translate("b", "de", "en", default)).IsSuccess);
    }

    [Fact]
    public async Task Catalogue_reads_the_total_size_of_the_required_files()
    {
        var files = string.Join(",", LiteEngine.RequiredFiles.Select(f => $"{{\"rfilename\":\"{f}\",\"size\":10}}"));
        var catalogue = new XenovaModelCatalogue(new HttpClient(new StubHandler(HttpStatusCode.OK, $"{{\"siblings\":[{files}]}}")));

        Assert.Equal(10L * LiteEngine.RequiredFiles.Count, await catalogue.FindSizeAsync(new LanguagePair("de", "en"), default));
    }

    [Fact]
    public async Task Catalogue_returns_null_for_an_unpublished_pair_or_incomplete_listing()
    {
        var missing = new XenovaModelCatalogue(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "")));
        var incomplete = new XenovaModelCatalogue(new HttpClient(new StubHandler(HttpStatusCode.OK, "{\"siblings\":[{\"rfilename\":\"vocab.json\",\"size\":1}]}")));

        Assert.Null(await missing.FindSizeAsync(new LanguagePair("ru", "ko"), default));
        Assert.Null(await incomplete.FindSizeAsync(new LanguagePair("de", "en"), default));
    }

    [Theory]
    [InlineData("de-en", "de", "en", "Das Wetter ist heute sehr schön.", new[] { "weather" })]
    [InlineData("de-en,en-ru", "de", "ru", "Das Wetter ist heute sehr schön.", new[] { "погод" })]
    public async Task Live_translation_for_direct_and_pivot_pairs(string pairs, string source, string target, string line, string[] expected)
    {
        var liveRoot = await LiveFolderAsync(pairs.Split(','));
        Assert.SkipUnless(liveRoot is not null, "model files not in the local test cache");
        using var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
        var engine = new LiteEngine(
            new ModelFolder(liveRoot, LiteEngine.RequiredFiles),
            catalogue,
            () => host,
            LitePairTranslator.Load,
            TimeSpan.FromSeconds(120));

        Assert.IsType<PrepareResult.Ready>(await engine.Prepare(source, target, default));
        var result = await engine.Translate(line, source, target, default);

        Assert.True(result.IsSuccess, result.Error?.Detail);
        Assert.Contains(expected, word => result.Text!.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (pairs.Contains(','))
        {
            Assert.Equal(2, host.LoadedKeys.Count);
        }

        try
        {
            Directory.Delete(liveRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Translate_russian_to_korean_has_no_route()
    {
        var engine = Engine();

        var result = await engine.Translate("Привет", "ru", "ko", default);

        Assert.Equal(EngineErrorCategory.NoModel, result.Error!.Category);
    }

    private static async Task<string?> LiveFolderAsync(string[] pairs)
    {
        var liveRoot = Path.Combine(Path.GetTempPath(), "saywhat-lite-live-" + Guid.NewGuid().ToString("N"));
        foreach (var pair in pairs)
        {
            var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
            if (files is null)
            {
                return null;
            }

            foreach (var (name, path) in files)
            {
                var target = Path.Combine(liveRoot, "lite", pair, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
        }

        return liveRoot;
    }

    private void Install(string pair)
    {
        foreach (var file in LiteEngine.RequiredFiles)
        {
            var path = Path.Combine(root, "lite", pair, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private LiteEngine Engine(
        Action<ModelHost>? hostOut = null,
        Action<string>? onTranslate = null,
        Func<string, string>? output = null,
        Action<string>? onDispose = null,
        TimeSpan? timeout = null,
        bool loadFails = false)
    {
        return new LiteEngine(
            folder,
            catalogue,
            () =>
            {
                var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
                hosts.Add(host);
                hostOut?.Invoke(host);
                return host;
            },
            path => loadFails
                ? throw new IOException("cannot load")
                : new FakeTranslator(Path.GetFileName(path), onTranslate, output, onDispose),
            timeout ?? TimeSpan.FromSeconds(30));
    }

    private sealed class FakeCatalogue : ILiteModelCatalogue
    {
        public Dictionary<string, long> Sizes { get; } = [];

        public bool Fails { get; set; }

        public Task<long?> FindSizeAsync(LanguagePair pair, CancellationToken cancellation)
        {
            if (Fails)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(Sizes.TryGetValue($"{pair.Source}-{pair.Target}", out var size) ? size : (long?)null);
        }
    }

    private sealed class FakeTranslator(string pair, Action<string>? onTranslate, Func<string, string>? output, Action<string>? onDispose)
        : ILitePairTranslator
    {
        public string Translate(string text)
        {
            onTranslate?.Invoke(pair);
            return output is null ? $"{text}|{pair}" : output(text);
        }

        public void Dispose() => onDispose?.Invoke(pair);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
