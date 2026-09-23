using System.Collections.Concurrent;
using System.Text;
using Hackathon.Assessment.Api.Snapshot;
using Microsoft.Extensions.Caching.Memory;

namespace Hackathon.Assessment.Api.Caching;

public sealed class SnapshotCacheOptions
{
    public const string SectionName = "Cache";

    public long SnapshotMaxBytes { get; init; } = 536_870_912;
}

public sealed class SnapshotCache : IRepositorySnapshotProvider, IDisposable
{
    private readonly IRepositorySnapshotProvider _inner;
    private readonly MemoryCache _completed;
    private readonly ConcurrentDictionary<SnapshotReferenceKey, Task<RepositorySnapshot>> _referenceFlights = new();
    private readonly ConcurrentDictionary<SnapshotCommitKey, Task<RepositorySnapshot>> _commitFlights = new();

    public SnapshotCache(
        IRepositorySnapshotProvider inner,
        SnapshotCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        options ??= new SnapshotCacheOptions();
        if (options.SnapshotMaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Snapshot capacity must be positive.");
        }

        _inner = inner;
        _completed = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.SnapshotMaxBytes,
            CompactionPercentage = 0.25
        });
    }

    public async Task<RepositorySnapshot> GetAsync(
        Uri repositoryUrl,
        string gitRef,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);

        var key = new SnapshotReferenceKey(Normalize(repositoryUrl), gitRef);
        var completion = new TaskCompletionSource<RepositorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedTask = _referenceFlights.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(sharedTask, completion.Task))
        {
            _ = ResolveReferenceAsync(key, repositoryUrl, gitRef, completion);
        }

        return await sharedTask.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<RepositorySnapshot> GetOrAddAsync(
        Uri repositoryUrl,
        string commitSha,
        Func<CancellationToken, Task<RepositorySnapshot>> valueFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentNullException.ThrowIfNull(valueFactory);

        var key = new SnapshotCommitKey(Normalize(repositoryUrl), commitSha);
        if (_completed.TryGetValue(key, out RepositorySnapshot? cached)
            && cached is not null)
        {
            return cached;
        }

        var completion = new TaskCompletionSource<RepositorySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedTask = _commitFlights.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(sharedTask, completion.Task))
        {
            _ = CompleteCommitAsync(key, completion, valueFactory);
        }

        return await sharedTask.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _completed.Dispose();

    private async Task ResolveReferenceAsync(
        SnapshotReferenceKey referenceKey,
        Uri repositoryUrl,
        string gitRef,
        TaskCompletionSource<RepositorySnapshot> completion)
    {
        try
        {
            var fetched = await _inner
                .GetAsync(repositoryUrl, gitRef, CancellationToken.None)
                .ConfigureAwait(false);
            var commitKey = new SnapshotCommitKey(referenceKey.RepositoryUrl, fetched.CommitSha);
            if (_completed.TryGetValue(commitKey, out RepositorySnapshot? cached)
                && cached is not null)
            {
                RemoveReferenceFlight(referenceKey, completion);
                completion.TrySetResult(cached);
                return;
            }

            Cache(commitKey, fetched);
            RemoveReferenceFlight(referenceKey, completion);
            completion.TrySetResult(fetched);
        }
        catch (OperationCanceledException exception)
        {
            RemoveReferenceFlight(referenceKey, completion);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            RemoveReferenceFlight(referenceKey, completion);
            completion.TrySetException(exception);
        }
        finally
        {
            RemoveReferenceFlight(referenceKey, completion);
        }
    }

    private async Task CompleteCommitAsync(
        SnapshotCommitKey key,
        TaskCompletionSource<RepositorySnapshot> completion,
        Func<CancellationToken, Task<RepositorySnapshot>> valueFactory)
    {
        try
        {
            var snapshot = await valueFactory(CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(snapshot.CommitSha, key.CommitSha, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The snapshot returned by the factory does not match the requested commit.");
            }

            Cache(key, snapshot);
            RemoveCommitFlight(key, completion);
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException exception)
        {
            RemoveCommitFlight(key, completion);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            RemoveCommitFlight(key, completion);
            completion.TrySetException(exception);
        }
        finally
        {
            RemoveCommitFlight(key, completion);
        }
    }

    private void Cache(SnapshotCommitKey key, RepositorySnapshot snapshot)
    {
        var size = snapshot.Files.Values.Aggregate(
            0L,
            (total, file) => checked(total + Encoding.UTF8.GetByteCount(file.Content)));

        _completed.Set(
            key,
            snapshot,
            new MemoryCacheEntryOptions
            {
                Priority = CacheItemPriority.Normal,
                Size = size
            });
    }

    private void RemoveReferenceFlight(
        SnapshotReferenceKey key,
        TaskCompletionSource<RepositorySnapshot> completion) =>
        _referenceFlights.TryRemove(
            new KeyValuePair<SnapshotReferenceKey, Task<RepositorySnapshot>>(
                key,
                completion.Task));

    private void RemoveCommitFlight(
        SnapshotCommitKey key,
        TaskCompletionSource<RepositorySnapshot> completion) =>
        _commitFlights.TryRemove(
            new KeyValuePair<SnapshotCommitKey, Task<RepositorySnapshot>>(
                key,
                completion.Task));

    private static string Normalize(Uri repositoryUrl) =>
        repositoryUrl.AbsoluteUri.ToLowerInvariant();

    private sealed record SnapshotReferenceKey(string RepositoryUrl, string GitRef);
    private sealed record SnapshotCommitKey(string RepositoryUrl, string CommitSha);
}
