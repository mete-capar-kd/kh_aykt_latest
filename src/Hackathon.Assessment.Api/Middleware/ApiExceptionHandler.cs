using Hackathon.Assessment.Api.Domain;
using Microsoft.AspNetCore.Diagnostics;

namespace Hackathon.Assessment.Api.Middleware;

public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException
            && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var statusCode = exception switch
        {
            RepositoryAccessException or SnapshotLimitExceededException =>
                StatusCodes.Status422UnprocessableEntity,
            GatewayException => StatusCodes.Status502BadGateway,
            AssessmentTimeoutException => StatusCodes.Status504GatewayTimeout,
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                StatusCodes.Status413PayloadTooLarge,
            BadHttpRequestException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            LogUnexpectedException(
                logger,
                CorrelationIdMiddleware.GetCorrelationId(httpContext));
        }

        httpContext.Response.Clear();
        var result = statusCode == StatusCodes.Status400BadRequest
            ? ApiProblemResults.ValidationProblem(
                httpContext,
                new Dictionary<string, string[]>
                {
                    ["body"] = ["The request body is invalid."]
                })
            : ApiProblemResults.Problem(
                httpContext,
                statusCode,
                statusCode switch
                {
                    StatusCodes.Status413PayloadTooLarge => "The request body is too large.",
                    StatusCodes.Status422UnprocessableEntity => "The repository cannot be assessed.",
                    StatusCodes.Status502BadGateway => "The AI gateway request failed.",
                    StatusCodes.Status504GatewayTimeout => "The assessment timed out.",
                    _ => "An unexpected error occurred."
                });

        await result.ExecuteAsync(httpContext);
        return true;
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unexpected API exception. CorrelationId={CorrelationId}")]
    private static partial void LogUnexpectedException(ILogger logger, string correlationId);
}
