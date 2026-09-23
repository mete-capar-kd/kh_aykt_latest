using System.Collections.Immutable;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Safety;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Safety;

public sealed class OutputGuardTests
{
    [Fact]
    public void CanaryInAnswerIsBlockedButEvidenceIsNotInspected()
    {
        var guard = Create(out var canary, out _);

        var blocked = guard.Guard($"answer {canary.Token}", "summary", Report());
        var evidenceOnly = guard.Guard("normal answer", "normal summary", Report(canary.Token));

        Assert.Equal("leak_blocked", blocked.RefusalReason);
        Assert.Null(evidenceOnly.RefusalReason);
    }

    [Fact]
    public void TwelvePromptWordsAreBlockedButElevenPass()
    {
        var guard = Create(out _, out var catalog);
        var words = Normalize(catalog.RouterSourcePrompt);

        var blocked = guard.Guard(string.Join(' ', words.Take(12)), "summary", Report());
        var allowed = guard.Guard(string.Join(' ', words.Take(11)), "summary", Report());

        Assert.Equal("leak_blocked", blocked.RefusalReason);
        Assert.Null(allowed.RefusalReason);
    }

    [Fact]
    public void StandardCannotAnswerPhrasePasses()
    {
        var guard = Create(out _, out _);

        var result = guard.Guard(
            "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: kanıt yok.",
            "summary",
            Report());

        Assert.Null(result.RefusalReason);
    }

    [Fact]
    public void BlocklistTermIsRejected()
    {
        var guard = Create(out _, out _);

        var result = guard.Guard("Bu repository aptal bir tasarım.", "summary", Report());

        Assert.Equal("blocklist", result.RefusalReason);
    }

    [Fact]
    public void UnverifiedReferencesAreRemovedAndVerifiedReferencesRemain()
    {
        var guard = Create(out _, out _);

        var result = guard.Guard(
            "README.md:1 doğrulandı; Fake.cs:9 uydurma.",
            "README.md#L1-L1 özeti.",
            Report());

        Assert.Contains("README.md:1", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Fake.cs:9", result.Answer, StringComparison.Ordinal);
        Assert.Contains("README.md#L1-L1", result.ExecutiveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptVersionDoesNotDependOnCanary()
    {
        var first = Catalog("first-canary");
        var second = Catalog("second-canary");

        Assert.Equal(first.PromptVersion, second.PromptVersion);
        Assert.NotEqual(first.RouterSystemPrompt, second.RouterSystemPrompt);
    }

    private static OutputGuard Create(
        out SafetyCanary canary,
        out PromptCatalog catalog)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Safety:CanaryToken"] = "test-canary-token"
            })
            .Build();
        canary = new SafetyCanary(configuration);
        catalog = new PromptCatalog(
            Path.Combine(AppContext.BaseDirectory, "prompts"),
            canary);
        return new OutputGuard(
            new SecretMasker(),
            new SafetyMetrics(NullLogger<SafetyMetrics>.Instance),
            catalog,
            canary);
    }

    private static PromptCatalog Catalog(string canary)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Safety:CanaryToken"] = canary
            })
            .Build();
        return new PromptCatalog(
            Path.Combine(AppContext.BaseDirectory, "prompts"),
            new SafetyCanary(configuration));
    }

    private static AssessmentReport Report(string snippet = "safe evidence")
    {
        var evidence = new Evidence(
            "README.md",
            1,
            1,
            snippet,
            "hash",
            "https://github.com/org/repo/blob/commit/README.md#L1-L1");
        var finding = new Finding(
            "m01-001",
            "Finding",
            Severity.Low,
            Confidence.Kesin,
            "UseCase §6.1",
            "rationale",
            "impact",
            [evidence],
            "recommendation");
        var metric = new MetricResult(
            MetricId.M01,
            MetricNames.GetName(MetricId.M01),
            MetricStatus.Uyumlu,
            10,
            "rationale",
            "risk",
            Coverage.Complete,
            [],
            [finding],
            null,
            1,
            1,
            0);
        return new AssessmentReport(
            "job",
            "https://github.com/org/repo",
            "main",
            "commit",
            [metric],
            10,
            "",
            DateTimeOffset.UnixEpoch,
            new ModelInfo("router", "profiler", "evaluator", "synthesizer", "version"));
    }

    private static string[] Normalize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
                value.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR")),
                @"[^\p{L}\p{N}]+",
                " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
