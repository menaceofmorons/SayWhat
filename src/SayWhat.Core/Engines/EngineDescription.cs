namespace SayWhat.Core.Engines;

public enum EngineLocality
{
    Local,
    Remote,
}

public sealed record EngineDescription(
    string EngineType,
    EngineLocality Locality,
    string UsageUnit,
    bool ReportsUsage,
    int MaxParallelRequests);
