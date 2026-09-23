namespace Hackathon.Assessment.Api.Health;

public sealed class ApplicationUptime(TimeProvider timeProvider)
{
    private readonly long _startTimestamp = timeProvider.GetTimestamp();

    public long UptimeSeconds =>
        Math.Max(0, (long)timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds);
}
