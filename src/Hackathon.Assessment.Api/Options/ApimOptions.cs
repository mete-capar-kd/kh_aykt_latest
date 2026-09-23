using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Options;

public sealed class ApimOptions
{
    public const string SectionName = "Apim";
    public const string AzureDeploymentsRouteStyle = "AzureDeployments";
    public const string OpenAiV1RouteStyle = "OpenAIv1";
    public const string OrganizationPlaceholder = "<ORGANİZASYONDAN-ALINACAK>";

    [Required]
    public string BaseUrl { get; set; } = OrganizationPlaceholder;

    [Required]
    public string RouteStyle { get; set; } = OrganizationPlaceholder;

    public string ApiVersion { get; set; } = OrganizationPlaceholder;

    [Required]
    public ApimAuthOptions Auth { get; set; } = new();

    [Required]
    public ApimDeploymentOptions Deployments { get; set; } = new();

    [Range(1, 600)]
    public int AttemptTimeoutSeconds { get; set; } = 60;

    [Range(1, 3)]
    public int MaxAttempts { get; set; } = 3;

    public string CacheStatusHeader { get; set; } = "";
}

public sealed class ApimAuthOptions
{
    [Required]
    public string Scheme { get; set; } = ApimOptions.OrganizationPlaceholder;

    public string HeaderName { get; set; } = ApimOptions.OrganizationPlaceholder;

    public string? Key { get; set; }

    public string? Scope { get; set; }
}

public sealed class ApimDeploymentOptions
{
    [Required]
    public string Cheap { get; set; } = ApimOptions.OrganizationPlaceholder;

    [Required]
    public string Strong { get; set; } = ApimOptions.OrganizationPlaceholder;
}

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    [Required]
    public string Team { get; set; } = ApimOptions.OrganizationPlaceholder;

    [Required]
    public string Application { get; set; } = "hackathon-assessment-api";

    [Required]
    public string Environment { get; set; } = "local";

    [Range(0.0, 1.0)]
    public double SamplingRatio { get; set; } = 1.0;
}

public sealed class ApimOptionsValidator(
    IConfiguration configuration,
    IHostEnvironment environment) : IValidateOptions<ApimOptions>
{
    public ValidateOptionsResult Validate(string? name, ApimOptions options)
    {
        var errors = new List<string>();
        var production = environment.IsProduction();
        if (production)
        {
            AddPlaceholder(errors, configuration, "Apim:BaseUrl");
            AddPlaceholder(errors, configuration, "Apim:RouteStyle");
            if (options.RouteStyle == ApimOptions.AzureDeploymentsRouteStyle)
            {
                AddPlaceholder(errors, configuration, "Apim:ApiVersion");
            }
            AddPlaceholder(errors, configuration, "Apim:Auth:Scheme");
            AddPlaceholder(errors, configuration, "Apim:Auth:HeaderName");
            AddPlaceholder(errors, configuration, "Apim:Deployments:Cheap");
            AddPlaceholder(errors, configuration, "Apim:Deployments:Strong");
        }

        if (options.Auth.Scheme is "ManagedIdentity" or "OAuth")
        {
            errors.Add(
                "Apim:Auth:Scheme uses an unsupported organization authentication scheme; only SubscriptionKey is implemented.");
        }
        else if (options.Auth.Scheme is not ("SubscriptionKey" or "None")
                 && !(IsPlaceholder(options.Auth.Scheme) && !production))
        {
            errors.Add("Apim:Auth:Scheme must be SubscriptionKey.");
        }

        if (options.Auth.Scheme == "SubscriptionKey")
        {
            if (string.IsNullOrWhiteSpace(options.Auth.HeaderName)
                || IsPlaceholder(options.Auth.HeaderName))
            {
                errors.Add("Apim:Auth:HeaderName must be configured for SubscriptionKey.");
            }

            if (string.IsNullOrWhiteSpace(options.Auth.Key) && production)
            {
                errors.Add("Apim:Auth:Key must be configured for SubscriptionKey.");
            }
        }

        if (options.Auth.Scheme == "None" && !environment.IsEnvironment("Testing"))
        {
            errors.Add("Apim:Auth:Scheme None is permitted only in tests.");
        }

        if (!IsPlaceholder(options.RouteStyle)
            && options.RouteStyle is not (ApimOptions.AzureDeploymentsRouteStyle
                or ApimOptions.OpenAiV1RouteStyle))
        {
            errors.Add("Apim:RouteStyle must be AzureDeployments or OpenAIv1.");
        }

        if (options.RouteStyle == ApimOptions.AzureDeploymentsRouteStyle
            && string.IsNullOrWhiteSpace(options.ApiVersion))
        {
            errors.Add("Apim:ApiVersion is required for the AzureDeployments route.");
        }

        if (!IsPlaceholder(options.BaseUrl)
            && (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(baseUri.UserInfo)
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment)))
        {
            errors.Add("Apim:BaseUrl must be an HTTPS base URL without credentials, query, or fragment.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void AddPlaceholder(
        ICollection<string> errors,
        IConfiguration configuration,
        string key)
    {
        if (IsPlaceholder(configuration[key]))
        {
            errors.Add($"Configuration key '{key}' must be configured.");
        }
    }

    private static bool IsPlaceholder(string? value) =>
        string.Equals(value, ApimOptions.OrganizationPlaceholder, StringComparison.Ordinal);
}
