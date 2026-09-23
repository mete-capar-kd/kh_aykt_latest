using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Scoring;

public static class ScoreCalculator
{
    private const decimal MaximumScore = 10m;

    public static decimal Compute(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var penalty = findings
            .DistinctBy(CreateFingerprint, StringComparer.Ordinal)
            .Sum(CalculatePenalty);

        return Math.Max(
            0m,
            Math.Round(MaximumScore - penalty, 1, MidpointRounding.AwayFromZero));
    }

    public static decimal Calculate(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return Compute(findings as IReadOnlyList<Finding> ?? findings.ToArray());
    }

    public static decimal? OverallScore(IEnumerable<MetricResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var assessableScores = results
            .Where(result =>
                result.Status != MetricStatus.Degerlendirilemedi
                && result.Score.HasValue)
            .Select(result => result.Score!.Value)
            .ToArray();

        return assessableScores.Length == 0
            ? null
            : Math.Round(assessableScores.Average(), 1, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculatePenalty(Finding finding)
    {
        var severityPenalty = finding.Severity switch
        {
            Severity.Critical => 3m,
            Severity.High => 2m,
            Severity.Medium => 1m,
            Severity.Low => 0.5m,
            Severity.Info => 0m,
            _ => throw new ArgumentOutOfRangeException(
                nameof(finding),
                finding.Severity,
                "Unknown finding severity.")
        };

        return finding.Confidence == Confidence.Potansiyel
            ? severityPenalty / 2m
            : severityPenalty;
    }

    private static string CreateFingerprint(Finding finding)
    {
        var evidence = finding.Evidence[0];
        var normalizedTitle = string.Join(
                ' ',
                finding.Title.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{evidence.File}|{evidence.StartLine}|{evidence.EndLine}|{normalizedTitle}");
    }
}
