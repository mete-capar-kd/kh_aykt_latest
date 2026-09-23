using System.Collections.Immutable;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Orchestration;

public sealed class StubAskOrchestrator : IAskOrchestrator
{
    public Task<AskResponse> AskAsync(
        AskContext context,
        AskRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = MetricNames.All
            .Select(metricId => new MetricResult(
                metricId,
                MetricNames.GetName(metricId),
                MetricStatus.Degerlendirilemedi,
                null,
                "Değerlendirme motoru henüz etkin değil.",
                "Değerlendirme motoru henüz etkin değil.",
                Coverage.None,
                ImmutableArray<SubCheckResult>.Empty,
                ImmutableArray<Finding>.Empty,
                "Assessment engine is not enabled.",
                0,
                0,
                0))
            .ToImmutableArray();
        var assessment = new AssessmentReport(
            Guid.NewGuid().ToString("N"),
            request.RepositoryUrl ?? "unknown",
            request.Ref ?? "main",
            "unknown",
            metrics,
            null,
            "",
            DateTimeOffset.UtcNow,
            new ModelInfo("stub", "stub", "stub", "stub", "stub"));

        return Task.FromResult(new AskResponse(
            "Değerlendirme motoru henüz etkin değil.",
            AnswerType.InsufficientEvidence,
            "stub",
            ImmutableArray<EvidenceReference>.Empty,
            context.CorrelationId,
            assessment));
    }
}
