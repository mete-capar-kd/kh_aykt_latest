using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Scanners.Generic;

internal static class MetricSet
{
    public static ImmutableArray<MetricId> Of(params MetricId[] metrics) => [.. metrics];
}

public sealed class GenericSecretScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-SECRET-001", MetricSet.Of(MetricId.M03), "high", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            for (var line = 1; line <= file.LineCount; line++)
            {
                var source = file.GetLine(line).ToString();
                if (IsCommentLine(source.AsSpan()))
                {
                    continue;
                }

                var masked = Masker.Mask(source);
                if (!string.Equals(source, masked, StringComparison.Ordinal))
                {
                    yield return Candidate(snapshot, file, line,
                        "A configured secret or personal-data pattern is present.");
                }
            }
        }
    }
}

public sealed class GenericEnvironmentFileScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-ENV-001", MetricSet.Of(MetricId.M03), "high", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values)
        {
            var name = Path.GetFileName(file.Path);
            if (!(name.Equals(".env", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
                || name.Equals(".env.example", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".env.sample", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".env.template", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return Candidate(snapshot, file, 1,
                "A non-example environment file is present in the repository.");
        }
    }
}

public sealed class DockerRootUserScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-DOCKER-001", MetricSet.Of(MetricId.M08), "medium", ScannerStack.Generic)
{
    private static readonly Regex From = ScannerRegex.Compile(@"^\s*FROM(?:\s+--platform=\S+)?\s+\S+");
    private static readonly Regex AnyUser = ScannerRegex.Compile(@"^\s*USER\s+\S+");
    private static readonly Regex RootUser = ScannerRegex.Compile(@"^\s*USER\s+(?:root|0)(?:\s|$)");

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(IsDockerfile)
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var lastStage = 0;
            for (var line = 1; line <= file.LineCount; line++)
            {
                if (From.IsMatch(file.GetLine(line)))
                {
                    lastStage = line;
                }
            }

            if (lastStage == 0)
            {
                continue;
            }

            var userFound = false;
            for (var line = lastStage + 1; line <= file.LineCount; line++)
            {
                var span = file.GetLine(line);
                if (!IsCommentLine(span) && AnyUser.IsMatch(span))
                {
                    userFound = true;
                    if (RootUser.IsMatch(span))
                    {
                        yield return Candidate(snapshot, file, line,
                            "The final Docker build stage explicitly selects root.");
                    }
                }
            }

            if (!userFound)
            {
                yield return Candidate(snapshot, file, lastStage,
                    "The final Docker build stage does not declare a non-root USER.");
            }
        }
    }

    private static bool IsDockerfile(SnapshotFile file) =>
        Path.GetFileName(file.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);
}

public sealed class DockerImageTagScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "GEN-DOCKER-002", MetricSet.Of(MetricId.M08), "medium",
        ScannerStack.Generic, ScannerRegex.Compile(@"^\s*FROM(?:\s+--platform=\S+)?\s+(?<image>\S+)"))
{
    protected override string Rationale => "A Docker base image is untagged or uses the mutable latest tag.";

    protected override bool IsEligibleFile(string path) =>
        Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(file =>
                     Path.GetFileName(file.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)))
        {
            for (var line = 1; line <= file.LineCount; line++)
            {
                var span = file.GetLine(line);
                if (IsCommentLine(span))
                {
                    continue;
                }

                var match = Pattern.Match(span.ToString());
                if (!match.Success)
                {
                    continue;
                }

                var image = match.Groups["image"].Value;
                if (image.Contains("@sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var leaf = image[(image.LastIndexOf('/') + 1)..];
                var colon = leaf.LastIndexOf(':');
                if (colon < 0 || leaf[(colon + 1)..].Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    var scratch = image.Equals("scratch", StringComparison.OrdinalIgnoreCase);
                    yield return Candidate(
                        snapshot,
                        file,
                        line,
                        Rationale,
                        scratch,
                        scratch
                            ? "The special scratch base image intentionally has no version tag."
                            : null);
                }
            }
        }
    }
}

public sealed class DockerSecretArgumentScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "GEN-DOCKER-003",
        MetricSet.Of(MetricId.M03, MetricId.M08), "high", ScannerStack.Generic,
        ScannerRegex.Compile(@"(?i)^\s*(?:ARG|ENV)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<value>.*)$"))
{
    private static readonly Regex SecretName = ScannerRegex.Compile(
        @"(?i)(?:password|secret|token|api_?key|connection)");

    protected override bool IsEligibleFile(string path) =>
        Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "Dockerfile ARG/ENV assigns a value to a secret-like variable.";

    protected override bool IsMatch(ReadOnlySpan<char> line)
    {
        var match = Pattern.Match(line.ToString());
        if (!match.Success || !SecretName.IsMatch(match.Groups["name"].Value))
        {
            return false;
        }

        var assigned = match.Groups["value"].Value.AsSpan().TrimStart();
        if (assigned.IsEmpty)
        {
            return false;
        }

        return assigned[0] != '=' || !assigned[1..].Trim().IsEmpty;
    }
}

public sealed class DockerHealthcheckScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-DOCKER-004", MetricSet.Of(MetricId.M08), "low", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(file =>
                     Path.GetFileName(file.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ContainsDirective(file, "HEALTHCHECK"))
            {
                yield return Candidate(snapshot, file, 1, "Dockerfile does not declare HEALTHCHECK.");
            }
        }
    }

    private static bool ContainsDirective(SnapshotFile file, string directive)
    {
        for (var line = 1; line <= file.LineCount; line++)
        {
            var text = file.GetLine(line);
            if (!IsCommentLine(text)
                && text.TrimStart().StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class WorkflowPermissionsScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-WF-001", MetricSet.Of(MetricId.M07), "medium", ScannerStack.Generic)
{
    private static readonly Regex WriteAll = ScannerRegex.Compile(@"^\s*permissions\s*:\s*write-all\b");
    private static readonly Regex Permissions = ScannerRegex.Compile(@"^\s*permissions\s*:");

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in WorkflowFiles(snapshot))
        {
            var firstPermissions = 0;
            var writeAllLine = 0;
            for (var line = 1; line <= file.LineCount; line++)
            {
                var span = file.GetLine(line);
                if (IsCommentLine(span))
                {
                    continue;
                }

                if (Permissions.IsMatch(span.ToString()) && firstPermissions == 0)
                {
                    firstPermissions = line;
                }

                if (WriteAll.IsMatch(span.ToString()))
                {
                    writeAllLine = line;
                    break;
                }
            }

            if (writeAllLine != 0)
            {
                yield return Candidate(snapshot, file, writeAllLine,
                    "Workflow permissions use the broad write-all grant.");
            }
            else if (firstPermissions == 0)
            {
                yield return Candidate(snapshot, file, 1,
                    "Workflow file has no explicit permissions block.");
            }
        }
    }

    internal static IEnumerable<SnapshotFile> WorkflowFiles(RepositorySnapshot snapshot) =>
        snapshot.Files.Values.Where(file =>
            file.Path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            && (file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || file.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
}

public sealed class WorkflowPullRequestTargetScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "GEN-WF-002",
        MetricSet.Of(MetricId.M07, MetricId.M03), "high", ScannerStack.Generic,
        ScannerRegex.Compile(@"^\s*pull_request_target\s*:"))
{
    protected override bool IsEligibleFile(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    protected override string Rationale => "Workflow uses the privileged pull_request_target trigger.";
}

public sealed class WorkflowUnpinnedActionScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "GEN-WF-003", MetricSet.Of(MetricId.M07), "low",
        ScannerStack.Generic, ScannerRegex.Compile(@"^\s*(?:-\s*)?uses\s*:\s*(?<action>[^\s#]+)"))
{
    private static readonly Regex FullSha = ScannerRegex.Compile(@"\A[0-9a-fA-F]{40}\z");

    protected override bool IsEligibleFile(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    protected override string Rationale => "Workflow action reference is not pinned to a 40-character commit SHA.";

    protected override bool ShouldReportMatch(string path, ReadOnlySpan<char> text)
    {
        var match = Pattern.Match(text.ToString());
        if (!match.Success)
        {
            return false;
        }

        var action = match.Groups["action"].Value;
        var at = action.LastIndexOf('@');
        var reference = at >= 0 ? action[(at + 1)..] : "";
        return !FullSha.IsMatch(reference);
    }

    protected override (bool LikelyFalsePositive, string? Reason) GetFalsePositive(
        string path, SnapshotFile file, int line, ReadOnlySpan<char> text)
    {
        var match = Pattern.Match(text.ToString());
        if (!match.Success)
        {
            return (false, null);
        }

        var action = match.Groups["action"].Value;
        if (action.StartsWith("./", StringComparison.Ordinal))
        {
            return (true, "This is a local action reference rather than a third-party action.");
        }

        var at = action.LastIndexOf('@');
        var reference = at >= 0 ? action[(at + 1)..] : "";
        if (action.StartsWith("actions/", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("github/", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "A first-party action tag can be an intentional policy exception.");
        }

        return (false, null);
    }
}

public sealed class ReadmeMissingScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-DOC-001", MetricSet.Of(MetricId.M10), "medium", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        if (snapshot.Files.Values.Any(file =>
                !file.Path.Contains('/', StringComparison.Ordinal)
                && Path.GetFileName(file.Path).StartsWith("README", StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        var missing = new SnapshotFile("README.md", "", SnapshotFileRole.Docs);
        yield return Candidate(snapshot, missing, 1, "No root README file is present.",
            pathOverride: "README.md");
    }
}

public sealed class ReadmeAdequacyScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-DOC-002", MetricSet.Of(MetricId.M10), "low", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        var readme = snapshot.Files.Values
            .Where(file => !file.Path.Contains('/', StringComparison.Ordinal)
                && Path.GetFileName(file.Path).StartsWith("README", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (readme is null)
        {
            yield break;
        }

        var nonEmptyLines = 0;
        var hasInstall = false;
        var hasArchitecture = false;
        var installLine = 1;
        var architectureLine = 1;
        for (var line = 1; line <= readme.LineCount; line++)
        {
            var span = readme.GetLine(line);
            if (span.Trim().Length == 0)
            {
                continue;
            }

            nonEmptyLines++;
            var heading = span.TrimStart();
            if (!hasInstall
                && heading.StartsWith("#", StringComparison.Ordinal)
                && (heading.Contains("kurulum", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("installation", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("setup", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("getting started", StringComparison.OrdinalIgnoreCase)))
            {
                hasInstall = true;
                installLine = line;
            }

            if (!hasArchitecture
                && heading.StartsWith("#", StringComparison.Ordinal)
                && (heading.Contains("mimari", StringComparison.OrdinalIgnoreCase)
                    || heading.Contains("architecture", StringComparison.OrdinalIgnoreCase)))
            {
                hasArchitecture = true;
                architectureLine = line;
            }
        }

        var reason = nonEmptyLines < 30
            ? $"README has only {nonEmptyLines} non-empty lines (fewer than 30)."
            : null;
        var candidateLine = 1;
        if (reason is null && !hasInstall)
        {
            reason = "README has no installation or setup heading.";
            candidateLine = installLine;
        }
        else if (reason is null && !hasArchitecture)
        {
            reason = "README has no architecture heading.";
            candidateLine = architectureLine;
        }

        if (reason is not null)
        {
            yield return Candidate(snapshot, readme, candidateLine, reason);
        }
    }
}

public sealed class AdrMissingScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-DOC-003", MetricSet.Of(MetricId.M10), "low", ScannerStack.Generic)
{
    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        if (snapshot.Files.Keys.Any(path =>
                path.StartsWith("docs/adr/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("adr/", StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        var missing = new SnapshotFile("docs/adr/README.md", "", SnapshotFileRole.Docs);
        yield return Candidate(snapshot, missing, 1, "No ADR directory or record exists.",
            pathOverride: "docs/adr/README.md");
    }
}

public sealed class BroadGitignoreScanner(ISecretMasker masker) :
    ScannerBase(masker, "GEN-GITIGNORE-001", MetricSet.Of(MetricId.M07), "medium", ScannerStack.Generic)
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "*.md", "*.yml", "*.yaml", "*.json", ".github/", "prompts/", "docs/",
        "config/", "infra/", "Dockerfile"
    };

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        if (!snapshot.Files.TryGetValue(".gitignore", out var file))
        {
            yield break;
        }

        for (var line = 1; line <= file.LineCount; line++)
        {
            var text = file.GetLine(line);
            if (IsCommentLine(text))
            {
                continue;
            }

            var value = text.ToString();
            if (Forbidden.Contains(value))
            {
                yield return Candidate(snapshot, file, line,
                    $"The exact broad ignore pattern '{value}' can hide repository files.");
            }
        }
    }
}
