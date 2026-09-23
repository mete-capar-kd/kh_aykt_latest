using Azure.Monitor.OpenTelemetry.AspNetCore;
using Hackathon.Assessment.Api.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Hackathon.Assessment.Api.Telemetry;

public static class TelemetryRegistration
{
    public static bool AddAssessmentOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var attributes = GetResourceAttributes(configuration);
        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddAttributes(attributes))
            .WithTracing(tracing => tracing
                .AddSource(AiTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(AiTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (GetAzureMonitorConnectionString(configuration) is { } connectionString)
        {
            var ratio = configuration.GetValue("Telemetry:SamplingRatio", 1.0);
            builder.UseAzureMonitor(options =>
            {
                options.ConnectionString = connectionString;
                options.SamplingRatio = (float)ratio;
            });
            return true;
        }

        return false;
    }

    public static IReadOnlyDictionary<string, object> GetResourceAttributes(
        IConfiguration configuration) =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.name"] = "hackathon-assessment-api",
            ["service.version"] = configuration["APP_VERSION"] ?? "0.0.0-local",
            ["team"] = configuration["Telemetry:Team"] ?? ApimOptions.OrganizationPlaceholder,
            ["application"] = configuration["Telemetry:Application"] ?? "hackathon-assessment-api",
            ["environment"] = configuration["Telemetry:Environment"] ?? "local",
            ["commit.sha"] = configuration["GIT_COMMIT_SHA"] ?? "local"
        };

    public static bool HasAzureMonitorConnectionString(IConfiguration configuration) =>
        GetAzureMonitorConnectionString(configuration) is not null;

    private static string? GetAzureMonitorConnectionString(IConfiguration configuration)
    {
        var value = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
