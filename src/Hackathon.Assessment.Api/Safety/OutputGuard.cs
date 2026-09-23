using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Telemetry;

namespace Hackathon.Assessment.Api.Safety;

public sealed record OutputGuardResult(
    string Answer,
    string ExecutiveSummary,
    string? RefusalReason);

public sealed class OutputGuard
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Regex TextReference = new(
        @"`?(?<path>[A-Za-z0-9_.@()+\-/\\]+\.[A-Za-z0-9]+)(?::(?<line>[1-9][0-9]*)|#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*))`?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SubCheckEvidence = new(
        @"\A(?<path>.+)#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ISecretMasker _masker;
    private readonly SafetyMetrics _metrics;
    private readonly string _canary;
    private readonly string _normalizedCanary;
    private readonly HashSet<string> _promptWindows;
    private readonly Regex[] _blocklist;

    public OutputGuard(
        ISecretMasker masker,
        SafetyMetrics metrics,
        PromptCatalog catalog,
        SafetyCanary canary)
    {
        _masker = masker;
        _metrics = metrics;
        _canary = canary.Token;
        _normalizedCanary = NormalizeCompact(canary.Token);
        _promptWindows = BuildWindows(
            [catalog.RouterSourcePrompt, catalog.SynthesizerSystemPrompt, catalog.SafetyPolicyPrompt],
            catalog.RefusalTemplates);
        _blocklist = catalog.OutputBlocklist
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => new Regex(
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value.ToLower(TurkishCulture))}(?![\p{{L}}\p{{N}}])",
                RegexOptions.CultureInvariant))
            .ToArray();
    }

    public OutputGuardResult Guard(
        string answer,
        string executiveSummary,
        AssessmentReport report)
    {
        answer = _masker.Mask(answer);
        executiveSummary = _masker.Mask(executiveSummary);
        if (ContainsCanary(answer) || ContainsCanary(executiveSummary)
            || ContainsPromptWindow(answer) || ContainsPromptWindow(executiveSummary))
        {
            _metrics.LeakBlocked();
            return new OutputGuardResult("", "", "leak_blocked");
        }

        if (ContainsBlockedTerm(answer) || ContainsBlockedTerm(executiveSummary))
        {
            return new OutputGuardResult("", "", "blocklist");
        }

        var allowed = BuildAllowedEvidence(report);
        return new OutputGuardResult(
            RemoveUnknownReferences(answer, allowed).Trim(),
            RemoveUnknownReferences(executiveSummary, allowed).Trim(),
            null);
    }

    private bool ContainsCanary(string value) =>
        value.Contains(_canary, StringComparison.Ordinal)
        || NormalizeCompact(value).Contains(_normalizedCanary, StringComparison.Ordinal);

    private bool ContainsPromptWindow(string value)
    {
        var words = NormalizeWords(value);
        for (var index = 0; index + 12 <= words.Length; index++)
        {
            if (_promptWindows.Contains(HashWindow(words, index)))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsBlockedTerm(string value)
    {
        var normalized = value.ToLower(TurkishCulture);
        return _blocklist.Any(pattern => pattern.IsMatch(normalized));
    }

    private string RemoveUnknownReferences(
        string value,
        ImmutableArray<AllowedEvidence> allowed) =>
        TextReference.Replace(value, match =>
        {
            var startText = match.Groups["line"].Success
                ? match.Groups["line"].Value
                : match.Groups["start"].Value;
            var endText = match.Groups["line"].Success
                ? startText
                : match.Groups["end"].Value;
            if (!int.TryParse(startText, out var start)
                || !int.TryParse(endText, out var end)
                || !allowed.Any(item =>
                    string.Equals(item.Path, NormalizePath(match.Groups["path"].Value), StringComparison.Ordinal)
                    && start >= item.Start
                    && end <= item.End))
            {
                _metrics.UnverifiedReferenceRemoved();
                return "";
            }

            return match.Value;
        });

    private static ImmutableArray<AllowedEvidence> BuildAllowedEvidence(AssessmentReport report)
    {
        var evidence = report.Metrics
            .SelectMany(metric => metric.Findings)
            .SelectMany(finding => finding.Evidence)
            .Select(item => new AllowedEvidence(
                NormalizePath(item.File), item.StartLine, item.EndLine))
            .ToImmutableArray()
            .ToBuilder();
        foreach (var reference in report.Metrics
            .SelectMany(metric => metric.SubChecks)
            .SelectMany(subCheck => subCheck.EvidenceRefs))
        {
            var match = SubCheckEvidence.Match(reference);
            if (match.Success
                && int.TryParse(match.Groups["start"].Value, out var start)
                && int.TryParse(match.Groups["end"].Value, out var end)
                && end >= start)
            {
                evidence.Add(new AllowedEvidence(
                    NormalizePath(match.Groups["path"].Value),
                    start,
                    end));
            }
        }

        return evidence.ToImmutable();
    }

    private static HashSet<string> BuildWindows(IEnumerable<string> prompts, string exclusions)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prompt in prompts)
        {
            AddWindows(result, NormalizeWords(prompt));
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        AddWindows(excluded, NormalizeWords(exclusions));
        AddWindows(excluded, NormalizeWords(
            "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum. "
            + "I cannot answer this from the repository evidence."));
        result.ExceptWith(excluded);
        return result;
    }

    private static void AddWindows(ISet<string> destination, string[] words)
    {
        for (var index = 0; index + 12 <= words.Length; index++)
        {
            destination.Add(HashWindow(words, index));
        }
    }

    private static string HashWindow(string[] words, int index)
    {
        var text = string.Join(' ', words, index, 12);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string[] NormalizeWords(string value) =>
        Regex.Replace(
                value.ToLower(TurkishCulture),
                @"[^\p{L}\p{N}]+",
                " ",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeCompact(string value) =>
        Regex.Replace(
            value,
            @"\s+",
            "",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private sealed record AllowedEvidence(string Path, int Start, int End);
}
