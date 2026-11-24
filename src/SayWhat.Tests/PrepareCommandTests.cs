using System.Net;
using System.Text;
using SayWhat.Core.Models;
using SayWhat.Core.ReferenceData;
using SayWhat.Engines.Lite;
using SayWhat.Spike;

namespace SayWhat.Tests;

public sealed class PrepareCommandTests : IDisposable
{
    private const string Pair = "de";

    private readonly string root = Path.Combine(Path.GetTempPath(), "saywhat-prepare-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_DownloadsEverythingIntoTheModelFolderLayout()
    {
        var handler = new StubHandler();
        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        var database = new ReferenceDatabase(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile));
        Assert.Equal("4242", database.SdeVersion);
        Assert.Equal("detector", File.ReadAllText(Path.Combine(root, PrepareCommand.DetectorFile)));
        Assert.True(new ModelFolder(root, LiteEngine.RequiredFiles).IsComplete(new LanguagePair(Pair, "en")));
        Assert.Equal("file:onnx/encoder_model_quantized.onnx", File.ReadAllText(Path.Combine(root, "lite", "de-en", "onnx", "encoder_model_quantized.onnx")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(root, "sde-work")));
    }

    [Fact]
    public async Task Run_SkipsItemsAlreadyPresent()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile), "existing");
        File.WriteAllText(Path.Combine(root, PrepareCommand.DetectorFile), "existing");
        Install(Pair);
        var handler = new StubHandler();

        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(root, PrepareCommand.DetectorFile)));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile)));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("sde.test", StringComparison.Ordinal) || url.Contains("detector.test", StringComparison.Ordinal) || url.Contains("/resolve/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RefusesNonHttpsSource()
    {
        var handler = new StubHandler();
        var sources = TestSources() with { DetectorUrl = "http://detector.test/lid.176.ftz" };

        var code = await new PrepareCommand(new HttpClient(handler), sources, output, error).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("non-HTTPS", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, url => url.StartsWith("http://", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, PrepareCommand.DetectorFile)));
    }

    [Fact]
    public async Task Run_FailedDownloadKeepsTheOldFileAndReturnsNonZero()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile), "existing");
        var target = Path.Combine(root, "lite", "de-en", LiteEngine.EncoderFile);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "old");
        var handler = new StubHandler { Fail = url => url.EndsWith(LiteEngine.EncoderFile, StringComparison.Ordinal) };

        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("prepare failed", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(LiteEngine.EncoderFile, error.ToString(), StringComparison.Ordinal);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile)));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Run_PartialFileNeverReplacesAGoodOne()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile), "existing");
        var detector = Path.Combine(root, PrepareCommand.DetectorFile);
        var handler = new StubHandler { Truncate = url => url.Contains("detector.test", StringComparison.Ordinal) };

        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.False(File.Exists(detector));
        Assert.False(File.Exists(detector + ".partial"));

        File.WriteAllText(detector, "good");
        var retry = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(0, retry);
        Assert.Equal("good", File.ReadAllText(detector));
    }

    [Fact]
    public async Task Run_SkipsPairWithNoPublishedModel()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile), "existing");
        File.WriteAllText(Path.Combine(root, PrepareCommand.DetectorFile), "existing");
        var handler = new StubHandler { Published = [] };

        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("no published model", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, "lite")));
    }

    [Fact]
    public async Task Run_FailedSdeImportKeepsThePreviousDatabase()
    {
        var handler = new StubHandler { SdeShips = 1 };

        var code = await Command(handler).RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile)));
        Assert.False(Directory.Exists(Path.Combine(root, "sde-work")));
    }

    [Fact(Skip = "Set SAYWHAT_RUN_INTEGRATION=1 to download the real assets.", SkipUnless = nameof(LiveEnabled), SkipType = typeof(PrepareCommandTests))]
    public async Task Prepare_LivePair_ProducesUsableAssets()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var command = new PrepareCommand(client, PrepareSources.Published, output, error);

        var code = await command.RunAsync(Options(), TestContext.Current.CancellationToken);
        if (code != 0)
        {
            error.GetStringBuilder().Clear();
            code = await command.RunAsync(Options(), TestContext.Current.CancellationToken);
        }

        Assert.True(code == 0, error.ToString());
        var database = new ReferenceDatabase(Path.Combine(root, PrepareCommand.ReferenceDatabaseFile));
        Assert.False(string.IsNullOrEmpty(database.SdeVersion));
        Assert.Contains("en", database.ClientLanguages());
        Assert.True(new FileInfo(Path.Combine(root, PrepareCommand.DetectorFile)).Length > 100_000);
        Assert.True(new ModelFolder(root, LiteEngine.RequiredFiles).IsComplete(new LanguagePair(Pair, "en")));
    }

    public static bool LiveEnabled => Environment.GetEnvironmentVariable("SAYWHAT_RUN_INTEGRATION") == "1";

    private PrepareOptions Options() => new(root, "en", [Pair]);

    private PrepareCommand Command(StubHandler handler) => new(new HttpClient(handler), TestSources(), output, error);

    private static PrepareSources TestSources() => new(
        "https://sde.test/{0}.jsonl",
        "https://detector.test/lid.176.ftz",
        "https://huggingface.co/Xenova/opus-mt-{0}-{1}/resolve/main/{2}");

    private void Install(string source)
    {
        foreach (var file in LiteEngine.RequiredFiles)
        {
            var path = Path.Combine(root, "lite", $"{source}-en", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "existing");
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public Func<string, bool> Fail { get; init; } = _ => false;

        public Func<string, bool> Truncate { get; init; } = _ => false;

        public string[] Published { get; init; } = [Pair];

        public int SdeShips { get; init; } = 120;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (Fail(url))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (url.StartsWith("https://huggingface.co/api/models/", StringComparison.Ordinal))
            {
                return Task.FromResult(Listing(url));
            }

            var body = Body(url);
            var content = new ByteArrayContent(Truncate(url) ? body[..(body.Length / 2)] : body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private HttpResponseMessage Listing(string url)
        {
            if (!Published.Any(source => url.Contains($"opus-mt-{source}-en", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var files = string.Join(",", LiteEngine.RequiredFiles.Select(file => $"{{\"rfilename\":\"{file}\",\"size\":10}}"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"siblings\":[" + files + "]}", Encoding.UTF8, "application/json") };
        }

        private byte[] Body(string url)
        {
            if (url.StartsWith("https://sde.test/", StringComparison.Ordinal))
            {
                var table = url["https://sde.test/".Length..^".jsonl".Length];
                using var stream = SyntheticSde.Create(SdeShips, DefaultSystems).Open(table);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }

            if (url.Contains("detector.test", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetBytes("detector");
            }

            return Encoding.UTF8.GetBytes("file:" + url[(url.IndexOf("/resolve/main/", StringComparison.Ordinal) + "/resolve/main/".Length)..]);
        }

        private static int DefaultSystems => SdeImportOptions.DefaultMinimumSolarSystems;
    }
}
