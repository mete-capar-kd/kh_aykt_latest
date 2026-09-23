using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Reporting;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Reporting;

public sealed class ReportBuilderTests
{
    [Fact]
    public void Build_outputs_ordered_table_from_same_metric_values()
    {
        var metrics = MetricNames.All
            .Reverse()
            .Select((id, index) => Metric(
                id,
                status: id == MetricId.M02
                    ? MetricStatus.Degerlendirilemedi
                    : MetricStatus.KismenUyumlu,
                score: id == MetricId.M02 ? null : 9.3m,
                rationale: $"rationale-{index}"))
            .ToImmutableArray();
        var report = Report(metrics);

        var markdown = new ReportBuilder(new RecordingMasker()).Build(report, "Özet");

        Assert.StartsWith(
            "| Metrik | Durum | Puan | Gerekçe | Risk | Dosya/Satır | Öneri |",
            markdown);
        Assert.True(markdown.IndexOf("m01", StringComparison.Ordinal)
            < markdown.IndexOf("m02", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("m09", StringComparison.Ordinal)
            < markdown.IndexOf("m10", StringComparison.Ordinal));
        Assert.Contains("| Değerlendirilemedi | — |", markdown);
        Assert.Contains("| Kısmen Uyumlu | 9.3 |", markdown);
        Assert.Contains("## Değerlendirilemeyen metrikler", markdown);
        Assert.Contains("## Yönetici özeti", markdown);
    }

    [Fact]
    public void Build_escapes_pipes_and_newlines_and_clips_cells()
    {
        var longRisk = new string('x', 400);
        var metric = Metric(
            MetricId.M01,
            rationale: "ilk | değer\nikinci",
            risk: longRisk);

        var markdown = new ReportBuilder(new RecordingMasker()).Build(
            Report([metric]),
            "Özet");

        Assert.Contains("ilk \\| değer<br>ikinci", markdown);
        Assert.DoesNotContain(longRisk, markdown);
        Assert.Contains($"{new string('x', 299)}…", markdown);
    }

    [Fact]
    public void Build_adds_at_most_three_commit_pinned_clickable_links()
    {
        var findings = new[]
        {
            Finding("one", Severity.Low, 1, "low recommendation"),
            Finding("two", Severity.High, 2, "high recommendation"),
            Finding("three", Severity.Medium, 3, "medium recommendation"),
            Finding("four", Severity.Critical, 4, "critical recommendation")
        }.ToImmutableArray();
        var metric = Metric(MetricId.M01, findings: findings);

        var markdown = new ReportBuilder(new RecordingMasker()).Build(
            Report([metric]),
            "Özet");

        Assert.Equal(3, CountOccurrences(markdown, "/blob/abc123/"));
        Assert.Contains(
            "[src/App.cs:1](https://github.com/example/repo/blob/abc123/src/App.cs#L1)",
            markdown);
        Assert.DoesNotContain("#L4)", markdown);
        Assert.Contains("| critical recommendation |", markdown);
    }

    [Fact]
    public void Build_links_verified_subcheck_even_when_no_finding_exists()
    {
        var metric = Metric(MetricId.M01) with
        {
            SubChecks =
            [
                new SubCheckResult(
                    "m01-sc01",
                    SubCheckStatus.Karsilandi,
                    "README reviewed.",
                    ["README.md#L1-L1"])
            ]
        };

        var markdown = new ReportBuilder(new RecordingMasker()).Build(
            Report([metric]), "Summary");

        Assert.Contains(
            "[README.md:1](https://github.com/example/repo/blob/abc123/README.md#L1)",
            markdown);
    }

    [Fact]
    public void Build_masks_the_complete_markdown()
    {
        var masker = new RecordingMasker();
        var metric = Metric(
            MetricId.M01,
            rationale: "FAKE_SECRET_DO_NOT_USE_0001");

        var markdown = new ReportBuilder(masker).Build(
            Report([metric]),
            "FAKE_SECRET_DO_NOT_USE_0001");

        Assert.NotNull(masker.Input);
        Assert.Contains("FAKE_SECRET_DO_NOT_USE_0001", masker.Input);
        Assert.DoesNotContain("FAKE_SECRET_DO_NOT_USE_0001", markdown);
        Assert.Contains("***MASKED***", markdown);
    }

    [Fact]
    public void Build_lists_not_assessable_reason()
    {
        var metric = Metric(
            MetricId.M07,
            MetricStatus.Degerlendirilemedi,
            null,
            notAssessableReason: "Branch policy koddan doğrulanamaz.");

        var markdown = new ReportBuilder(new RecordingMasker()).Build(
            Report([metric]),
            "Özet");

        Assert.Contains(
            "**m07 — CI/CD ve Kaynak Kod Yönetimi:** Branch policy koddan doğrulanamaz.",
            markdown);
    }

    private static AssessmentReport Report(ImmutableArray<MetricResult> metrics) =>
        new(
            "job",
            "https://github.com/example/repo",
            "main",
            "abc123",
            metrics,
            metrics.Where(metric => metric.Score.HasValue).Select(metric => metric.Score!.Value).DefaultIfEmpty().Average(),
            string.Empty,
            DateTimeOffset.UnixEpoch,
            new ModelInfo("router", "profiler", "evaluator", "synthesizer", "v1"));

    private static MetricResult Metric(
        MetricId id,
        MetricStatus status = MetricStatus.Uyumlu,
        decimal? score = 10m,
        string rationale = "rationale",
        string risk = "risk",
        ImmutableArray<Finding> findings = default,
        string? notAssessableReason = null) =>
        new(
            id,
            MetricNames.GetName(id),
            status,
            score,
            rationale,
            risk,
            status == MetricStatus.Degerlendirilemedi ? Coverage.None : Coverage.Complete,
            ImmutableArray<SubCheckResult>.Empty,
            findings.IsDefault ? ImmutableArray<Finding>.Empty : findings,
            notAssessableReason,
            3,
            4,
            2);

    private static Finding Finding(
        string title,
        Severity severity,
        int line,
        string recommendation) =>
        new(
            title,
            title,
            severity,
            Confidence.Kesin,
            "standard",
            "rationale",
            "impact",
            [new Evidence("src/App.cs", line, line, "snippet", "hash", "unused")],
            recommendation);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;

        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed class RecordingMasker : ISecretMasker
    {
        public string? Input { get; private set; }

        public string Mask(string value)
        {
            Input = value;
            return value.Replace(
                "FAKE_SECRET_DO_NOT_USE_0001",
                "***MASKED***",
                StringComparison.Ordinal);
        }
    }
}
