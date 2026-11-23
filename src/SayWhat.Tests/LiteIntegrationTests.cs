using System.Net;
using System.Text;
using SayWhat.Core.Engines;
using SayWhat.Core.Models;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public sealed class LiteIntegrationTests : IDisposable
{
    private const string PublishedPairs = "de-en,en-ru,ru-en";

    private readonly string root = Path.Combine(Path.GetTempPath(), "saywhat-lite-integration-" + Guid.NewGuid().ToString("N"));
    private readonly List<ModelHost> hosts = [];

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
    public async Task Direct_pivot_and_no_route_pairs_resolve_through_the_interface_model_host_and_folder()
    {
        Install("de-en");
        Install("en-ru");
        IEngine engine = Engine(PublishedCatalogue());

        var direct = await engine.Translate("hallo", "de", "en", default);
        var pivot = await engine.Translate("hallo", "de", "ru", default);
        var noRoute = await engine.Translate("hallo", "ru", "ko", default);

        Assert.Equal("hallo|de-en", direct.Text);
        Assert.Equal("hallo|de-en|en-ru", pivot.Text);
        Assert.Equal(EngineErrorCategory.NoModel, noRoute.Error!.Category);
        Assert.IsType<PrepareResult.Ready>(await engine.Prepare("de", "ru", default));
        Assert.IsType<PrepareResult.NoModel>(await engine.Prepare("ru", "ko", default));
    }

    [Fact]
    public async Task Pivot_pins_both_legs_in_the_host_and_release_unloads_them_all()
    {
        Install("de-en");
        Install("en-ru");
        var engine = Engine(PublishedCatalogue());

        await engine.Translate("hallo", "de", "ru", default);

        var host = Assert.Single(hosts);
        Assert.Equal(["lite/de-en", "lite/en-ru"], host.LoadedKeys.Order().ToArray());

        await engine.Release();

        Assert.Empty(host.LoadedKeys);
        var again = await engine.Translate("hallo", "de", "en", default);
        Assert.Equal("hallo|de-en", again.Text);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public async Task Prepare_reports_download_size_from_the_catalogue_for_models_missing_from_the_folder()
    {
        Install("de-en");
        var engine = Engine(PublishedCatalogue(size: 10));

        var prepared = await engine.Prepare("de", "ru", default);

        var download = Assert.IsType<PrepareResult.NeedsDownload>(prepared);
        Assert.Equal(10L * LiteEngine.RequiredFiles.Count, download.SizeBytes);
        Assert.Equal(EngineErrorCategory.Unavailable, (await engine.Translate("hallo", "de", "ru", default)).Error!.Category);
    }

    [Fact]
    public async Task Live_direct_pivot_and_no_route_pairs_through_the_interface()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SAYWHAT_FETCH_TEST_MODELS") == "1", "live integration checks are opt-in");
        using var live = await LiveModelFolder.CreateAsync("saywhat-lite-integration-live-", ["de-en", "en-ru"]);
        Assert.SkipUnless(live is not null, "model files not in the local test cache");
        using var client = new HttpClient();
        using var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
        IEngine engine = new LiteEngine(
            new ModelFolder(live.Root, LiteEngine.RequiredFiles),
            new XenovaModelCatalogue(client),
            () => host,
            LitePairTranslator.Load,
            TimeSpan.FromSeconds(120));

        var direct = await engine.Translate("Das Wetter ist heute sehr schön.", "de", "en", default);
        var pivot = await engine.Translate("Das Wetter ist heute sehr schön.", "de", "ru", default);
        var noRoute = await engine.Translate("Привет", "ru", "ko", default);
        var prepareNoRoute = await engine.Prepare("ru", "ko", default);

        Assert.True(direct.IsSuccess, direct.Error?.Detail);
        Assert.Contains("weather", direct.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(pivot.IsSuccess, pivot.Error?.Detail);
        Assert.Contains("погод", pivot.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["lite/de-en", "lite/en-ru"], host.LoadedKeys.Order().ToArray());
        Assert.Equal(EngineErrorCategory.NoModel, noRoute.Error!.Category);
        Assert.IsType<PrepareResult.NoModel>(prepareNoRoute);
    }

    private static XenovaModelCatalogue PublishedCatalogue(long size = 10)
    {
        var listing = "{\"siblings\":[" + string.Join(",", LiteEngine.RequiredFiles.Select(f => $"{{\"rfilename\":\"{f}\",\"size\":{size}}}")) + "]}";
        var published = PublishedPairs.Split(',');
        return new XenovaModelCatalogue(new HttpClient(new RoutingHandler(url =>
            published.Any(pair => url.Contains($"opus-mt-{pair}", StringComparison.Ordinal))
                ? (HttpStatusCode.OK, listing)
                : (HttpStatusCode.Unauthorized, ""))));
    }

    private LiteEngine Engine(ILiteModelCatalogue catalogue) =>
        new(
            new ModelFolder(root, LiteEngine.RequiredFiles),
            catalogue,
            () =>
            {
                var host = new ModelHost(TimeSpan.FromMinutes(10), 500L * 1024 * 1024, TimeProvider.System);
                hosts.Add(host);
                return host;
            },
            path => new StubTranslator(Path.GetFileName(path)),
            TimeSpan.FromSeconds(30));

    private void Install(string pair)
    {
        foreach (var file in LiteEngine.RequiredFiles)
        {
            var path = Path.Combine(root, "lite", pair, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private sealed class StubTranslator(string pair) : ILitePairTranslator
    {
        public string Translate(string text) => $"{text}|{pair}";

        public void Dispose()
        {
        }
    }

    private sealed class RoutingHandler(Func<string, (HttpStatusCode Status, string Body)> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = route(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
