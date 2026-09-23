using System.Collections.Immutable;
using System.Diagnostics;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Scanners;
using Hackathon.Assessment.Api.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Scanners;

public sealed class ScannerRegistryTests
{
    [Fact]
    public void UnknownStackRunsGenericScannersOnly()
    {
        var snapshot = Snapshot((".env", "KEY=value"), ("src/a.txt", "ordinary"));
        var result = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()))
            .ScanAll(snapshot);

        Assert.Contains(result, finding => finding.RuleId == "GEN-ENV-001");
        Assert.DoesNotContain(result, finding =>
            finding.RuleId.StartsWith("NET-", StringComparison.Ordinal)
            || finding.RuleId.StartsWith("PY-", StringComparison.Ordinal)
            || finding.RuleId.StartsWith("NODE-", StringComparison.Ordinal));
    }

    [Fact]
    public void MonorepoRunsGenericAndEveryDetectedLanguagePack()
    {
        var snapshot = Snapshot(
            ("MyApp.csproj", "<Project />"),
            ("package.json", "{}"),
            ("src/Program.cs", "[AllowAnonymous]"),
            ("src/server.js", "const port = process.env.PORT;"));
        var results = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()))
            .ScanAll(snapshot);
        var ids = results.Select(finding => finding.RuleId).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("GEN-DOC-001", ids);
        Assert.Contains("NET-AUTH-001", ids);
        Assert.Contains("NODE-ENV-001", ids);
    }

    [Fact]
    public void RunScannerFiltersByMetricThenOrdersSeverityPathAndLine()
    {
        var snapshot = Snapshot(
            ("Dockerfile", "FROM node\n"),
            (".github/workflows/build.yml", "pull_request_target:\n"));
        var registry = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()));

        var results = registry.Run(MetricId.M08, snapshot);

        Assert.NotEmpty(results.Candidates);
        Assert.All(results.Candidates, candidate =>
            Assert.Contains(MetricId.M08, candidate.MetricIds));
        Assert.DoesNotContain(results.Candidates, candidate => candidate.RuleId == "GEN-WF-002");
        Assert.Equal(results.Candidates.OrderBy(
                candidate => candidate.SeverityHint switch
                {
                    "critical" => 0,
                    "high" => 1,
                    "medium" => 2,
                    "low" => 3,
                    _ => 4
                })
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Line), results.Candidates);
    }

    [Fact]
    public void ScannerResultsAreCachedOncePerSnapshot()
    {
        var snapshot = Snapshot(("src/a.txt", "content"));
        var scanner = new CountingScanner();
        var registry = new ScannerRegistry([scanner]);

        var first = registry.ScanAll(snapshot);
        var second = registry.ScanAll(snapshot);

        Assert.Equal(1, scanner.ScanCount);
        Assert.Same(first, second);
    }

    [Fact]
    public void RunScannerCapsAt200AndReportsTotalAndTruncation()
    {
        var files = Enumerable.Range(0, 250)
            .Select(number => ($"secrets/{number:000}/.env", "KEY=value"))
            .ToArray();
        var snapshot = Snapshot(files);
        var result = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()))
            .Run(MetricId.M03, snapshot);

        Assert.Equal(250, result.Total);
        Assert.True(result.Truncated);
        Assert.Equal(200, result.Candidates.Length);
    }

    [Fact]
    public void TwoThousandFileScanCompletesUnderTwoSeconds()
    {
        var files = Enumerable.Range(0, 2000)
            .Select(number => ($"src/File{number:0000}.cs", "public class Sample { }"))
            .ToArray();
        var snapshot = Snapshot(files);
        var registry = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()));
        var timer = Stopwatch.StartNew();

        _ = registry.ScanAll(snapshot);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"Scanning took {timer.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void CommentLinesDoNotProduceRuleCandidates()
    {
        var snapshot = Snapshot(
            ("src/app.cs", "// [AllowAnonymous]\n/* new HttpClient( */\nvar safe = true;"),
            ("app.py", "# eval(payload)\nDEBUG = False"),
            ("src/server.js", "// process.env.KEY\napp.use(cors({origin: allowList}));"));
        var results = new ScannerRegistry(BuiltInScanners.Create(new SecretMasker()))
            .ScanAll(snapshot);

        Assert.DoesNotContain(results, finding =>
            finding.RuleId is "NET-AUTH-001" or "NET-DI-001" or "PY-EVAL-001"
                or "NODE-ENV-001" or "NODE-CORS-001");
    }

    private static RepositorySnapshot Snapshot(params (string Path, string Content)[] files) =>
        new(
            "org",
            "repo",
            "0123456789abcdef0123456789abcdef01234567",
            files.ToDictionary(
                file => file.Path,
                file => new SnapshotFile(file.Path, file.Content, SnapshotFileRole.Other),
                StringComparer.Ordinal));

    private sealed class CountingScanner : IScanner
    {
        private static readonly ImmutableArray<MetricId> ScannerMetrics = [MetricId.M01];
        public string Id => "COUNTING";
        public IReadOnlyList<MetricId> Metrics => ScannerMetrics;
        public int ScanCount { get; private set; }
        public bool AppliesTo(StackSignals signals) => true;

        public IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
        {
            ScanCount++;
            return [];
        }
    }
}
