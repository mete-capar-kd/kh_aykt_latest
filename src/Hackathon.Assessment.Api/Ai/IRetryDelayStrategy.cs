namespace Hackathon.Assessment.Api.Ai;

public interface IRetryDelayStrategy
{
    TimeSpan GetDelay(int retryNumber, TimeSpan? retryAfter);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RetryDelayStrategy : IRetryDelayStrategy
{
    public TimeSpan GetDelay(int retryNumber, TimeSpan? retryAfter)
    {
        if (retryAfter is { } serverDelay)
        {
            return serverDelay < TimeSpan.Zero ? TimeSpan.Zero : serverDelay;
        }

        var exponentialMilliseconds = 1000d * Math.Pow(2, Math.Max(0, retryNumber - 1));
        var milliseconds = Math.Min(10_000d, exponentialMilliseconds + Random.Shared.Next(0, 251));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
}
