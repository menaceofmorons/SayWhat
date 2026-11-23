namespace SayWhat.Tests;

public sealed class FastTextModelFixture : IAsyncLifetime
{
    private const string ModelUrl = "https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.ftz";

    private string directory = string.Empty;

    public string ModelPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        directory = Path.Combine(Path.GetTempPath(), "saywhat-fasttext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ModelPath = Path.Combine(directory, "lid.176.ftz");

        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync(ModelUrl);
        await File.WriteAllBytesAsync(ModelPath, bytes);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
