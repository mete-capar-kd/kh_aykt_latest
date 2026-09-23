using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;

namespace Hackathon.Assessment.Api.Scanners.Python;

internal static class PythonMetrics
{
    public static ImmutableArray<MetricId> Of(params MetricId[] metrics) => [.. metrics];
}

public sealed class DynamicEvalScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "PY-EVAL-001", PythonMetrics.Of(MetricId.M03),
        "high", ScannerStack.Python, ScannerRegex.Compile(@"\b(?:eval|exec)\s*\("))
{
    protected override bool IsEligibleFile(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "Dynamic eval/exec execution may process untrusted input.";
}

public sealed class DisabledTlsVerificationScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "PY-TLS-001", PythonMetrics.Of(MetricId.M03),
        "high", ScannerStack.Python, ScannerRegex.Compile(@"verify\s*=\s*False\b"))
{
    protected override bool IsEligibleFile(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "TLS certificate verification is explicitly disabled.";
}

public sealed class FStringSqlScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "PY-SQL-001",
        PythonMetrics.Of(MetricId.M03, MetricId.M04), "high", ScannerStack.Python,
        ScannerRegex.Compile("""(?i)\bf["'][^"'\r\n]*(?:select|insert|update|delete)\b[^"'\r\n]*\{"""))
{
    protected override bool IsEligibleFile(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "An SQL statement appears to interpolate a Python f-string expression.";
}

public sealed class BareExceptScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "PY-EXC-001", PythonMetrics.Of(MetricId.M05),
        "medium", ScannerStack.Python, ScannerRegex.Compile(@"^\s*except\s*:"))
{
    protected override bool IsEligibleFile(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "Bare except catches every exception without naming the failure.";
}

public sealed class PythonDebugScanner(ISecretMasker masker) :
    RegexLineScanner(masker, "PY-DEBUG-001",
        PythonMetrics.Of(MetricId.M03, MetricId.M08), "medium", ScannerStack.Python,
        ScannerRegex.Compile(@"\bDEBUG\s*=\s*True\b"))
{
    protected override bool IsEligibleFile(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    protected override string Rationale => "DEBUG is explicitly enabled in a Python source/config file.";
}
