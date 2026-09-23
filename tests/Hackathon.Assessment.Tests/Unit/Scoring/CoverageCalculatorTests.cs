using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Scoring;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Scoring;

public sealed class CoverageCalculatorTests
{
    [Fact]
    public void Calculate_returns_none_when_there_are_no_subchecks()
    {
        var result = CoverageCalculator.Evaluate([]);

        Assert.Equal(Coverage.None, result.Coverage);
        Assert.Empty(result.SubChecks);
    }

    [Fact]
    public void Calculate_returns_none_when_every_subcheck_is_not_applicable()
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(SubCheckStatus.Uygulanamaz, "koddan doğrulanamaz"),
            SubCheck(SubCheckStatus.Uygulanamaz, "repository için geçerli değil")
        ]);

        Assert.Equal(Coverage.None, result.Coverage);
    }

    [Fact]
    public void Calculate_returns_none_when_no_applicable_subcheck_is_evidenced()
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(SubCheckStatus.KanitYok),
            SubCheck(SubCheckStatus.Karsilandi)
        ]);

        Assert.Equal(Coverage.None, result.Coverage);
        Assert.All(
            result.SubChecks,
            subCheck => Assert.Equal(SubCheckStatus.KanitYok, subCheck.Status));
    }

    [Fact]
    public void Calculate_returns_partial_when_evidence_and_missing_evidence_remain()
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(SubCheckStatus.Ihlal, evidenceRefs: ["finding-1"]),
            SubCheck(SubCheckStatus.KanitYok)
        ]);

        Assert.Equal(Coverage.Partial, result.Coverage);
    }

    [Fact]
    public void Calculate_returns_complete_when_all_applicable_subchecks_are_evidenced()
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(SubCheckStatus.Karsilandi, evidenceRefs: ["evidence-1"]),
            SubCheck(SubCheckStatus.Ihlal, evidenceRefs: ["finding-1"]),
            SubCheck(SubCheckStatus.Uygulanamaz, "koddan doğrulanamaz")
        ]);

        Assert.Equal(Coverage.Complete, result.Coverage);
    }

    [Theory]
    [InlineData(SubCheckStatus.Karsilandi)]
    [InlineData(SubCheckStatus.Ihlal)]
    public void Evaluate_records_evidence_backed_status_without_evidence_as_missing(
        SubCheckStatus status)
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(status, evidenceRefs: ["", "  "]),
            SubCheck(SubCheckStatus.Ihlal, evidenceRefs: ["finding-1"])
        ]);

        Assert.Equal(Coverage.Partial, result.Coverage);
        Assert.Equal(SubCheckStatus.KanitYok, result.SubChecks[0].Status);
        Assert.Empty(result.SubChecks[0].EvidenceRefs);
    }

    [Fact]
    public void Evaluate_does_not_treat_evidence_on_missing_status_as_evidenced()
    {
        var result = CoverageCalculator.Evaluate(
        [
            SubCheck(SubCheckStatus.KanitYok, evidenceRefs: ["unexpected"])
        ]);

        Assert.Equal(Coverage.None, result.Coverage);
        Assert.Empty(result.SubChecks[0].EvidenceRefs);
    }

    [Fact]
    public void Evaluate_rejects_not_applicable_subcheck_without_reason()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CoverageCalculator.Evaluate(
            [
                SubCheck(SubCheckStatus.Uygulanamaz, reason: "")
            ]));

        Assert.Contains("requires a reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_rejects_unknown_status()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoverageCalculator.Evaluate(
            [
                SubCheck((SubCheckStatus)int.MaxValue)
            ]));
    }

    private static SubCheckResult SubCheck(
        SubCheckStatus status,
        string reason = "reason",
        ImmutableArray<string> evidenceRefs = default) =>
        new("sub-check", status, reason, evidenceRefs);
}
