namespace Hackathon.Assessment.Api.Contracts;

public sealed record HealthResponse(
    string Status,
    string Version,
    string CommitSha,
    long UptimeSeconds);
