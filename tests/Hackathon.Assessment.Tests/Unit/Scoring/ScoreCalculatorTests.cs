using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Scoring;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Scoring;

public sealed class ScoreCalculatorTests
{
    [Fact]
    public void Compute_applies_all_severity_penalties()
    {
        var findings = new[]
        {
            Finding("critical", Severity.Critical, line: 1),
            Finding("high", Severity.High, line: 2),
            Finding("medium", Severity.Medium, line: 3),
            Finding("low", Severity.Low, line: 4),
            Finding("info", Severity.Info, line: 5)
        };

        Assert.Equal(3.5m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_halves_potential_penalty()
    {
        var findings = new[]
        {
            Finding("potential-high", Severity.High, Confidence.Potansiyel)
        };

        Assert.Equal(9m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_rounds_midpoints_away_from_zero()
    {
        var findings = new[]
        {
            Finding("potential-medium", Severity.Medium, Confidence.Potansiyel, line: 1),
            Finding("potential-low", Severity.Low, Confidence.Potansiyel, line: 2)
        };

        Assert.Equal(9.3m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_clamps_score_at_zero()
    {
        var findings = Enumerable.Range(1, 5)
            .Select(index => Finding($"critical-{index}", Severity.Critical, line: index))
            .ToArray();

        Assert.Equal(0m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_counts_same_technical_finding_only_once()
    {
        var findings = new[]
        {
            Finding("  Missing   Authorization ", Severity.High, line: 8),
            Finding("missing authorization", Severity.High, line: 8)
        };

        Assert.Equal(8m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_keeps_findings_at_different_ranges_distinct()
    {
        var findings = new[]
        {
            Finding("missing authorization", Severity.High, line: 8),
            Finding("missing authorization", Severity.High, line: 9)
        };

        Assert.Equal(6m, ScoreCalculator.Compute(findings));
    }

    [Fact]
    public void Compute_is_deterministic_for_different_input_order()
    {
        var first = Finding("high", Severity.High, line: 2);
        var second = Finding("low", Severity.Low, line: 3);

        var forward = ScoreCalculator.Compute([first, second]);
        var reverse = ScoreCalculator.Compute([second, first]);

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void OverallScore_averages_only_assessable_metrics_and_rounds()
    {
        var results = new[]
        {
            Metric(score: 9.2m, MetricStatus.Uyumlu),
            Metric(score: 9.3m, MetricStatus.KismenUyumlu),
            Metric(score: 10m, MetricStatus.Degerlendirilemedi)
        };

        Assert.Equal(9.3m, ScoreCalculator.OverallScore(results));
    }

    [Fact]
    public void OverallScore_returns_null_when_all_metrics_are_not_assessable()
    {
        var results = new[]
        {
            Metric(score: null, MetricStatus.Degerlendirilemedi),
            Metric(score: null, MetricStatus.Degerlendirilemedi)
        };

        Assert.Null(ScoreCalculator.OverallScore(results));
    }

    private static Finding Finding(
        string title,
        Severity severity,
        Confidence confidence = Confidence.Kesin,
        int line = 1) =>
        new(
            $"finding-{line}",
            title,
            severity,
            confidence,
            "standard",
            "rationale",
            "impact",
            [new Evidence("src/App.cs", line, line, "snippet", "hash", "unused")],
            "recommendation");

    private static MetricResult Metric(decimal? score, MetricStatus status) =>
        new(
            MetricId.M01,
            MetricNames.GetName(MetricId.M01),
            status,
            score,
            "rationale",
            "risk",
            status == MetricStatus.Degerlendirilemedi ? Coverage.None : Coverage.Complete,
            ImmutableArray<SubCheckResult>.Empty,
            ImmutableArray<Finding>.Empty,
            status == MetricStatus.Degerlendirilemedi ? "reason" : null,
            0,
            0,
            0);
}
