namespace SayWhat.Core.Engines;

public abstract record PrepareResult
{
    private PrepareResult()
    {
    }

    public sealed record Ready : PrepareResult;

    public sealed record NeedsDownload(long SizeBytes) : PrepareResult;

    public sealed record NoModel : PrepareResult;
}
