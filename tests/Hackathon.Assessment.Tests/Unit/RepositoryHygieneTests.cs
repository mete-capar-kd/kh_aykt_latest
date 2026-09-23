using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit;

public sealed class RepositoryHygieneTests
{
    private static readonly string[] ExpectedConfigurationKeys =
    [
        "APP_VERSION", "GIT_COMMIT_SHA",
        "Apim__BaseUrl", "Apim__Auth__Scheme", "Apim__Auth__HeaderName",
        "Apim__Auth__Key", "Apim__Auth__Scope", "Apim__Deployments__Cheap",
        "Apim__Deployments__Strong", "Apim__AttemptTimeoutSeconds", "Apim__MaxAttempts",
        "EntraId__Instance", "EntraId__TenantId", "EntraId__Audience",
        "EntraId__RequiredRoles__0", "EntraId__RequiredScopes__0",
        "Repository__DefaultUrl", "Repository__DefaultRef",
        "Repository__AllowedInputHosts__0", "Repository__AllowedDownloadHosts__0",
        "Repository__AllowedDownloadHosts__1", "Repository__GitHubToken",
        "Assessment__MaxFiles", "Assessment__MaxTotalBytes",
        "Assessment__ProfilerTimeoutSeconds", "Assessment__MetricTimeoutSeconds",
        "Assessment__SynthesizerTimeoutSeconds", "Assessment__GlobalTimeoutSeconds",
        "Assessment__MaxConcurrentFullAssessments",
        "Cache__MetricResultTtlMinutes", "Cache__MetricResultMaxEntries",
        "Cache__SnapshotMaxBytes", "RateLimit__PermitsPerMinute",
        "RateLimit__QueueLimit", "RateLimit__ExemptClientIds__0",
        "Safety__CanaryToken", "APPLICATIONINSIGHTS_CONNECTION_STRING",
        "Telemetry__Team", "Telemetry__Application", "Telemetry__Environment"
    ];

    private static readonly string[] SecretKeys =
    [
        "Apim__Auth__Key", "Repository__GitHubToken",
        "Safety__CanaryToken", "APPLICATIONINSIGHTS_CONNECTION_STRING"
    ];

    [Fact]
    public void BroadIgnoreRulesAreAbsent()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "*.json", "*.md", "*.yml", "*.yaml", ".github/",
            "prompts/", "docs/", "config/", "infra/", "Dockerfile"
        };
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".gitignore"));

        Assert.DoesNotContain(lines, line => forbidden.Contains(line.Trim()));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("appsettings.Local.json")]
    [InlineData("secrets.json")]
    [InlineData("local.settings.json")]
    public void LocalSecretsAreIgnored(string path)
    {
        Assert.Equal(0, GitCheckIgnore(path));
    }

    [Fact]
    public void ExampleEnvironmentIsNotIgnored()
    {
        Assert.Equal(1, GitCheckIgnore(".env.example"));
    }

    [Fact]
    public void ConfigurationTemplatesContainEveryNonSecretKey()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".env.example"));
        var entries = lines
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        Assert.Equal(
            ExpectedConfigurationKeys.Order(StringComparer.Ordinal),
            entries.Keys.Order(StringComparer.Ordinal));

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src/Hackathon.Assessment.Api/appsettings.json")));
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(json.RootElement, "", flattened);

        foreach (var key in SecretKeys)
        {
            Assert.Equal("", entries[key]);
            Assert.False(flattened.ContainsKey(key), $"Secret key {key} must not be in appsettings.json");
        }

        foreach (var key in ExpectedConfigurationKeys.Except(SecretKeys))
        {
            Assert.True(flattened.ContainsKey(key), $"Missing {key} in appsettings.json");
            Assert.Equal(entries[key], flattened[key]);
        }
    }

    [Fact]
    public void PackageVersionsArePinnedAndFixturesAreNotCompiled()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));
        var versions = props.Descendants("PackageVersion").ToArray();
        var packageIds = versions
            .Select(item => item.Attribute("Include")?.Value ?? "")
            .Order(StringComparer.Ordinal);
        var expectedPackageIds = new[]
        {
            "coverlet.collector",
            "Microsoft.AspNetCore.Mvc.Testing",
            "Microsoft.NET.Test.Sdk",
            "NetArchTest.Rules",
            "NSubstitute",
            "xunit",
            "xunit.runner.visualstudio"
        }.Order(StringComparer.Ordinal);
        Assert.Equal(expectedPackageIds, packageIds);
        Assert.All(versions, item =>
        {
            var version = item.Attribute("Version")?.Value ?? "";
            Assert.NotEmpty(version);
            Assert.DoesNotContain('*', version);
            Assert.DoesNotContain('-', version);
        });

        var project = XDocument.Load(Path.Combine(
            RepositoryRoot(), "tests/Hackathon.Assessment.Tests/Hackathon.Assessment.Tests.csproj"));
        Assert.All(project.Descendants("PackageReference"), reference =>
            Assert.Null(reference.Attribute("Version")));
        Assert.Contains(project.Descendants("Compile"), item =>
            item.Attribute("Remove")?.Value == "Fixtures/**");
        Assert.Contains(project.Descendants("None"), item =>
            item.Attribute("Remove")?.Value == "Fixtures/**");
        Assert.Contains(project.Descendants("None"), item =>
            item.Attribute("Include")?.Value == "Fixtures/**"
            && item.Attribute("CopyToOutputDirectory")?.Value == "PreserveNewest");
    }

    private static void Flatten(
        JsonElement element, string prefix, Dictionary<string, string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Flatten(property.Value, Append(prefix, property.Name), values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                Flatten(item, Append(prefix, (index++).ToString()), values);
            }
        }
        else
        {
            values.Add(prefix, element.ToString());
        }
    }

    private static string Append(string prefix, string segment) =>
        prefix.Length == 0 ? segment : $"{prefix}__{segment}";

    private static int GitCheckIgnore(string path)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardError = true
        };
        start.ArgumentList.Add("check-ignore");
        start.ArgumentList.Add("--no-index");
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git check-ignore.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new TimeoutException("git check-ignore timed out.");
        }
        if (process.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
        return process.ExitCode;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
