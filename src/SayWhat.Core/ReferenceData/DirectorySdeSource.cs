namespace SayWhat.Core.ReferenceData;

public sealed class DirectorySdeSource : ISdeSource
{
    private readonly string directory;

    public DirectorySdeSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
    }

    public Stream Open(string table) =>
        new FileStream(
            Path.Combine(directory, table + ".jsonl"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
}
