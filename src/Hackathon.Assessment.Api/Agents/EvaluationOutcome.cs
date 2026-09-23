using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Agents;

public sealed record EvaluationOutcome(
    MetricId MetricId,
    ImmutableArray<Finding> Findings,
    int RejectedCount,
    ImmutableArray<SubCheckResult> SubChecks,
    Coverage Coverage,
    string Rationale,
    string Risk,
    int FilesExamined,
    int ToolCalls,
    string? NotAssessableReason,
    bool IsTimeoutOrError)
{
    public static EvaluationOutcome NotAssessable(MetricId metricId, string reason) =>
        new(
            metricId,
            ImmutableArray<Finding>.Empty,
            0,
            ImmutableArray<SubCheckResult>.Empty,
            Coverage.None,
            "",
            "",
            0,
            0,
            reason,
            true);
}
