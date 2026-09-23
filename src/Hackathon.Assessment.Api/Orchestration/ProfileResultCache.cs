using System.Collections.Concurrent;
using Hackathon.Assessment.Api.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace Hackathon.Assessment.Api.Orchestration;

public sealed class ProfileResultCache : IDisposable
{
    private readonly MemoryCache _completed;
    private readonly ConcurrentDictionary<ProfileKey, Task<RepoProfile>> _inFlight = new();
    private readonly TimeSpan _ttl;

    public ProfileResultCache(int maxEntries, TimeSpan ttl)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl));
        }

        _completed = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxEntries });
        _ttl = ttl;
    }

    public async Task<RepoProfile> GetOrAddAsync(
        string repositoryUrl,
        string commitSha,
        string promptVersion,
        Func<Task<RepoProfile>> valueFactory,
        CancellationToken callerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentNullException.ThrowIfNull(valueFactory);

        var key = new ProfileKey(repositoryUrl.ToLowerInvariant(), commitSha, promptVersion);
        if (_completed.TryGetValue(key, out RepoProfile? cached) && cached is not null)
        {
            return cached;
        }

        var completion = new TaskCompletionSource<RepoProfile>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = _inFlight.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(shared, completion.Task))
        {
            _ = CompleteAsync(key, completion, valueFactory);
        }

        return await shared.WaitAsync(callerToken).ConfigureAwait(false);
    }

    public void Dispose() => _completed.Dispose();

    private async Task CompleteAsync(
        ProfileKey key,
        TaskCompletionSource<RepoProfile> completion,
        Func<Task<RepoProfile>> valueFactory)
    {
        try
        {
            var profile = await valueFactory().ConfigureAwait(false);
            _completed.Set(
                key,
                profile,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl, Size = 1 });
            completion.TrySetResult(profile);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _inFlight.TryRemove(
                new KeyValuePair<ProfileKey, Task<RepoProfile>>(key, completion.Task));
        }
    }

    private sealed record ProfileKey(string RepositoryUrl, string CommitSha, string PromptVersion);
}
