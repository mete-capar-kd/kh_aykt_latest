using System.Collections.Immutable;
using System.Diagnostics;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Scanners;
using Hackathon.Assessment.Api.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Scanners;

public sealed class ScannerRuleTests
{
    private static readonly string ProviderSecret =
        string.Concat("gh", "p_", new string('A', 30));

    public static IEnumerable<object[]> PositiveCases()
    {
        yield return Case("GEN-SECRET-001", "src/app.cs", $"const api_key={ProviderSecret}");
        yield return Case("GEN-ENV-001", ".env.production", "PORT=8080");
        yield return Case("GEN-DOCKER-001", "Dockerfile", "FROM node:20\nRUN echo ready");
        yield return Case("GEN-DOCKER-001", "Dockerfile", "FROM node:20\nUSER root");
        yield return Case("GEN-DOCKER-002", "Dockerfile", "FROM node\nRUN echo ready");
        yield return Case("GEN-DOCKER-002", "Dockerfile", "FROM node:latest");
        yield return Case("GEN-DOCKER-003", "Dockerfile", "ARG API_KEY=secret-value");
        yield return Case("GEN-DOCKER-003", "Dockerfile", "ENV DB_PASSWORD=placeholder-value");
        yield return Case("GEN-DOCKER-003", "Dockerfile", "ENV ACCESS_TOKEN value");
        yield return Case("GEN-DOCKER-004", "Dockerfile", "FROM node:20");
        yield return Case("GEN-WF-001", ".github/workflows/build.yml", "permissions: write-all");
        yield return Case("GEN-WF-001", ".github/workflows/build.yml", "name: Build\njobs:\n  test:\n    runs-on: ubuntu-latest");
        yield return Case("GEN-WF-002", ".github/workflows/pr.yml", "on:\n  pull_request_target:");
        yield return Case("GEN-WF-003", ".github/workflows/ci.yml", "      - uses: vendor/action@v1");
        yield return Case("GEN-DOC-001", "src/app.cs", "public class App {}");
        yield return Case("GEN-DOC-002", "README.md", "# Project\n\nNo installation or architecture.");
        yield return Case("GEN-DOC-003", "README.md", "# Project\n");
        yield return Case("GEN-GITIGNORE-001", ".gitignore", "*.yaml");
        yield return Case("NET-AUTH-001", "src/Endpoint.cs", "[AllowAnonymous]");
        yield return Case("NET-ASYNC-001", "src/Service.cs", "Task<string> task; var data = task.Result;");
        yield return Case("NET-EXC-001", "src/Service.cs", "try {\n DoWork();\n} catch (Exception) {\n}");
        yield return Case("NET-CFG-001", "appsettings.Production.json",
            string.Concat("{\"ConnectionStrings\":{\"Main\":\"Server=db;",
                "Password", "=abc\"}}"));
        yield return Case("NET-CORS-001", "src/Program.cs", "policy.AllowAnyOrigin();");
        yield return Case("NET-DI-001", "src/Service.cs", "var client = new HttpClient();");
        yield return Case("NET-DI-002", "src/Program.cs", "var provider = services.BuildServiceProvider();");
        yield return Case("PY-EVAL-001", "app.py", "result = eval(user_input)");
        yield return Case("PY-EVAL-001", "worker.py", "exec(payload)");
        yield return Case("PY-TLS-001", "client.py", "requests.get(url, verify=False)");
        yield return Case("PY-SQL-001", "db.py", "query = f\"SELECT * FROM users WHERE id = {user_id}\"");
        yield return Case("PY-EXC-001", "handler.py", "except:");
        yield return Case("PY-DEBUG-001", "settings.py", "DEBUG = True");
        yield return Case("NODE-ENV-001", "src/server.js", "const port = process.env.PORT;");
        yield return Case("NODE-EXEC-001", "src/server.js", "const { exec } = require('child_process');\nexec('ls ' + input);");
        yield return Case("NODE-EXEC-001", "src/server.js", "const { exec } = require('child_process');\nexec(`ls ${input}`);");
        yield return Case("NODE-CORS-001", "src/server.js", "app.use(cors());");
        yield return Case("NODE-CORS-001", "src/server.ts", "cors({origin: '*'});");
    }

    public static IEnumerable<object[]> NegativeCases()
    {
        yield return Case("GEN-SECRET-001", "src/app.cs", "const api_key=none");
        yield return Case("GEN-ENV-001", ".env.example", "PORT=8080");
        yield return Case("GEN-ENV-001", ".environment", "PORT=8080");
        yield return Case("GEN-DOCKER-001", "Dockerfile", "FROM node:20\nUSER app");
        yield return Case("GEN-DOCKER-002", "Dockerfile", "FROM node:20");
        yield return Case("GEN-DOCKER-003", "Dockerfile", "ARG API_KEY");
        yield return Case("GEN-DOCKER-003", "Dockerfile", "ENV PASSWORD");
        yield return Case("GEN-DOCKER-004", "Dockerfile", "FROM node:20\nHEALTHCHECK CMD true");
        yield return Case("GEN-WF-001", ".github/workflows/build.yml", "permissions:\n  contents: read");
        yield return Case("GEN-WF-002", ".github/workflows/pr.yml", "on:\n  pull_request:");
        yield return Case("GEN-WF-003", ".github/workflows/ci.yml", "      - uses: vendor/action@0123456789abcdef0123456789abcdef01234567");
        yield return Case("GEN-DOC-001", "README.md", "# Project");
        yield return Case("GEN-DOC-002", "README.md", ValidReadme());
        yield return Case("GEN-DOC-003", "docs/adr/0001-decision.md", "# Decision");
        yield return Case("GEN-GITIGNORE-001", ".gitignore", "# *.yaml\n*.tmp");
        yield return Case("NET-AUTH-001", "tests/Api.Tests/FakeTests.cs", "[AllowAnonymous]", likelyFalsePositive: true);
        yield return Case("NET-AUTH-001", "src/HealthEndpoint.cs", "[AllowAnonymous]", likelyFalsePositive: true);
        yield return Case("NET-ASYNC-001", "src/Service.cs", "var description = \"Result\";");
        yield return Case("NET-ASYNC-001", "src/Service.cs", "var data = SomeTask.Result;", likelyFalsePositive: true);
        yield return Case("NET-EXC-001", "src/Service.cs", "try {\n DoWork();\n} catch (Exception ex) {\n Log(ex);\n}");
        yield return Case("NET-CFG-001", "appsettings.json", """{"ConnectionStrings":{"Main":"Server=db"}}""");
        yield return Case("NET-CFG-001", "appsettings.json", """{"Other":{"Main":"Password=not-in-connection-section"}}""");
        yield return Case("NET-CORS-001", "src/Program.cs", "policy.WithOrigins(\"https://app.example.test\");");
        yield return Case("NET-DI-001", "src/Service.cs", "var client = factory.CreateClient();");
        yield return Case("NET-DI-001", "tests/Api.Tests/FakeTests.cs", "var client = new HttpClient();");
        yield return Case("NET-DI-002", "src/Startup.cs", "services.AddControllers();");
        yield return Case("NET-DI-002", "src/Service.cs", "var provider = services.BuildServiceProvider();");
        yield return Case("PY-EVAL-001", "app.py", "result = evaluate(user_input)");
        yield return Case("PY-EVAL-001", "app.py", "# eval(user_input)");
        yield return Case("PY-TLS-001", "client.py", "requests.get(url, verify=True)");
        yield return Case("PY-SQL-001", "db.py", """cursor.execute("SELECT * FROM users WHERE id=?", [user_id])""");
        yield return Case("PY-EXC-001", "handler.py", "except ValueError:");
        yield return Case("PY-EXC-001", "handler.py", "# except:");
        yield return Case("PY-DEBUG-001", "settings.py", "DEBUG = False");
        yield return Case("NODE-ENV-001", "config/server.config.js", "const port = process.env.PORT;");
        yield return Case("NODE-ENV-001", "src/env.ts", "const port = process.env.PORT;");
        yield return Case("NODE-EXEC-001", "src/server.js", "const { exec } = require('child_process');\nexec('ls');");
        yield return Case("NODE-EXEC-001", "src/server.js", "exec('ls ' + input);");
        yield return Case("NODE-CORS-001", "src/server.js", "app.use(cors({origin: allowedOrigins}));");
    }

    [Theory]
    [MemberData(nameof(PositiveCases))]
    public void EveryRuleHasAnInfringingFixture(
        string ruleId, string path, string content, bool likelyFalsePositive)
    {
        var matches = Scan(ruleId, path, content);
        Assert.NotEmpty(matches);
        if (likelyFalsePositive)
        {
            Assert.Contains(matches, candidate => candidate.LikelyFalsePositive);
        }
    }

    [Theory]
    [MemberData(nameof(NegativeCases))]
    public void EveryRuleHasALegitimateOrNegativeFixture(
        string ruleId, string path, string content, bool expectedFalsePositive)
    {
        var matches = Scan(ruleId, path, content);
        if (expectedFalsePositive)
        {
            Assert.Contains(matches, candidate => candidate.LikelyFalsePositive);
        }
        else
        {
            Assert.Empty(matches);
        }
    }

    [Fact]
    public void SecretCandidateContextIsMaskedAndClippedAtBothFileEnds()
    {
        var lines = Enumerable.Range(1, 120).Select(line => $"line {line}").ToArray();
        lines[2] = string.Concat("api", "_key=", new string('S', 24));
        lines[99] = string.Concat("token=", new string('T', 24));
        var candidates = Scan("GEN-SECRET-001", "src/app.cs", string.Join('\n', lines));

        Assert.Equal(2, candidates.Length);
        var first = candidates.Single(candidate => candidate.Line == 3);
        var middle = candidates.Single(candidate => candidate.Line == 100);
        Assert.StartsWith("1:", first.Context, StringComparison.Ordinal);
        Assert.Contains("18: line 18", first.Context, StringComparison.Ordinal);
        Assert.StartsWith("85:", middle.Context, StringComparison.Ordinal);
        Assert.Contains("115: line 115", middle.Context, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 24), first.Context, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('T', 24), middle.Context, StringComparison.Ordinal);
        Assert.StartsWith("aday — bağlayıcı değil:", first.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/ExampleTests.cs", "Test")]
    [InlineData("src/Migrations/20260101.sql", "Migration")]
    [InlineData("samples/appsettings.example", "Example")]
    [InlineData(".env.example", "Example")]
    [InlineData("src/appsettings.Production.json", "Config")]
    [InlineData(".github/workflows/build.yml", "Workflow")]
    [InlineData("src/Controllers/HomeController.cs", "Controller")]
    [InlineData("src/HomeController.cs", "Controller")]
    [InlineData("src/service.cs", "App")]
    public void FileRoleClassifierUsesDocumentedPriority(string path, string role) =>
        Assert.Equal(role, FileRoleClassifier.Classify(path));

    [Fact]
    public void FirstPartyVersionTagIsRetainedAsLikelyFalsePositive()
    {
        var candidates = Scan(
            "GEN-WF-003",
            ".github/workflows/ci.yml",
            "      - uses: actions/checkout@v4");

        var candidate = Assert.Single(candidates);
        Assert.True(candidate.LikelyFalsePositive);
        Assert.Contains("first-party", candidate.FalsePositiveReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRegisteredRuleHasPositiveAndNegativeCoverage()
    {
        var positiveIds = PositiveCases().Select(data => (string)data[0]!)
            .ToHashSet(StringComparer.Ordinal);
        var negativeIds = NegativeCases().Select(data => (string)data[0]!)
            .ToHashSet(StringComparer.Ordinal);
        var registeredIds = BuiltInScanners.Create(new SecretMasker())
            .Select(scanner => scanner.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(28, registeredIds.Count);
        Assert.Equal(
            registeredIds.Order(StringComparer.Ordinal),
            positiveIds.Order(StringComparer.Ordinal));
        Assert.Equal(
            registeredIds.Order(StringComparer.Ordinal),
            negativeIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RuleDocumentationListsEveryRegisteredRule()
    {
        var scannerIds = BuiltInScanners.Create(new SecretMasker())
            .Select(scanner => scanner.Id)
            .ToArray();
        var documentation = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/tools.md"));
        var ruleRows = documentation.Split('\n')
            .Where(line => line.StartsWith("| GEN-", StringComparison.Ordinal)
                || line.StartsWith("| NET-", StringComparison.Ordinal)
                || line.StartsWith("| PY-", StringComparison.Ordinal)
                || line.StartsWith("| NODE-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(28, ruleRows.Length);
        Assert.All(scannerIds, id => Assert.Contains($"| {id} |", documentation, StringComparison.Ordinal));
    }

    private static object[] Case(
        string ruleId,
        string path,
        string content,
        bool likelyFalsePositive = false) =>
        [ruleId, path, content, likelyFalsePositive];

    private static CandidateFinding[] Scan(string ruleId, string path, string content)
    {
        var files = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        if (path.Length > 0)
        {
            files[path] = new SnapshotFile(path, content, SnapshotFileRole.App);
        }

        if (ruleId.StartsWith("NET-", StringComparison.Ordinal))
        {
            files.TryAdd("test-project.csproj",
                new SnapshotFile("test-project.csproj", "<Project/>", SnapshotFileRole.Config));
        }

        if (ruleId.StartsWith("NODE-", StringComparison.Ordinal))
        {
            files.TryAdd("package.json",
                new SnapshotFile("package.json", "{}", SnapshotFileRole.Config));
        }

        var snapshot = new RepositorySnapshot("owner", "repo", new string('a', 40), files);
        return new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()))
            .ScanAll(snapshot)
            .Where(candidate => candidate.RuleId == ruleId)
            .ToArray();
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

    private static string ValidReadme() =>
        """
        # Sample

        ## Installation

        ## Architecture
        """ + "\n" + string.Join('\n',
            Enumerable.Range(1, 30).Select(number => $"This is useful documentation line {number}."));
}
