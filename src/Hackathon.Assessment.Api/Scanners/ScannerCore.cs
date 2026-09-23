using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Scanners.DotNet;
using Hackathon.Assessment.Api.Scanners.Generic;
using Hackathon.Assessment.Api.Scanners.Node;
using Hackathon.Assessment.Api.Scanners.Python;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Tools;

namespace Hackathon.Assessment.Api.Scanners;

public sealed record StackSignals(bool DotNet, bool Python, bool Node);

public static class StackDetector
{
    public static StackSignals Detect(RepositorySnapshot snapshot)
    {
        var dotNet = false;
        var python = false;
        var node = false;
        foreach (var file in snapshot.Files.Values)
        {
            var name = Path.GetFileName(file.Path);
            var extension = Path.GetExtension(file.Path);
            dotNet |= extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
            python |= extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("setup.py", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            node |= name.Equals("package.json", StringComparison.OrdinalIgnoreCase);
        }

        return new StackSignals(dotNet, python, node);
    }
}

public static class FileRoleClassifier
{
    public static string Classify(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        var name = lower[(lower.LastIndexOf('/') + 1)..];

        if (lower.StartsWith("test/", StringComparison.Ordinal)
            || lower.StartsWith("tests/", StringComparison.Ordinal)
            || lower.Contains("/test/", StringComparison.Ordinal)
            || lower.Contains("/tests/", StringComparison.Ordinal)
            || lower.Contains(".tests/", StringComparison.Ordinal)
            || name.EndsWith("tests.cs", StringComparison.Ordinal)
            || name.EndsWith("test.cs", StringComparison.Ordinal)
            || name.StartsWith("test_", StringComparison.Ordinal)
            || name.EndsWith("_test.py", StringComparison.Ordinal)
            || name.Contains(".spec.", StringComparison.Ordinal)
            || name.Contains(".test.", StringComparison.Ordinal))
        {
            return "Test";
        }

        if (lower.Contains("/migrations/", StringComparison.Ordinal)
            || lower.StartsWith("migrations/", StringComparison.Ordinal))
        {
            return "Migration";
        }

        if (lower.Contains("/samples/", StringComparison.Ordinal)
            || lower.StartsWith("samples/", StringComparison.Ordinal)
            || lower.Contains("/examples/", StringComparison.Ordinal)
            || lower.StartsWith("examples/", StringComparison.Ordinal)
            || name.EndsWith(".example", StringComparison.Ordinal)
            || name.EndsWith(".sample", StringComparison.Ordinal)
            || name.EndsWith(".template", StringComparison.Ordinal)
            || name.Equals(".env.example", StringComparison.Ordinal))
        {
            return "Example";
        }

        if (lower.StartsWith("docs/", StringComparison.Ordinal)
            || lower.Contains("/docs/", StringComparison.Ordinal)
            || name.EndsWith(".md", StringComparison.Ordinal))
        {
            return "Docs";
        }

        if (lower.StartsWith(".github/workflows/", StringComparison.Ordinal))
        {
            return "Workflow";
        }

        if (name.StartsWith("appsettings", StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)
            || name.EndsWith(".config", StringComparison.Ordinal)
            || (name.EndsWith(".yml", StringComparison.Ordinal)
                || name.EndsWith(".yaml", StringComparison.Ordinal))
            || name.StartsWith(".env", StringComparison.Ordinal))
        {
            return "Config";
        }

        if (name.EndsWith("controller.cs", StringComparison.Ordinal)
            || lower.Contains("/controllers/", StringComparison.Ordinal)
            || lower.StartsWith("controllers/", StringComparison.Ordinal))
        {
            return "Controller";
        }

        return "App";
    }
}

public interface IScanner
{
    string Id { get; }
    IReadOnlyList<MetricId> Metrics { get; }
    bool AppliesTo(StackSignals signals);
    IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot);
}

public interface IScannerRegistry : IScannerRunner
{
    IReadOnlyList<CandidateFinding> ScanAll(RepositorySnapshot snapshot);
}

public sealed class ScannerRegistry(IEnumerable<IScanner> scanners) : IScannerRegistry
{
    public const int MaximumCandidates = 200;
    private readonly IScanner[] _scanners = [.. scanners];
    private readonly ConditionalWeakTable<RepositorySnapshot, Lazy<IReadOnlyList<CandidateFinding>>> _cache = new();

    public IReadOnlyList<CandidateFinding> ScanAll(RepositorySnapshot snapshot) =>
        _cache.GetValue(
            snapshot,
            key => new Lazy<IReadOnlyList<CandidateFinding>>(
                () => ScanOnce(key),
                LazyThreadSafetyMode.ExecutionAndPublication))
        .Value;

    public ScannerRunResult Run(MetricId metricId, RepositorySnapshot snapshot)
    {
        var ordered = ScanAll(snapshot)
            .Where(candidate => candidate.MetricIds.Contains(metricId))
            .OrderBy(candidate => SeverityRank(candidate.SeverityHint))
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Line)
            .ToArray();
        return new ScannerRunResult(
            [.. ordered.Take(MaximumCandidates)],
            ordered.Length > MaximumCandidates,
            ordered.Length);
    }

    private IReadOnlyList<CandidateFinding> ScanOnce(RepositorySnapshot snapshot)
    {
        var signals = StackDetector.Detect(snapshot);
        var results = new List<CandidateFinding>();
        var unique = new HashSet<(string RuleId, string Path, int Line)>();
        foreach (var scanner in _scanners)
        {
            if (!scanner.AppliesTo(signals))
            {
                continue;
            }

            foreach (var candidate in scanner.Scan(snapshot))
            {
                if (unique.Add((candidate.RuleId, candidate.Path, candidate.Line)))
                {
                    results.Add(candidate);
                }
            }
        }

        return results.ToImmutableArray();
    }

    private static int SeverityRank(string severity) =>
        severity switch
        {
            "critical" => 0,
            "high" => 1,
            "medium" => 2,
            "low" => 3,
            _ => 4
        };
}

public enum ScannerStack
{
    Generic,
    DotNet,
    Python,
    Node
}

public abstract class ScannerBase(
    ISecretMasker masker,
    string id,
    ImmutableArray<MetricId> metrics,
    string severityHint,
    ScannerStack stack) : IScanner
{
    public string Id { get; } = id;
    public IReadOnlyList<MetricId> Metrics { get; } = metrics;
    protected ISecretMasker Masker { get; } = masker;
    protected string SeverityHint { get; } = severityHint;
    protected ScannerStack Stack { get; } = stack;

    public bool AppliesTo(StackSignals signals) =>
        Stack switch
        {
            ScannerStack.Generic => true,
            ScannerStack.DotNet => signals.DotNet,
            ScannerStack.Python => signals.Python,
            ScannerStack.Node => signals.Node,
            _ => false
        };

    public abstract IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot);

    protected CandidateFinding Candidate(
        RepositorySnapshot snapshot,
        SnapshotFile file,
        int line,
        string rationale,
        bool forceFalsePositive = false,
        string? falsePositiveReason = null,
        string? pathOverride = null)
    {
        var path = pathOverride ?? file.Path;
        var role = FileRoleClassifier.Classify(path);
        var likelyFalsePositive = forceFalsePositive || role is "Test" or "Example";
        var reason = falsePositiveReason
            ?? (role switch
            {
                "Test" => "The pattern is in a test file and may be an intentional test fixture.",
                "Example" => "The pattern is in an example or sample and may be illustrative.",
                _ when forceFalsePositive => "The observed context can be a legitimate use.",
                _ => null
            });
        var first = Math.Max(1, line - 15);
        var last = Math.Min(file.LineCount, line + 15);
        var context = new StringBuilder();
        for (var current = first; current <= last; current++)
        {
            if (current > first)
            {
                context.Append('\n');
            }

            context.Append(current);
            context.Append(": ");
            context.Append(Masker.Mask(file.GetLine(current).ToString()));
        }

        return new CandidateFinding(
            Id,
            Metrics.ToImmutableArray(),
            path,
            line,
            SeverityHint,
            context.ToString(),
            role,
            likelyFalsePositive,
            reason,
            $"aday — bağlayıcı değil: {rationale}");
    }

    protected static bool IsCommentLine(ReadOnlySpan<char> line)
    {
        line = line.TrimStart();
        return line.StartsWith("//", StringComparison.Ordinal)
            || line.StartsWith("#", StringComparison.Ordinal)
            || line.StartsWith("/*", StringComparison.Ordinal)
            || line.StartsWith("*/", StringComparison.Ordinal)
            || line.StartsWith("* ", StringComparison.Ordinal);
    }
}

public static class BuiltInScanners
{
    public static IScanner[] Create(ISecretMasker masker) =>
    [
        new GenericSecretScanner(masker),
        new GenericEnvironmentFileScanner(masker),
        new DockerRootUserScanner(masker),
        new DockerImageTagScanner(masker),
        new DockerSecretArgumentScanner(masker),
        new DockerHealthcheckScanner(masker),
        new WorkflowPermissionsScanner(masker),
        new WorkflowPullRequestTargetScanner(masker),
        new WorkflowUnpinnedActionScanner(masker),
        new ReadmeMissingScanner(masker),
        new ReadmeAdequacyScanner(masker),
        new AdrMissingScanner(masker),
        new BroadGitignoreScanner(masker),
        new AllowAnonymousScanner(masker),
        new BlockingCallScanner(masker),
        new EmptyCatchScanner(masker),
        new AppSettingsConnectionStringScanner(masker),
        new AllowAnyOriginScanner(masker),
        new NewHttpClientScanner(masker),
        new BuildServiceProviderScanner(masker),
        new DynamicEvalScanner(masker),
        new DisabledTlsVerificationScanner(masker),
        new FStringSqlScanner(masker),
        new BareExceptScanner(masker),
        new PythonDebugScanner(masker),
        new ProcessEnvironmentScanner(masker),
        new ShellExecutionScanner(masker),
        new OpenCorsScanner(masker)
    ];
}

public abstract class RegexLineScanner(
    ISecretMasker masker,
    string id,
    ImmutableArray<MetricId> metrics,
    string severityHint,
    ScannerStack stack,
    Regex regex) : ScannerBase(masker, id, metrics, severityHint, stack)
{
    protected Regex Pattern { get; } = regex;

    protected virtual bool IsEligibleFile(string path) => true;
    protected virtual bool IsMatch(ReadOnlySpan<char> line) => Pattern.IsMatch(line);
    protected virtual bool ShouldReportMatch(string path, ReadOnlySpan<char> text) => true;
    protected virtual (bool LikelyFalsePositive, string? Reason) GetFalsePositive(
        string path,
        SnapshotFile file,
        int line,
        ReadOnlySpan<char> text) => (false, null);
    protected virtual string Rationale => $"Rule {Id} matched a candidate pattern.";

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (!IsEligibleFile(file.Path))
            {
                continue;
            }

            for (var lineNumber = 1; lineNumber <= file.LineCount; lineNumber++)
            {
                var line = file.GetLine(lineNumber);
                if (IsCommentLine(line) || !IsMatch(line) || !ShouldReportMatch(file.Path, line))
                {
                    continue;
                }

                var (falsePositive, reason) = GetFalsePositive(file.Path, file, lineNumber, line);
                yield return Candidate(snapshot, file, lineNumber, Rationale, falsePositive, reason);
            }
        }
    }
}

public static class ScannerRegex
{
    public static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    public static int LineAt(SnapshotFile file, int characterIndex)
    {
        var starts = file.LineStarts;
        var index = Array.BinarySearch(starts, characterIndex);
        return index >= 0 ? index + 1 : ~index;
    }
}
