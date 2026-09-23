using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Caching;

public sealed class SnapshotCacheTests
{
    private static readonly Uri RepositoryUrl = new("https://github.com/org/repo");

    [Fact]
    public async Task GetAsync_single_flies_concurrent_resolution_of_same_ref()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubProvider(async (_, _, _) =>
        {
            await release.Task;
            return Snapshot("sha-1");
        });
        using var cache = new SnapshotCache(provider);

        var waiters = Enumerable.Range(0, 5)
            .Select(_ => cache.GetAsync(RepositoryUrl, "main", CancellationToken.None))
            .ToArray();
        await Task.Yield();
        release.SetResult();
        await Task.WhenAll(waiters);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task GetAsync_does_not_return_stale_snapshot_for_moving_ref()
    {
        var snapshots = new Queue<RepositorySnapshot>(
        [
            Snapshot("sha-1"),
            Snapshot("sha-2")
        ]);
        var provider = new StubProvider((_, _, _) => Task.FromResult(snapshots.Dequeue()));
        using var cache = new SnapshotCache(provider);

        var first = await cache.GetAsync(RepositoryUrl, "main", CancellationToken.None);
        var second = await cache.GetAsync(RepositoryUrl, "main", CancellationToken.None);

        Assert.Equal("sha-1", first.CommitSha);
        Assert.Equal("sha-2", second.CommitSha);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task GetOrAddAsync_replays_snapshot_by_known_commit()
    {
        using var cache = new SnapshotCache(new StubProvider());
        var calls = 0;

        Task<RepositorySnapshot> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Snapshot("sha-1"));
        }

        var first = await cache.GetOrAddAsync(
            RepositoryUrl,
            "sha-1",
            Factory,
            CancellationToken.None);
        var second = await cache.GetOrAddAsync(
            new Uri("HTTPS://GITHUB.COM/ORG/REPO"),
            "sha-1",
            Factory,
            CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_does_not_cache_snapshot_larger_than_byte_budget()
    {
        using var cache = new SnapshotCache(
            new StubProvider(),
            new SnapshotCacheOptions { SnapshotMaxBytes = 3 });
        var calls = 0;

        Task<RepositorySnapshot> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Snapshot("sha-1", "şş"));
        }

        await cache.GetOrAddAsync(RepositoryUrl, "sha-1", Factory, CancellationToken.None);
        await cache.GetOrAddAsync(RepositoryUrl, "sha-1", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_rejects_factory_result_for_different_commit()
    {
        using var cache = new SnapshotCache(new StubProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrAddAsync(
                RepositoryUrl,
                "expected",
                _ => Task.FromResult(Snapshot("other")),
                CancellationToken.None));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    private static RepositorySnapshot Snapshot(string sha, string content = "content") =>
        new(
            "org",
            "repo",
            sha,
            new Dictionary<string, SnapshotFile>
            {
                ["file.txt"] = new("file.txt", content, SnapshotFileRole.Other)
            });

    private sealed class StubProvider : IRepositorySnapshotProvider
    {
        private readonly Func<Uri, string, CancellationToken, Task<RepositorySnapshot>> _get;
        private int _calls;

        public StubProvider(
            Func<Uri, string, CancellationToken, Task<RepositorySnapshot>>? get = null)
        {
            _get = get ?? ((_, _, _) => Task.FromResult(Snapshot("sha")));
        }

        public int Calls => _calls;

        public Task<RepositorySnapshot> GetAsync(
            Uri repositoryUrl,
            string gitRef,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return _get(repositoryUrl, gitRef, ct);
        }
    }
}
