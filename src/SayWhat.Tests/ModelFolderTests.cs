using SayWhat.Core.Models;

namespace SayWhat.Tests;

public sealed class ModelFolderTests : IDisposable
{
    private static readonly string[] Files = ["model.onnx", "tokenizer.json"];

    private readonly string root = Path.Combine(Path.GetTempPath(), "saywhat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Each_pair_has_its_own_path_under_the_root()
    {
        var folder = new ModelFolder(root, Files);

        var one = folder.PathFor(new LanguagePair("en", "de"));
        var two = folder.PathFor(new LanguagePair("de", "en"));

        Assert.StartsWith(root, one);
        Assert.NotEqual(one, two);
        Assert.EndsWith("en-de", one);
    }

    [Fact]
    public void Missing_directory_is_incomplete()
    {
        var folder = new ModelFolder(root, Files);

        Assert.False(folder.IsComplete(new LanguagePair("en", "de")));
    }

    [Fact]
    public void Pair_with_a_missing_file_is_incomplete_and_with_all_files_is_complete()
    {
        var folder = new ModelFolder(root, Files);
        var pair = new LanguagePair("en", "de");
        Directory.CreateDirectory(folder.PathFor(pair));
        File.WriteAllText(Path.Combine(folder.PathFor(pair), "model.onnx"), "x");
        Assert.False(folder.IsComplete(pair));

        File.WriteAllText(Path.Combine(folder.PathFor(pair), "tokenizer.json"), "x");

        Assert.True(folder.IsComplete(pair));
        Assert.False(folder.IsComplete(new LanguagePair("en", "fr")));
    }
}
