using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hackathon.Assessment.Api.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hackathon.Assessment.Tests.Integration;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task HealthReturnsVersionUptimeCorrelationAndNoStore()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "health-check-1");

        using var response = await client.GetAsync("/health");
        var rawBody = await response.Content.ReadAsStringAsync();
        var health = JsonSerializer.Deserialize<HealthResponse>(
            rawBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("health-check-1", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.NotNull(health);
        Assert.Equal("Healthy", health.Status);
        Assert.Equal("0.0.0-local", health.Version);
        Assert.Equal("local", health.CommitSha);
        Assert.True(health.UptimeSeconds >= 0);
        Assert.DoesNotContain("connectionString", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RateLimitRejectsThirdAskWithRetryAfterButDoesNotLimitHealth()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Repository:DefaultUrl"] = "https://github.com/org/repo",
            ["RateLimit:PermitsPerMinute"] = "2",
            ["RateLimit:QueueLimit"] = "0"
        };
        using var factory = new AssessmentApiFactory(settings: settings);
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync("/api/ask", new { question = "valid question" });
        using var second = await client.PostAsJsonAsync("/api/ask", new { question = "valid question" });
        using var third = await client.PostAsJsonAsync("/api/ask", new { question = "valid question" });
        using var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.RetryAfter is not null);
        using var problem = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("correlationId", out _));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task TestingEnvironmentAcceptsOrganizationPlaceholders()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void ProductionMapsOnlyAskAndHealthRoutes()
    {
        using var factory = new AssessmentApiFactory(
            environment: "Production",
            settings: AssessmentApiFactory.ProductionSettings);
        _ = factory.CreateClient();
        var routes = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route is not null)
            .Select(route => route!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["/api/ask", "/health"], routes);
    }

    [Fact]
    public void ProductionPlaceholderValidationNamesKeysWithoutValues()
    {
        using var factory = new AssessmentApiFactory(environment: "Production");

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var details = exception!.ToString();
        Assert.Contains("Apim:BaseUrl", details, StringComparison.Ordinal);
        Assert.DoesNotContain("<ORGANİZASYONDAN-ALINACAK>", details, StringComparison.Ordinal);
    }
}
