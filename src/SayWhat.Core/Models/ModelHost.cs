namespace SayWhat.Core.Models;

public sealed class ModelHost : IDisposable
{
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Registration> registrations = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan idleTimeout;
    private readonly long memoryCeilingBytes;
    private readonly ITimer sweepTimer;

    public ModelHost(TimeSpan idleTimeout, long memoryCeilingBytes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.idleTimeout = idleTimeout;
        this.memoryCeilingBytes = memoryCeilingBytes;
        this.clock = clock;
        sweepTimer = clock.CreateTimer(_ => UnloadIdle(), null, SweepPeriod, SweepPeriod);
    }

    public long LoadedBytes
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Sum(entry => entry.EstimatedBytes);
            }
        }
    }

    public IReadOnlyCollection<string> LoadedKeys
    {
        get
        {
            lock (gate)
            {
                return entries.Keys.ToArray();
            }
        }
    }

    public void Register(string key, long estimatedBytes, Func<CancellationToken, Task<LoadedModel>> loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(loader);
        lock (gate)
        {
            registrations[key] = new Registration(estimatedBytes, loader);
        }
    }

    public async Task<ModelLease> AcquireAsync(string key, CancellationToken cancellation)
    {
        var chain = await AcquireChainAsync([key], cancellation).ConfigureAwait(false);
        return chain.Leases[0];
    }

    public async Task<ModelChain> AcquireChainAsync(IReadOnlyList<string> keys, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var pinned = new List<Entry>();
        var toUnload = new List<Entry>();
        try
        {
            lock (gate)
            {
                foreach (var key in keys)
                {
                    pinned.Add(Pin(key, toUnload));
                }
            }

            DisposeAll(toUnload);
            var leases = new List<ModelLease>();
            foreach (var entry in pinned)
            {
                var model = await entry.Loading.WaitAsync(cancellation).ConfigureAwait(false);
                leases.Add(new ModelLease(this, entry, model.Session));
            }

            return new ModelChain(leases);
        }
        catch
        {
            foreach (var entry in pinned)
            {
                Unpin(entry);
            }

            throw;
        }
    }

    public void UnloadIdle()
    {
        var toUnload = new List<Entry>();
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in entries.Values.ToArray())
            {
                if (entry.Pins == 0 && entry.Loading.IsCompletedSuccessfully && now - entry.LastUsed >= idleTimeout)
                {
                    entries.Remove(entry.Key);
                    toUnload.Add(entry);
                }
            }
        }

        DisposeAll(toUnload);
    }

    public void Dispose()
    {
        sweepTimer.Dispose();
        List<Entry> all;
        lock (gate)
        {
            all = entries.Values.ToList();
            entries.Clear();
        }

        DisposeAll(all);
    }

    internal void Release(Entry entry) => Unpin(entry);

    private Entry Pin(string key, List<Entry> toUnload)
    {
        if (entries.TryGetValue(key, out var existing))
        {
            existing.Pins++;
            existing.LastUsed = clock.GetUtcNow();
            return existing;
        }

        if (!registrations.TryGetValue(key, out var registration))
        {
            throw new KeyNotFoundException($"No model registered for '{key}'.");
        }

        EvictLeastRecentlyUsed(registration.EstimatedBytes, toUnload);
        var entry = new Entry(key, registration.EstimatedBytes, clock.GetUtcNow()) { Pins = 1 };
        entries[key] = entry;
        entry.Loading = Task.Run(() => LoadAsync(entry, registration));
        return entry;
    }

    private void EvictLeastRecentlyUsed(long incomingBytes, List<Entry> toUnload)
    {
        var candidates = entries.Values
            .Where(entry => entry.Pins == 0 && entry.Loading.IsCompletedSuccessfully)
            .OrderBy(entry => entry.LastUsed)
            .ToList();
        var total = entries.Values.Sum(entry => entry.EstimatedBytes);
        foreach (var candidate in candidates)
        {
            if (total + incomingBytes <= memoryCeilingBytes)
            {
                break;
            }

            entries.Remove(candidate.Key);
            toUnload.Add(candidate);
            total -= candidate.EstimatedBytes;
        }
    }

    private async Task<LoadedModel> LoadAsync(Entry entry, Registration registration)
    {
        try
        {
            return await registration.Loader(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                if (entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    entries.Remove(entry.Key);
                }
            }

            throw;
        }
    }

    private void Unpin(Entry entry)
    {
        lock (gate)
        {
            entry.Pins--;
            entry.LastUsed = clock.GetUtcNow();
        }
    }

    private static void DisposeAll(List<Entry> unloaded)
    {
        foreach (var entry in unloaded)
        {
            if (entry.Loading.IsCompletedSuccessfully && entry.Loading.Result.Session is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed record Registration(long EstimatedBytes, Func<CancellationToken, Task<LoadedModel>> Loader);

    internal sealed class Entry(string key, long estimatedBytes, DateTimeOffset lastUsed)
    {
        public string Key { get; } = key;

        public long EstimatedBytes { get; } = estimatedBytes;

        public DateTimeOffset LastUsed { get; set; } = lastUsed;

        public int Pins { get; set; }

        public Task<LoadedModel> Loading { get; set; } = null!;
    }
}

public sealed class ModelLease : IDisposable
{
    private readonly ModelHost host;
    private readonly ModelHost.Entry entry;
    private int released;

    internal ModelLease(ModelHost host, ModelHost.Entry entry, object session)
    {
        this.host = host;
        this.entry = entry;
        Session = session;
    }

    public object Session { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            host.Release(entry);
        }
    }
}

public sealed class ModelChain(IReadOnlyList<ModelLease> leases) : IDisposable
{
    public IReadOnlyList<ModelLease> Leases { get; } = leases;

    public void Dispose()
    {
        foreach (var lease in Leases)
        {
            lease.Dispose();
        }
    }
}
