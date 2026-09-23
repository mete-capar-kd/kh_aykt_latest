using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Scoring;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Scoring;

public sealed class StatusResolverTests
{
    [Fact]
    public void Resolve_returns_not_assessable_for_no_coverage()
    {
        var result = StatusResolver.Resolve(10m, [], Coverage.None, "Kanıt bulunamadı.");

        Assert.Equal(MetricStatus.Degerlendirilemedi, result.Status);
        Assert.Null(result.Score);
        Assert.Equal("Kanıt bulunamadı.", result.NotAssessableReason);
    }

    [Fact]
    public void Resolve_returns_not_assessable_when_reason_is_present()
    {
        var result = StatusResolver.Resolve(8m, [], Coverage.Complete, "Zaman aşımı.");

        Assert.Equal(MetricStatus.Degerlendirilemedi, result.Status);
        Assert.Null(result.Score);
        Assert.Equal("Zaman aşımı.", result.NotAssessableReason);
    }

    [Fact]
    public void Resolve_returns_compliant_for_nine_with_complete_coverage()
    {
        var result = StatusResolver.Resolve(9m, [], Coverage.Complete, null);

        Assert.Equal(MetricStatus.Uyumlu, result.Status);
        Assert.Equal(9m, result.Score);
    }

    [Fact]
    public void Resolve_caps_partial_coverage_at_partially_compliant()
    {
        var result = StatusResolver.Resolve(10m, [], Coverage.Partial, null);

        Assert.Equal(MetricStatus.KismenUyumlu, result.Status);
        Assert.Equal(10m, result.Score);
    }

    [Fact]
    public void Resolve_treats_potential_high_as_open_high_risk()
    {
        var result = StatusResolver.Resolve(
            9m,
            [Finding(Severity.High, Confidence.Potansiyel)],
            Coverage.Complete,
            null);

        Assert.Equal(MetricStatus.KismenUyumlu, result.Status);
        Assert.Equal(9m, result.Score);
    }

    [Fact]
    public void Resolve_never_returns_compliant_with_critical_finding()
    {
        var result = StatusResolver.Resolve(
            9.5m,
            [Finding(Severity.Critical)],
            Coverage.Complete,
            null);

        Assert.Equal(MetricStatus.KismenUyumlu, result.Status);
    }

    [Theory]
    [InlineData("5", MetricStatus.KismenUyumlu)]
    [InlineData("8.9", MetricStatus.KismenUyumlu)]
    [InlineData("4.9", MetricStatus.Uyumsuz)]
    [InlineData("0", MetricStatus.Uyumsuz)]
    public void Resolve_applies_score_boundaries(string scoreText, MetricStatus expected)
    {
        var score = decimal.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);

        var result = StatusResolver.Resolve(score, [], Coverage.Complete, null);

        Assert.Equal(expected, result.Status);
    }

    private static Finding Finding(
        Severity severity,
        Confidence confidence = Confidence.Kesin) =>
        new(
            "finding",
            "finding",
            severity,
            confidence,
            "standard",
            "rationale",
            "impact",
            [new Evidence("src/App.cs", 1, 1, "snippet", "hash", "unused")],
            "recommendation");
}
