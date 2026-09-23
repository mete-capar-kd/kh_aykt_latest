using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Auth;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Middleware;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Safety;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Endpoints;

public static class AskEndpoints
{
    public static readonly object InputGuardResultItemKey = new();

    private static readonly HashSet<string> ValidMetricIds = new(StringComparer.Ordinal)
    {
        "m01", "m02", "m03", "m04", "m05",
        "m06", "m07", "m08", "m09", "m10"
    };

    private static readonly Regex OwnerPattern = new(
        @"\A[A-Za-z0-9-]{1,39}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RepositoryPattern = new(
        @"\A[A-Za-z0-9._-]{1,100}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex RefPattern = new(
        @"\A[A-Za-z0-9._/-]{1,200}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static RouteHandlerBuilder MapAskEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/ask", HandleAskAsync)
            .AddEndpointFilter<AskRequestValidationFilter>()
            .RequireAuthorization(EntraIdAuthentication.AskPolicyName)
            .RequireRateLimiting(AskRateLimiting.PolicyName)
            .WithName("Ask")
            .WithSummary("Ask a question about a repository assessment.")
            .Produces<AskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

    private static async Task<Ok<AskResponse>> HandleAskAsync(
        AskRequest request,
        HttpContext httpContext,
        IAskOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var guardResult = httpContext.Items[InputGuardResultItemKey] as InputGuardResult
            ?? throw new InvalidOperationException("The input validation filter did not run.");
        var context = new AskContext(
            CorrelationIdMiddleware.GetCorrelationId(httpContext),
            guardResult.NormalizedQuestion,
            guardResult.SuspectedInjection,
            CallerIdentity.GetCallerId(httpContext));

        var response = await orchestrator.AskAsync(context, request, cancellationToken);
        return TypedResults.Ok(response with { CorrelationId = context.CorrelationId });
    }

    public sealed class AskRequestValidationFilter(
        IOptions<RepositoryOptions> repositoryOptions,
        IInputGuard inputGuard) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            var requestIndex = -1;
            AskRequest? request = null;
            for (var index = 0; index < context.Arguments.Count; index++)
            {
                if (context.Arguments[index] is AskRequest askRequest)
                {
                    requestIndex = index;
                    request = askRequest;
                    break;
                }
            }

            if (requestIndex < 0 || request is null)
            {
                return ApiProblemResults.ValidationProblem(
                    context.HttpContext,
                    new Dictionary<string, string[]>
                    {
                        ["body"] = ["A JSON request body is required."]
                    });
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var guardResult = inputGuard.Process(request.Question ?? "");
            if (request.Question is null)
            {
                AddError(errors, "question", "The question field is required.");
            }
            else if (guardResult.NormalizedQuestion.Length is < 3 or > 2000)
            {
                AddError(errors, "question", "The question must contain 3 to 2000 characters.");
            }

            var repositoryUrl = request.RepositoryUrl ?? repositoryOptions.Value.DefaultUrl;
            if (!IsValidRepositoryUrl(repositoryUrl, repositoryOptions.Value.AllowedInputHosts))
            {
                AddError(errors, "repositoryUrl", "The repository URL is invalid or not allowed.");
            }

            var reference = request.Ref ?? repositoryOptions.Value.DefaultRef;
            if (!IsValidReference(reference))
            {
                AddError(errors, "ref", "The ref must be a valid branch or commit reference.");
            }

            if (request.Metrics is { } metrics)
            {
                if (metrics.Count == 0)
                {
                    AddError(errors, "metrics", "At least one metric must be selected.");
                }
                else
                {
                    if (metrics.Any(metric => !ValidMetricIds.Contains(metric)))
                    {
                        AddError(errors, "metrics", "Metric identifiers must be m01 through m10.");
                    }

                    if (metrics.Distinct(StringComparer.Ordinal).Count() != metrics.Count)
                    {
                        AddError(errors, "metrics", "Metric identifiers must not repeat.");
                    }
                }
            }

            if (errors.Count > 0)
            {
                return ApiProblemResults.ValidationProblem(context.HttpContext, errors);
            }

            context.Arguments[requestIndex] = request with
            {
                Question = guardResult.NormalizedQuestion,
                RepositoryUrl = repositoryUrl,
                Ref = reference
            };
            context.HttpContext.Items[InputGuardResultItemKey] = guardResult;
            return await next(context);
        }

        private static bool IsValidRepositoryUrl(string url, IReadOnlyCollection<string> allowedHosts)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(url, url.Trim(), StringComparison.Ordinal)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || url.Contains('@')
                || url.Contains('?')
                || url.Contains('#')
                || !allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = uri.AbsolutePath;
            if (path.EndsWith("/", StringComparison.Ordinal))
            {
                path = path[..^1];
            }

            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = path[1..].Split('/');
            return segments.Length == 2
                && OwnerPattern.IsMatch(segments[0])
                && RepositoryPattern.IsMatch(segments[1]);
        }

        private static bool IsValidReference(string reference) =>
            !reference.Contains("..", StringComparison.Ordinal)
            && RefPattern.IsMatch(reference);

        private static void AddError(
            IDictionary<string, string[]> errors,
            string key,
            string error)
        {
            if (errors.TryGetValue(key, out var current))
            {
                errors[key] = [.. current, error];
            }
            else
            {
                errors.Add(key, [error]);
            }
        }
    }
}
