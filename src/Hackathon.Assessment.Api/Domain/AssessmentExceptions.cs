namespace Hackathon.Assessment.Api.Domain;

public sealed class RepositoryAccessException(string message) : Exception(message)
{
}

public sealed class SnapshotLimitExceededException(string message) : Exception(message)
{
}

public sealed class GatewayException(string message) : Exception(message)
{
}

public sealed class AssessmentTimeoutException(string message) : Exception(message)
{
}
