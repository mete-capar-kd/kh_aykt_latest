using System.ComponentModel.DataAnnotations;
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
    IHostEnvironment environment) : IValidateOptions<RepositoryOptions>
{
    private const string Placeholder = "<ORGANİZASYONDAN-ALINACAK>";

    public ValidateOptionsResult Validate(string? name, RepositoryOptions options)
    {
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var missingKeys = configuration
            .AsEnumerable()
            .Where(pair => string.Equals(pair.Value, Placeholder, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return missingKeys.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                missingKeys.Select(key => $"Configuration key '{key}' must be configured."));
    }
}
