namespace SayWhat.Core.Engines;

public enum EngineErrorCategory
{
    Unavailable,
    Quota,
    Cap,
    NoModel,
    Authentication,
    BadResponse,
    Timeout,
}
