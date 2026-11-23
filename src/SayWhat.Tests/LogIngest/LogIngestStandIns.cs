using SayWhat.Core.LogIngest;

namespace SayWhat.Tests.LogIngest;

public sealed class FakeLogLockProbe : ILogLockProbe
{
    private readonly Dictionary<string, LogLockState> _states = new(StringComparer.OrdinalIgnoreCase);

    public LogLockState DefaultState { get; set; } = LogLockState.NotHeld;

    public void Set(string path, LogLockState state) => _states[path] = state;

    public LogLockState Probe(string path) => _states.TryGetValue(path, out var state) ? state : DefaultState;
}

public sealed class FakeEveLogFolder(string path) : IEveLogFolder
{
    public string Path { get; set; } = path;
}
