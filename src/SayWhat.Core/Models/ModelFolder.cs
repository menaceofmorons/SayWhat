namespace SayWhat.Core.Models;

public sealed class ModelFolder
{
    private const string LiteFolderName = "lite";

    private readonly IReadOnlyList<string> requiredFiles;

    public ModelFolder(string root, IReadOnlyList<string> requiredFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(requiredFiles);
        Root = root;
        this.requiredFiles = requiredFiles;
    }

    public string Root { get; }

    public string PathFor(LanguagePair pair) =>
        Path.Combine(Root, LiteFolderName, $"{pair.Source}-{pair.Target}");

    public bool IsComplete(LanguagePair pair)
    {
        var directory = PathFor(pair);
        return Directory.Exists(directory)
            && requiredFiles.All(file => File.Exists(Path.Combine(directory, file)));
    }
}
