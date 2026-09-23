using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Scoring;

public sealed record CoverageCalculation(
    Coverage Coverage,
    ImmutableArray<SubCheckResult> SubChecks);

public static class CoverageCalculator
{
    public static Coverage Calculate(IEnumerable<SubCheckResult> subChecks) =>
        Evaluate(subChecks).Coverage;

    public static CoverageCalculation Evaluate(IEnumerable<SubCheckResult> subChecks)
    {
        ArgumentNullException.ThrowIfNull(subChecks);

        var normalized = subChecks.Select(Normalize).ToImmutableArray();
        var applicable = normalized
            .Where(result => result.Status != SubCheckStatus.Uygulanamaz)
            .ToImmutableArray();

        if (applicable.IsEmpty)
        {
            return new CoverageCalculation(Coverage.None, normalized);
        }

        var evidencedCount = applicable.Count(IsEvidenced);
        if (evidencedCount == 0)
        {
            return new CoverageCalculation(Coverage.None, normalized);
        }

        var coverage = evidencedCount == applicable.Length
            ? Coverage.Complete
            : Coverage.Partial;

        return new CoverageCalculation(coverage, normalized);
    }

    private static SubCheckResult Normalize(SubCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            SubCheckStatus.Karsilandi or SubCheckStatus.Ihlal => NormalizeEvidenceBacked(result),
            SubCheckStatus.KanitYok => result with { EvidenceRefs = [] },
            SubCheckStatus.Uygulanamaz => NormalizeNotApplicable(result),
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Status,
                "Unknown sub-check status.")
        };
    }

    private static SubCheckResult NormalizeEvidenceBacked(SubCheckResult result)
    {
        var evidenceRefs = result.EvidenceRefs.IsDefault
            ? []
            : result.EvidenceRefs
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .ToImmutableArray();

        return evidenceRefs.IsEmpty
            ? result with { Status = SubCheckStatus.KanitYok, EvidenceRefs = [] }
            : result with { EvidenceRefs = evidenceRefs };
    }

    private static SubCheckResult NormalizeNotApplicable(SubCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            throw new ArgumentException(
                "An Uygulanamaz sub-check requires a reason.",
                nameof(result));
        }

        return result with { EvidenceRefs = [] };
    }

    private static bool IsEvidenced(SubCheckResult result) =>
        result.Status is SubCheckStatus.Karsilandi or SubCheckStatus.Ihlal
        && !result.EvidenceRefs.IsDefaultOrEmpty;
}
