using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Health;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace Hackathon.Assessment.Api.Endpoints;

public static class HealthEndpoints
{
    public static RouteHandlerBuilder MapHealthEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/health", HandleHealth)
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithName("Health")
            .WithSummary("Returns service health without calling external services.")
            .Produces<HealthResponse>(StatusCodes.Status200OK);

    private static Ok<HealthResponse> HandleHealth(
        HttpContext context,
        IConfiguration configuration,
        ApplicationUptime uptime)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new HealthResponse(
            "Healthy",
            configuration["APP_VERSION"] ?? "0.0.0-local",
            configuration["GIT_COMMIT_SHA"] ?? "local",
            uptime.UptimeSeconds));
    }
}
