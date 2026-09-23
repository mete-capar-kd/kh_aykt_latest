namespace Hackathon.Assessment.Api.Domain;

public sealed class RepositoryAccessException(string message) : Exception(message)
{
}

public sealed class SnapshotLimitExceededException(string message) : Exception(message)
{
}

public sealed class GatewayException : Exception
{
    public GatewayException(string message) : base(message)
    {
    }

    public GatewayException(int statusCode, string deployment, int retryCount)
        : base($"APIM gateway request failed with status {statusCode} for deployment {deployment} after {retryCount} retries.")
    {
        StatusCode = statusCode;
        Deployment = deployment;
        RetryCount = retryCount;
    }

    public int? StatusCode { get; }
    public string? Deployment { get; }
    public int RetryCount { get; }
}

public sealed class AssessmentTimeoutException(string message) : Exception(message)
{
}

public sealed class ContentFilteredException(
    int statusCode,
    string deployment,
    string finishReason) :
    Exception($"APIM content filter rejected deployment {deployment} (status {statusCode}).")
{
    public int StatusCode { get; } = statusCode;
    public string Deployment { get; } = deployment;
    public string FinishReason { get; } = finishReason;
}
