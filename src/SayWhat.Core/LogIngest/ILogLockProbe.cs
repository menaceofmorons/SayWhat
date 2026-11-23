namespace SayWhat.Core.LogIngest;

public enum LogLockState
{
    NotHeld,
    Held,
    Unavailable,
}

public interface ILogLockProbe
{
    LogLockState Probe(string path);
}
