using System.ComponentModel.DataAnnotations;
using Hackathon.Assessment.Api.Auth;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Options;

public sealed class RepositoryOptions
{
    public const string SectionName = "Repository";

    [Required]
    public string DefaultUrl { get; set; } = "";

    [Required]
    [RegularExpression(@"^(?!.*\.\.)[A-Za-z0-9._/-]{1,200}$")]
    public string DefaultRef { get; set; } = "main";

    [MinLength(1)]
    public string[] AllowedInputHosts { get; set; } = ["github.com"];

    [MinLength(1)]
    public string[] AllowedDownloadHosts { get; set; } =
        ["api.github.com", "codeload.github.com"];

    public string? GitHubToken { get; set; }
}

public sealed class OrganizationPlaceholderValidator(
    IConfiguration configuration,
    IHostEnvironment environment) :
    IValidateOptions<RepositoryOptions>,
    IValidateOptions<EntraIdOptions>
{
    public ValidateOptionsResult Validate(string? name, RepositoryOptions options)
        => ValidateConfiguration();

    public ValidateOptionsResult Validate(string? name, EntraIdOptions options)
    {
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var missingKeys = configuration
            .AsEnumerable()
            .Where(pair => string.Equals(
                pair.Value,
                EntraIdOptions.OrganizationPlaceholder,
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (string.Equals(
            options.TenantId,
            EntraIdOptions.OrganizationPlaceholder,
            StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(options.TenantId))
        {
            missingKeys.Add($"{EntraIdOptions.SectionName}:TenantId");
        }

        if (string.Equals(
            options.Audience,
            EntraIdOptions.OrganizationPlaceholder,
            StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(options.Audience))
        {
            missingKeys.Add($"{EntraIdOptions.SectionName}:Audience");
        }

        return ToValidationResult(missingKeys);
    }

    private ValidateOptionsResult ValidateConfiguration()
    {
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var missingKeys = configuration
            .AsEnumerable()
            .Where(pair => string.Equals(
                pair.Value,
                EntraIdOptions.OrganizationPlaceholder,
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        return ToValidationResult(missingKeys);
    }

    private static ValidateOptionsResult ToValidationResult(HashSet<string> missingKeys)
    {
        return missingKeys.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                missingKeys
                    .Order(StringComparer.Ordinal)
                    .Select(key => $"Configuration key '{key}' must be configured."));
    }
}
