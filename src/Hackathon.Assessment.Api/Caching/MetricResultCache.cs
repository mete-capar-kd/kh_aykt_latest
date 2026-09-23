using System.Collections.Concurrent;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace Hackathon.Assessment.Api.Caching;

public sealed class MetricResultCacheOptions
{
    public const string SectionName = "Cache";

    public int MetricResultTtlMinutes { get; init; } = 120;
    public int MetricResultMaxEntries { get; init; } = 1000;
    public TimeSpan MetricDeadline { get; init; } = TimeSpan.FromSeconds(140);
    public TimeSpan? TtlOverride { get; init; }
}

public sealed class MetricResultCache : IDisposable
{
    private readonly MemoryCache _completed;
    private readonly ConcurrentDictionary<MetricResultCacheKey, Task<EvaluationOutcome>> _inFlight = new();
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _metricDeadline;

    public MetricResultCache(MetricResultCacheOptions? options = null)
    {
        options ??= new MetricResultCacheOptions();
        var ttl = options.TtlOverride
            ?? TimeSpan.FromMinutes(options.MetricResultTtlMinutes);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache TTL must be positive.");
        }

        if (options.MetricResultMaxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache capacity must be positive.");
        }

        if (options.MetricDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Metric deadline must be positive.");
        }

        _ttl = ttl;
        _metricDeadline = options.MetricDeadline;
        _completed = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.MetricResultMaxEntries
        });
    }

    public async Task<EvaluationOutcome> GetOrAddAsync(
        string repositoryUrl,
        string commitSha,
        MetricId metricId,
        string promptVersion,
        Func<CancellationToken, Task<EvaluationOutcome>> valueFactory,
        CancellationToken callerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentNullException.ThrowIfNull(valueFactory);

        var key = CreateKey(repositoryUrl, commitSha, metricId, promptVersion);

        if (_completed.TryGetValue(key, out EvaluationOutcome? cached)
            && cached is not null)
        {
            return cached;
        }

        var completion = new TaskCompletionSource<EvaluationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedTask = _inFlight.GetOrAdd(key, completion.Task);
        if (ReferenceEquals(sharedTask, completion.Task))
        {
            _ = CompleteAsync(key, completion, valueFactory);
        }

        return await sharedTask.WaitAsync(callerToken).ConfigureAwait(false);
    }

    public bool IsCached(
        string repositoryUrl,
        string commitSha,
        MetricId metricId,
        string promptVersion) =>
        _completed.TryGetValue(
            CreateKey(repositoryUrl, commitSha, metricId, promptVersion),
            out EvaluationOutcome? cached)
        && cached is not null;

    public void Dispose() => _completed.Dispose();

    private static MetricResultCacheKey CreateKey(
        string repositoryUrl,
        string commitSha,
        MetricId metricId,
        string promptVersion) =>
        new(repositoryUrl.ToLowerInvariant(), commitSha, metricId, promptVersion);

    private async Task CompleteAsync(
        MetricResultCacheKey key,
        TaskCompletionSource<EvaluationOutcome> completion,
        Func<CancellationToken, Task<EvaluationOutcome>> valueFactory)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_metricDeadline);
            var outcome = await valueFactory(deadline.Token).ConfigureAwait(false);
            if (IsSuccessful(outcome))
            {
                _completed.Set(
                    key,
                    outcome,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _ttl,
                        Size = 1
                    });
            }

            RemoveFlight(key, completion);
            completion.TrySetResult(outcome);
        }
        catch (OperationCanceledException exception)
        {
            RemoveFlight(key, completion);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            RemoveFlight(key, completion);
            completion.TrySetException(exception);
        }
        finally
        {
            RemoveFlight(key, completion);
        }
    }

    private void RemoveFlight(
        MetricResultCacheKey key,
        TaskCompletionSource<EvaluationOutcome> completion) =>
        _inFlight.TryRemove(
            new KeyValuePair<MetricResultCacheKey, Task<EvaluationOutcome>>(
                key,
                completion.Task));

    private static bool IsSuccessful(EvaluationOutcome outcome) =>
        !outcome.IsTimeoutOrError
        && string.IsNullOrWhiteSpace(outcome.NotAssessableReason);

    private sealed record MetricResultCacheKey(
        string RepositoryUrl,
        string CommitSha,
        MetricId MetricId,
        string PromptVersion);
}
