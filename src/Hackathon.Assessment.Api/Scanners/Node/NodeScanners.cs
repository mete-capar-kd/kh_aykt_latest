using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Scanners.Node;

internal static class NodeMetrics
{
    public static ImmutableArray<MetricId> Of(params MetricId[] metrics) => [.. metrics];
}

public sealed class ProcessEnvironmentScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NODE-ENV-001",
        NodeMetrics.Of(MetricId.M01, MetricId.M03), "low", ScannerStack.Node,
        ScannerRegex.Compile(@"\bprocess\.env\."))
{
    protected override bool IsEligibleFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = Path.GetFileName(path);
        if (!IsJavaScriptOrTypeScript(path)
            || normalized.StartsWith("config/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/config/", StringComparison.OrdinalIgnoreCase)
            || name.Equals("env.js", StringComparison.OrdinalIgnoreCase)
            || name.Equals("env.ts", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(name.EndsWith(".config.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".config.ts", StringComparison.OrdinalIgnoreCase));
    }

    protected override string Rationale => "Direct process.env access can bypass centralized configuration validation.";

    internal static bool IsJavaScriptOrTypeScript(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx";
}

public sealed class ShellExecutionScanner(ISecretMasker masker) :
    ScannerBase(masker, "NODE-EXEC-001", NodeMetrics.Of(MetricId.M03), "high", ScannerStack.Node)
{
    private static readonly System.Text.RegularExpressions.Regex InterpolatedCommand =
        ScannerRegex.Compile(
            """\bexec(?:Sync)?\s*\(\s*(?:`[^`]*\$\{|["'][^"']*["']\s*\+)""");

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(file =>
                     ProcessEnvironmentScanner.IsJavaScriptOrTypeScript(file.Path)
                     && file.Content.Contains("child_process", StringComparison.Ordinal)))
        {
            for (var line = 1; line <= file.LineCount; line++)
            {
                var text = file.GetLine(line);
                if (IsCommentLine(text) || !InterpolatedCommand.IsMatch(text))
                {
                    continue;
                }

                yield return Candidate(snapshot, file, line,
                    "exec/execSync constructs a shell command using interpolation or concatenation.");
            }
        }
    }
}

public sealed class OpenCorsScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NODE-CORS-001", NodeMetrics.Of(MetricId.M03),
        "medium", ScannerStack.Node,
        ScannerRegex.Compile("""\bcors\s*\(\s*\)|\borigin\s*:\s*["']\*["']"""))
{
    protected override bool IsEligibleFile(string path) =>
        ProcessEnvironmentScanner.IsJavaScriptOrTypeScript(path);

    protected override string Rationale => "CORS configuration allows every origin or uses defaults without options.";
}
