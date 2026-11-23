using SayWhat.Core.Models;

namespace SayWhat.Tests;

public class ModelHostTests
{
    private const long Mb = 1024 * 1024;

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }

    private sealed class FakeSession : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture : IDisposable
    {
        public FakeClock Clock { get; } = new();

        public ModelHost Host { get; }

        public Dictionary<string, int> LoadCounts { get; } = new();

        public Dictionary<string, FakeSession> Sessions { get; } = new();

        public Fixture(long ceilingMb = 500)
        {
            Host = new ModelHost(TimeSpan.FromMinutes(10), ceilingMb * Mb, Clock);
        }

        public void Register(string key, long sizeMb, TaskCompletionSource? gate = null)
        {
            Host.Register(key, sizeMb * Mb, async _ =>
            {
                lock (LoadCounts)
                {
                    LoadCounts[key] = LoadCounts.GetValueOrDefault(key) + 1;
                }

                if (gate is not null)
                {
                    await gate.Task;
                }

                var session = new FakeSession();
                lock (Sessions)
                {
                    Sessions[key] = session;
                }

                return new LoadedModel(session);
            });
        }

        public async Task UseAsync(string key)
        {
            using var lease = await Host.AcquireAsync(key, CancellationToken.None);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public async Task Idle_model_unloads_after_the_timeout()
    {
        using var f = new Fixture();
        f.Register("a", 100);
        await f.UseAsync("a");

        f.Clock.Advance(TimeSpan.FromMinutes(9));
        f.Host.UnloadIdle();
        Assert.Contains("a", f.Host.LoadedKeys);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        f.Host.UnloadIdle();
        Assert.Empty(f.Host.LoadedKeys);
        Assert.True(f.Sessions["a"].Disposed);
    }

    [Fact]
    public async Task Model_in_use_is_not_unloaded_when_idle_time_passes()
    {
        using var f = new Fixture();
        f.Register("a", 100);
        using var lease = await f.Host.AcquireAsync("a", CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromHours(1));
        f.Host.UnloadIdle();

        Assert.Contains("a", f.Host.LoadedKeys);
    }

    [Fact]
    public async Task Least_recently_used_model_is_evicted_first()
    {
        using var f = new Fixture(300);
        f.Register("a", 100);
        f.Register("b", 100);
        f.Register("c", 100);
        f.Register("d", 100);
        await f.UseAsync("a");
        await f.UseAsync("b");
        await f.UseAsync("c");
        await f.UseAsync("a");

        await f.UseAsync("d");

        Assert.DoesNotContain("b", f.Host.LoadedKeys);
        Assert.Contains("a", f.Host.LoadedKeys);
        Assert.Contains("c", f.Host.LoadedKeys);
        Assert.Contains("d", f.Host.LoadedKeys);
        Assert.True(f.Sessions["b"].Disposed);
        Assert.False(f.Sessions["a"].Disposed);
    }

    [Fact]
    public async Task Model_in_use_is_not_evicted()
    {
        using var f = new Fixture(200);
        f.Register("a", 100);
        f.Register("b", 100);
        f.Register("c", 100);
        using var held = await f.Host.AcquireAsync("a", CancellationToken.None);
        await f.UseAsync("b");

        await f.UseAsync("c");

        Assert.Contains("a", f.Host.LoadedKeys);
        Assert.DoesNotContain("b", f.Host.LoadedKeys);
        Assert.Contains("c", f.Host.LoadedKeys);
    }

    [Fact]
    public async Task Models_in_a_pivot_chain_are_not_evicted()
    {
        using var f = new Fixture(200);
        f.Register("xx-en", 100);
        f.Register("en-yy", 100);
        f.Register("other", 100);
        using var chain = await f.Host.AcquireChainAsync(["xx-en", "en-yy"], CancellationToken.None);

        await f.UseAsync("other");

        Assert.Contains("xx-en", f.Host.LoadedKeys);
        Assert.Contains("en-yy", f.Host.LoadedKeys);
        Assert.Equal(2, chain.Leases.Count);
    }

    [Fact]
    public async Task Model_larger_than_the_ceiling_loads_after_evicting_all_else()
    {
        using var f = new Fixture(300);
        f.Register("a", 100);
        f.Register("b", 100);
        f.Register("huge", 800);
        await f.UseAsync("a");
        await f.UseAsync("b");

        await f.UseAsync("huge");

        Assert.Equal(["huge"], f.Host.LoadedKeys);
        Assert.True(f.Sessions["a"].Disposed);
        Assert.True(f.Sessions["b"].Disposed);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_model_load_it_once()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Register("a", 100, gate);

        var first = f.Host.AcquireAsync("a", CancellationToken.None);
        var second = f.Host.AcquireAsync("a", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        using var leaseOne = await first;
        using var leaseTwo = await second;

        Assert.Equal(1, f.LoadCounts["a"]);
        Assert.Same(leaseOne.Session, leaseTwo.Session);
    }

    [Fact]
    public async Task Failed_load_reaches_the_caller_and_allows_a_retry()
    {
        using var f = new Fixture();
        var attempts = 0;
        f.Host.Register("a", 10 * Mb, _ =>
            ++attempts == 1
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new LoadedModel(new object())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.AcquireAsync("a", CancellationToken.None));
        Assert.Empty(f.Host.LoadedKeys);

        using var lease = await f.Host.AcquireAsync("a", CancellationToken.None);
        Assert.Contains("a", f.Host.LoadedKeys);
    }

    [Fact]
    public async Task Unregistered_model_is_rejected()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Host.AcquireAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Blocking_loader_does_not_block_the_calling_thread()
    {
        using var f = new Fixture();
        using var release = new ManualResetEventSlim();
        f.Host.Register("a", Mb, _ =>
        {
            release.Wait();
            return Task.FromResult(new LoadedModel(new object()));
        });

        var pending = f.Host.AcquireAsync("a", CancellationToken.None);

        Assert.False(pending.IsCompleted);
        release.Set();
        using var lease = await pending;
        Assert.Contains("a", f.Host.LoadedKeys);
    }

    [Fact]
    public async Task Cancelled_waiter_releases_its_claim()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Register("a", 100, gate);
        using var cts = new CancellationTokenSource();

        var waiting = f.Host.AcquireAsync("a", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.SetResult();
        await Task.Delay(50);
        f.Clock.Advance(TimeSpan.FromHours(1));
        f.Host.UnloadIdle();

        Assert.Empty(f.Host.LoadedKeys);
    }
}
