using SayWhat.Core.Engines;

namespace SayWhat.Tests;

public class EngineInterfaceContractTests
{
    private sealed class StandInEngine : IEngine
    {
        public int ReleaseCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public EngineDescription Describe() =>
            new("stand-in", EngineLocality.Local, "characters", false, 2);

        public Task<PrepareResult> Prepare(string sourceLanguage, string targetLanguage, CancellationToken cancellation) =>
            Task.FromResult<PrepareResult>(targetLanguage switch
            {
                "ready" => new PrepareResult.Ready(),
                "download" => new PrepareResult.NeedsDownload(1234),
                _ => new PrepareResult.NoModel(),
            });

        public Task<TranslationResult> Translate(
            string protectedText, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            LastToken = cancellation;
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(targetLanguage switch
            {
                "plain" => TranslationResult.Success("out"),
                "usage" => TranslationResult.Success("out", new EngineUsage("characters", protectedText.Length)),
                _ => TranslationResult.Failure(Enum.Parse<EngineErrorCategory>(targetLanguage)),
            });
        }

        public Task Release()
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Describe_ReturnsAllFields()
    {
        var d = new StandInEngine().Describe();
        Assert.Equal("stand-in", d.EngineType);
        Assert.Equal(EngineLocality.Local, d.Locality);
        Assert.Equal("characters", d.UsageUnit);
        Assert.False(d.ReportsUsage);
        Assert.Equal(2, d.MaxParallelRequests);
    }

    [Fact]
    public async Task Prepare_ReturnsEveryOutcome()
    {
        IEngine engine = new StandInEngine();
        Assert.IsType<PrepareResult.Ready>(await engine.Prepare("en", "ready", default));
        var download = Assert.IsType<PrepareResult.NeedsDownload>(await engine.Prepare("en", "download", default));
        Assert.Equal(1234, download.SizeBytes);
        Assert.IsType<PrepareResult.NoModel>(await engine.Prepare("en", "none", default));
    }

    [Fact]
    public async Task Translate_WithoutUsage()
    {
        var r = await new StandInEngine().Translate("hi", "en", "plain", default);
        Assert.True(r.IsSuccess);
        Assert.Equal("out", r.Text);
        Assert.Null(r.Usage);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Translate_WithUsage()
    {
        var r = await new StandInEngine().Translate("hi", "en", "usage", default);
        Assert.True(r.IsSuccess);
        Assert.Equal(new EngineUsage("characters", 2), r.Usage);
    }

    [Fact]
    public void ErrorCategories_AreTheSevenOfSection42()
    {
        Assert.Equal(
            ["Unavailable", "Quota", "Cap", "NoModel", "Authentication", "BadResponse", "Timeout"],
            Enum.GetNames<EngineErrorCategory>());
    }

    [Theory]
    [InlineData(EngineErrorCategory.Unavailable)]
    [InlineData(EngineErrorCategory.Quota)]
    [InlineData(EngineErrorCategory.Cap)]
    [InlineData(EngineErrorCategory.NoModel)]
    [InlineData(EngineErrorCategory.Authentication)]
    [InlineData(EngineErrorCategory.BadResponse)]
    [InlineData(EngineErrorCategory.Timeout)]
    public async Task Translate_ReportsEachErrorCategory(EngineErrorCategory category)
    {
        var r = await new StandInEngine().Translate("hi", "en", category.ToString(), default);
        Assert.False(r.IsSuccess);
        Assert.Null(r.Text);
        Assert.Equal(category, r.Error!.Category);
    }

    [Fact]
    public async Task Translate_ReceivesCancellation()
    {
        var engine = new StandInEngine();
        using var cts = new CancellationTokenSource();
        await engine.Translate("hi", "en", "plain", cts.Token);
        Assert.Equal(cts.Token, engine.LastToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.Translate("hi", "en", "plain", cts.Token));
    }

    [Fact]
    public async Task Release_IsCallableRepeatedly()
    {
        var engine = new StandInEngine();
        await engine.Release();
        await engine.Release();
        Assert.Equal(2, engine.ReleaseCount);
    }

    [Fact]
    public void Interface_HasExactlyTheFourOperations()
    {
        Assert.Equal(
            ["Describe", "Prepare", "Release", "Translate"],
            typeof(IEngine).GetMethods().Select(m => m.Name).Order().ToArray());
    }
}
