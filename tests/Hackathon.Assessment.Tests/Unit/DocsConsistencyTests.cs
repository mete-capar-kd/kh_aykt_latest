using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Contracts;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit;

public sealed class DocsConsistencyTests
{
    private static readonly string[] ExpectedSpecFiles =
    [
        "00-kesin-yasaklar.md",
        "01-urun-ve-kapsam.md",
        "02-runtime-zinciri.md",
        "03-solution-yapisi.md",
        "04-test-ve-kalite.md",
        "05-llm-rolleri.md",
        "06-tool-katmani-ve-record-finding.md",
        "07-maskeleme.md",
        "08-scannerlar.md",
        "09-metrikler.md",
        "10-puanlama.md",
        "11-api-sozlesmesi.md",
        "12-limitler-ve-snapshot.md",
        "13-apim-client.md",
        "14-telemetry.md",
        "15-aura-guvenlik.md",
        "16-config.md",
        "17-git-akisi.md"
    ];

    private static readonly string[] ExpectedMetricNames =
    [
        "Kod ve Proje Yapısı Standartları",
        "Kimlik Doğrulama ve Yetkilendirme",
        "Uygulama Güvenliği ve Secret Yönetimi",
        "Veri Yönetimi ve Entegrasyon Standartları",
        "Loglama, İzlenebilirlik ve APM",
        "Test ve Kod Kalitesi Standartları",
        "CI/CD ve Kaynak Kod Yönetimi",
        "Container ve Çalışma Ortamı Standartları",
        "Performans, Dayanıklılık ve Ölçeklenebilirlik",
        "Dokümantasyon ve Mimari Yönetişim"
    ];

    [Fact]
    public void ExactlyEighteenArchitectureSpecsArePresent()
    {
        var actual = Directory.GetFiles(
                Path.Combine(RepositoryRoot(), "docs/spec"), "*.md")
            .Select(path => new FileInfo(path).Name)
            .Where(name => name != "README.md")
            .ToArray();

        Assert.True(HasExpectedSpecFiles(actual));
    }

    [Fact]
    public void MetricsSpecContainsAllTenK2Names()
    {
        var metrics = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs/spec/09-metrikler.md"));

        foreach (var name in ExpectedMetricNames)
        {
            Assert.Contains(name, metrics, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("docs/spec/16-config.md", "2000")]
    [InlineData("docs/spec/16-config.md", "52428800")]
    [InlineData("docs/spec/06-tool-katmani-ve-record-finding.md", "500")]
    [InlineData("docs/spec/06-tool-katmani-ve-record-finding.md", "200")]
    [InlineData("docs/spec/06-tool-katmani-ve-record-finding.md", "1.500")]
    [InlineData("docs/spec/15-aura-guvenlik.md", "120 dk")]
    [InlineData("docs/spec/12-limitler-ve-snapshot.md", "140 sn")]
    [InlineData("docs/spec/12-limitler-ve-snapshot.md", "210 sn")]
    public void SpecsRetainRequiredLimits(string path, string limit)
    {
        var content = File.ReadAllText(Path.Combine(RepositoryRoot(), path));
        Assert.Contains(limit, content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigSpecKeysArePresentInEnvironmentTemplate()
    {
        var root = RepositoryRoot();
        var configSpec = File.ReadAllText(Path.Combine(root, "docs/spec/16-config.md"));
        var expected = Regex.Matches(configSpec, @"`(?<key>[A-Za-z][A-Za-z0-9_]*(?:__[A-Za-z0-9_]+)*)`")
            .Select(match => match.Groups["key"].Value)
            .Where(key => key.Contains("__", StringComparison.Ordinal)
                || key is "APP_VERSION" or "GIT_COMMIT_SHA"
                    or "APPLICATIONINSIGHTS_CONNECTION_STRING")
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var entries = File.ReadAllLines(Path.Combine(root, ".env.example"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(expected);
        Assert.Empty(MissingConfigurationKeys(expected, entries));
    }

    [Fact]
    public void MissingConfigurationKeysAreDetected()
    {
        var missing = MissingConfigurationKeys(
            new HashSet<string>(["Required__Key"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(["Required__Key"], missing);
    }

    [Fact]
    public void CopilotInstructionsStayShort()
    {
        var instructions = File.ReadAllText(Path.Combine(
            RepositoryRoot(), ".github/copilot-instructions.md"));

        Assert.True(InstructionsWithinLimit(instructions), "Copilot instructions exceed 4,000 characters.");
    }

    [Fact]
    public void CopilotInstructionLengthLimitIncludesBoundary()
    {
        Assert.True(InstructionsWithinLimit(new string('x', 4000)));
        Assert.False(InstructionsWithinLimit(new string('x', 4001)));
    }

    [Fact]
    public void RelativeDocumentationLinksResolve()
    {
        var root = RepositoryRoot();
        var markdownFiles = Directory.GetFiles(
                Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "README.md"));

        foreach (var markdownFile in markdownFiles)
        {
            var content = File.ReadAllText(markdownFile);
            Assert.Empty(FindBrokenRelativeLinks(root, markdownFile, content));
        }
    }

    [Fact]
    public void BrokenRelativeLinksAreDetected()
    {
        var root = RepositoryRoot();
        var markdownFile = Path.Combine(root, "docs", "missing-source.md");

        Assert.Equal(
            ["does-not-exist.md"],
            FindBrokenRelativeLinks(root, markdownFile, "[broken](does-not-exist.md)"));
    }

    [Fact]
    public void ApiExampleResponseDeserializesWithApplicationJsonContext()
    {
        var apiDocument = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/api.md"));
        var example = Regex.Matches(apiDocument, @"```json\s*(?<json>[\s\S]*?)```")
            .Select(match => match.Groups["json"].Value)
            .Single(json => json.Contains("\"answerType\"", StringComparison.Ordinal)
                && json.Contains("\"assessment\"", StringComparison.Ordinal));
        var options = new JsonSerializerOptions(AppJsonContext.Default.Options)
        {
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        var context = new AppJsonContext(options);

        var response = JsonSerializer.Deserialize(example, context.AskResponse);

        Assert.NotNull(response);
        Assert.Equal(10, response.Assessment?.Metrics.Length);
    }

    [Fact]
    public void MissingRequiredSpecIsDetected()
    {
        var actual = ExpectedSpecFiles.Where(name => name != "09-metrikler.md");

        Assert.False(HasExpectedSpecFiles(actual));
    }

    private static bool HasExpectedSpecFiles(IEnumerable<string> actual) =>
        new HashSet<string>(ExpectedSpecFiles, StringComparer.Ordinal)
            .SetEquals(actual);

    private static string[] MissingConfigurationKeys(
        IEnumerable<string> expected,
        ISet<string> actual) =>
        expected.Except(actual, StringComparer.Ordinal).ToArray();

    private static bool InstructionsWithinLimit(string instructions) =>
        instructions.Length <= 4000;

    private static string[] FindBrokenRelativeLinks(
        string root,
        string markdownFile,
        string content)
    {
        var brokenLinks = new List<string>();
        var linkPattern = new Regex(@"\[[^\]]+\]\((?<target>[^)]+)\)", RegexOptions.Compiled);

        foreach (Match match in linkPattern.Matches(content))
        {
            var target = match.Groups["target"].Value;
            if (Uri.TryCreate(target, UriKind.Absolute, out _)
                || target.StartsWith('#')
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Uri.UnescapeDataString(target.Split('#', 2)[0]);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var resolved = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(markdownFile)!, relativePath));
            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                brokenLinks.Add(target);
            }
        }

        return brokenLinks.ToArray();
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
