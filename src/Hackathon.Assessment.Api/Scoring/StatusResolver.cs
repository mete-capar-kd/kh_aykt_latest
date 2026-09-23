using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Scoring;

public sealed record StatusResolution(
    MetricStatus Status,
    decimal? Score,
    string? NotAssessableReason);

public static class StatusResolver
{
    public static StatusResolution Resolve(
        decimal? score,
        IReadOnlyList<Finding> findings,
        Coverage coverage,
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (coverage == Coverage.None || !string.IsNullOrWhiteSpace(reason) || !score.HasValue)
        {
            return new StatusResolution(
                MetricStatus.Degerlendirilemedi,
                null,
                string.IsNullOrWhiteSpace(reason)
                    ? "Yeterli doğrulanmış kanıt bulunamadı."
                    : reason);
        }

        if (score.Value < 5m)
        {
            return new StatusResolution(MetricStatus.Uyumsuz, score, null);
        }

        var hasOpenHighRisk = findings.Any(
            finding => finding.Severity is Severity.High or Severity.Critical);

        if (score.Value >= 9m
            && coverage == Coverage.Complete
            && !hasOpenHighRisk)
        {
            return new StatusResolution(MetricStatus.Uyumlu, score, null);
        }

        return new StatusResolution(MetricStatus.KismenUyumlu, score, null);
    }
}
