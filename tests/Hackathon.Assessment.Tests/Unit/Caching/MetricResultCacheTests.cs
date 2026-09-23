using System.Collections.Immutable;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Domain;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Caching;

public sealed class MetricResultCacheTests
{
    [Fact]
    public async Task GetOrAddAsync_replays_successful_result_for_normalized_repository_url()
    {
        using var cache = CreateCache();
        var calls = 0;

        var first = await GetAsync(
            cache,
            "HTTPS://GITHUB.COM/ORG/REPO",
            valueFactory: Factory);
        var second = await GetAsync(
            cache,
            "https://github.com/org/repo",
            valueFactory: Factory);

        Assert.Same(first, second);
        Assert.Equal(1, calls);

        Task<EvaluationOutcome> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Outcome());
        }
    }

    [Fact]
    public async Task GetOrAddAsync_runs_one_calculation_for_concurrent_waiters()
    {
        using var cache = CreateCache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<EvaluationOutcome> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return CompleteAsync();
        }

        var waiters = Enumerable.Range(0, 5)
            .Select(_ => GetAsync(cache, valueFactory: Factory))
            .ToArray();
        await Task.Yield();
        release.SetResult();
        var outcomes = await Task.WhenAll(waiters);

        Assert.Equal(1, calls);
        Assert.All(outcomes, outcome => Assert.Same(outcomes[0], outcome));

        async Task<EvaluationOutcome> CompleteAsync()
        {
            await release.Task;
            return Outcome();
        }
    }

    [Fact]
    public async Task Cancellation_of_first_waiter_does_not_cancel_shared_calculation()
    {
        using var cache = CreateCache();
        using var firstCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken calculationToken = default;

        async Task<EvaluationOutcome> Factory(CancellationToken token)
        {
            calculationToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Outcome();
        }

        var first = GetAsync(cache, valueFactory: Factory, callerToken: firstCancellation.Token);
        await started.Task;
        var second = GetAsync(cache, valueFactory: Factory);
        firstCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(calculationToken.IsCancellationRequested);

        release.SetResult();
        Assert.False((await second).IsTimeoutOrError);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "insufficient evidence")]
    public async Task GetOrAddAsync_does_not_cache_unsuccessful_outcome(
        bool isTimeoutOrError,
        string? notAssessableReason)
    {
        using var cache = CreateCache();
        var calls = 0;

        Task<EvaluationOutcome> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Outcome(isTimeoutOrError, notAssessableReason));
        }

        await GetAsync(cache, valueFactory: Factory);
        await GetAsync(cache, valueFactory: Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_removes_failed_calculation_from_single_flight()
    {
        using var cache = CreateCache();
        var calls = 0;

        async Task<EvaluationOutcome> Factory(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("failed");
            }

            return await Task.FromResult(Outcome());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetAsync(cache, valueFactory: Factory));
        var outcome = await GetAsync(cache, valueFactory: Factory);

        Assert.False(outcome.IsTimeoutOrError);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_cancels_calculation_at_its_own_deadline()
    {
        using var cache = CreateCache(metricDeadline: TimeSpan.FromMilliseconds(40));
        var calls = 0;

        async Task<EvaluationOutcome> Factory(CancellationToken token)
        {
            calls++;
            if (calls == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return Outcome();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GetAsync(cache, valueFactory: Factory));
        var retry = await GetAsync(cache, valueFactory: Factory);

        Assert.False(retry.IsTimeoutOrError);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_recalculates_after_ttl()
    {
        using var cache = CreateCache(ttl: TimeSpan.FromMilliseconds(40));
        var calls = 0;

        Task<EvaluationOutcome> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Outcome());
        }

        await GetAsync(cache, valueFactory: Factory);
        await GetAsync(cache, valueFactory: Factory);
        Assert.Equal(1, calls);

        await Task.Delay(150);
        await GetAsync(cache, valueFactory: Factory);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Size_limit_does_not_retain_two_distinct_entries_at_capacity_one()
    {
        using var cache = CreateCache(maxEntries: 1);
        var replayCalls = 0;

        await GetAsync(cache, commitSha: "sha-1");
        await GetAsync(cache, commitSha: "sha-2");

        await GetAsync(cache, commitSha: "sha-1", valueFactory: ReplayFactory);
        await GetAsync(cache, commitSha: "sha-2", valueFactory: ReplayFactory);

        Assert.True(replayCalls >= 1);

        Task<EvaluationOutcome> ReplayFactory(CancellationToken _)
        {
            replayCalls++;
            return Task.FromResult(Outcome());
        }
    }

    private static MetricResultCache CreateCache(
        TimeSpan? ttl = null,
        int maxEntries = 1000,
        TimeSpan? metricDeadline = null) =>
        new(new MetricResultCacheOptions
        {
            TtlOverride = ttl,
            MetricResultMaxEntries = maxEntries,
            MetricDeadline = metricDeadline ?? TimeSpan.FromSeconds(5)
        });

    private static Task<EvaluationOutcome> GetAsync(
        MetricResultCache cache,
        string repositoryUrl = "https://github.com/org/repo",
        string commitSha = "commit",
        Func<CancellationToken, Task<EvaluationOutcome>>? valueFactory = null,
        CancellationToken callerToken = default) =>
        cache.GetOrAddAsync(
            repositoryUrl,
            commitSha,
            MetricId.M01,
            "prompt-v1",
            valueFactory ?? (_ => Task.FromResult(Outcome())),
            callerToken);

    private static EvaluationOutcome Outcome(
        bool isTimeoutOrError = false,
        string? notAssessableReason = null) =>
        new(
            MetricId.M01,
            ImmutableArray<Finding>.Empty,
            0,
            ImmutableArray<SubCheckResult>.Empty,
            Coverage.None,
            "rationale",
            "risk",
            1,
            1,
            notAssessableReason,
            isTimeoutOrError);
}
