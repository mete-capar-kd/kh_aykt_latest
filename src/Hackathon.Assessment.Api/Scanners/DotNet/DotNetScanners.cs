using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Scanners.DotNet;

internal static class DotNetMetrics
{
    public static ImmutableArray<MetricId> Of(params MetricId[] metrics) => [.. metrics];
}

public sealed class AllowAnonymousScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NET-AUTH-001", DotNetMetrics.Of(MetricId.M02),
        "medium", ScannerStack.DotNet, ScannerRegex.Compile(@"\[\s*AllowAnonymous\s*\]"))
{
    protected override string Rationale => "[AllowAnonymous] may expose an endpoint without authentication.";

    protected override (bool LikelyFalsePositive, string? Reason) GetFalsePositive(
        string path, SnapshotFile file, int line, ReadOnlySpan<char> text)
    {
        if (FileRoleClassifier.Classify(path) == "Test")
        {
            return (true, "AllowAnonymous is in a test file and may be used to verify anonymous access.");
        }

        if (path.Contains("health", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Health endpoints can intentionally be anonymous.");
        }

        return (false, null);
    }
}

public sealed class BlockingCallScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NET-ASYNC-001", DotNetMetrics.Of(MetricId.M09),
        "medium", ScannerStack.DotNet,
        ScannerRegex.Compile(@"\.Result\b|\.Wait\s*\(\s*\)|\.GetAwaiter\s*\(\s*\)\.GetResult\s*\(\s*\)"))
{
    private static readonly Regex AsyncContext = ScannerRegex.Compile(@"\b(?:Task|Async|await)\b");
    protected override string Rationale => "A blocking task wait may occupy a request thread.";

    protected override (bool LikelyFalsePositive, string? Reason) GetFalsePositive(
        string path, SnapshotFile file, int line, ReadOnlySpan<char> text)
    {
        if (text.Contains(".Result", StringComparison.Ordinal)
            && !AsyncContext.IsMatch(text))
        {
            return (true, "The .Result expression has no Task/Async/await signal on the same line.");
        }

        return (false, null);
    }
}

public sealed class EmptyCatchScanner(ISecretMasker masker) :
    ScannerBase(masker, "NET-EXC-001", DotNetMetrics.Of(MetricId.M05),
        "medium", ScannerStack.DotNet)
{
    private static readonly Regex EmptyCatch = ScannerRegex.Compile(
        @"catch\s*(?:\([^)]*\))?\s*\{\s*\}");

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(file =>
                     file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in EmptyCatch.Matches(file.Content))
            {
                var line = ScannerRegex.LineAt(file, match.Index);
                if (!ScannerBase.IsCommentLine(file.GetLine(line)))
                {
                    yield return Candidate(snapshot, file, line,
                        "Empty catch block silently discards an exception.");
                }
            }
        }
    }
}

public sealed class AppSettingsConnectionStringScanner(ISecretMasker masker) :
    ScannerBase(masker, "NET-CFG-001", DotNetMetrics.Of(MetricId.M03),
        "high", ScannerStack.DotNet)
{
    private static readonly Regex ConnectionStrings = ScannerRegex.Compile(
        "\"ConnectionStrings\"\\s*:\\s*\\{(?<block>[^{}]*)\\}");
    private static readonly Regex SecretAssignment = ScannerRegex.Compile(
        @"(?i)(?:password|pwd|accountkey|sharedaccesskey)\s*=");

    public override IEnumerable<CandidateFinding> Scan(RepositorySnapshot snapshot)
    {
        foreach (var file in snapshot.Files.Values.Where(file =>
                     Path.GetFileName(file.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                     && file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match section in ConnectionStrings.Matches(file.Content))
            {
                var block = section.Groups["block"];
                var assignment = SecretAssignment.Match(block.Value);
                if (!assignment.Success)
                {
                    continue;
                }

                var line = ScannerRegex.LineAt(file, block.Index + assignment.Index);
                if (!ScannerBase.IsCommentLine(file.GetLine(line)))
                {
                    yield return Candidate(snapshot, file, line,
                        "ConnectionStrings contains a password or key assignment.");
                }
            }
        }
    }
}

public sealed class AllowAnyOriginScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NET-CORS-001", DotNetMetrics.Of(MetricId.M03),
        "medium", ScannerStack.DotNet, ScannerRegex.Compile(@"\bAllowAnyOrigin\s*\(\s*\)"))
{
    protected override string Rationale => "AllowAnyOrigin permits cross-origin requests from every origin.";
}

public sealed class NewHttpClientScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NET-DI-001", DotNetMetrics.Of(MetricId.M01),
        "low", ScannerStack.DotNet, ScannerRegex.Compile(@"new\s+HttpClient\s*\("))
{
    protected override bool IsEligibleFile(string path) =>
        FileRoleClassifier.Classify(path) != "Test";

    protected override string Rationale => "Direct HttpClient construction may bypass managed client configuration.";
}

public sealed class BuildServiceProviderScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "NET-DI-002", DotNetMetrics.Of(MetricId.M01),
        "medium", ScannerStack.DotNet, ScannerRegex.Compile(@"\.BuildServiceProvider\s*\("))
{
    protected override bool IsEligibleFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase);
    }

    protected override string Rationale => "Building a second service provider can duplicate singleton state.";
}
