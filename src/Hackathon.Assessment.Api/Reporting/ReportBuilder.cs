using System.Text;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;

namespace Hackathon.Assessment.Api.Reporting;

public sealed class ReportBuilder(ISecretMasker secretMasker)
{
    private const int MaximumCellLength = 300;
    private static readonly Regex SubCheckReference = new(
        @"\A(?<path>.+)#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string Build(AssessmentReport report, string executiveSummary)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(executiveSummary);

        var builder = new StringBuilder();
        builder.AppendLine("| Metrik | Durum | Puan | Gerekçe | Risk | Dosya/Satır | Öneri |");
        builder.AppendLine("|---|---|---:|---|---|---|---|");

        foreach (var metric in report.Metrics.OrderBy(metric => metric.MetricId))
        {
            builder
                .Append("| ")
                .Append(Cell($"{MetricIdText(metric.MetricId)} — {metric.MetricName}"))
                .Append(" | ")
                .Append(Cell(StatusText(metric.Status)))
                .Append(" | ")
                .Append(Cell(metric.Score?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"))
                .Append(" | ")
                .Append(Cell(metric.Rationale))
                .Append(" | ")
                .Append(Cell(metric.Risk))
                .Append(" | ")
                .Append(Cell(EvidenceLinks(report, metric)))
                .Append(" | ")
                .Append(Cell(Recommendation(metric)))
                .AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Değerlendirilemeyen metrikler");

        var notAssessableMetrics = report.Metrics
            .Where(metric => metric.Status == MetricStatus.Degerlendirilemedi)
            .OrderBy(metric => metric.MetricId)
            .ToArray();

        if (notAssessableMetrics.Length == 0)
        {
            builder.AppendLine("- Yok.");
        }
        else
        {
            foreach (var metric in notAssessableMetrics)
            {
                builder
                    .Append("- **")
                    .Append(MetricIdText(metric.MetricId))
                    .Append(" — ")
                    .Append(EscapeMarkdown(metric.MetricName))
                    .Append(":** ")
                    .AppendLine(EscapeMarkdown(
                        string.IsNullOrWhiteSpace(metric.NotAssessableReason)
                            ? "Neden belirtilmedi."
                            : metric.NotAssessableReason));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Yönetici özeti");
        builder.AppendLine(EscapeMarkdown(executiveSummary, preserveNewLines: true));

        return secretMasker.Mask(builder.ToString());
    }

    private static string EvidenceLinks(AssessmentReport report, MetricResult metric)
    {
        var links = metric.Findings
            .SelectMany(finding => finding.Evidence)
            .Select(evidence => new EvidenceLocation(
                evidence.File, evidence.StartLine, evidence.EndLine))
            .Concat(metric.SubChecks
                .SelectMany(subCheck => subCheck.EvidenceRefs)
                .Select(ParseSubCheckReference)
                .OfType<EvidenceLocation>())
            .Distinct()
            .Take(3)
            .Select(evidence =>
            {
                var label = evidence.StartLine == evidence.EndLine
                    ? $"{evidence.File}:{evidence.StartLine}"
                    : $"{evidence.File}:{evidence.StartLine}-{evidence.EndLine}";
                label = label.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("[", "\\[", StringComparison.Ordinal)
                    .Replace("]", "\\]", StringComparison.Ordinal);
                return $"[{label}]({CreatePinnedUrl(
                    report, evidence.File, evidence.StartLine, evidence.EndLine)})";
            })
            .ToArray();

        return links.Length == 0 ? "—" : string.Join("<br>", links);
    }

    private static EvidenceLocation? ParseSubCheckReference(string reference)
    {
        var match = SubCheckReference.Match(reference);
        return match.Success
            && int.TryParse(match.Groups["start"].Value, out var start)
            && int.TryParse(match.Groups["end"].Value, out var end)
            && end >= start
                ? new EvidenceLocation(match.Groups["path"].Value, start, end)
                : null;
    }

    private static string CreatePinnedUrl(
        AssessmentReport report, string path, int startLine, int endLine)
    {
        var normalizedPath = path.Replace('\\', '/');
        var escapedPath = string.Join(
            '/',
            normalizedPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        var lineFragment = startLine == endLine
            ? $"#L{startLine}"
            : $"#L{startLine}-L{endLine}";
        var repositoryUrl = report.RepositoryUrl.TrimEnd('/');
        if (repositoryUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repositoryUrl = repositoryUrl[..^4];
        }

        return $"{repositoryUrl}/blob/{Uri.EscapeDataString(report.CommitSha)}/{escapedPath}{lineFragment}";
    }

    private static string Recommendation(MetricResult metric)
    {
        var recommendation = metric.Findings
            .OrderBy(finding => finding.Severity)
            .FirstOrDefault()
            ?.Recommendation;

        return string.IsNullOrWhiteSpace(recommendation) ? "—" : recommendation;
    }

    private static string Cell(string value)
    {
        var escaped = EscapeMarkdown(
            string.IsNullOrWhiteSpace(value) ? "—" : value,
            preserveNewLines: false);

        return escaped.Length <= MaximumCellLength
            ? escaped
            : string.Concat(escaped.AsSpan(0, MaximumCellLength - 1), "…");
    }

    private static string EscapeMarkdown(string value, bool preserveNewLines = false)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("|", "\\|", StringComparison.Ordinal);

        return preserveNewLines
            ? normalized
            : normalized.Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private static string MetricIdText(MetricId metricId) =>
        metricId.ToString().ToLowerInvariant();

    private static string StatusText(MetricStatus status) =>
        status switch
        {
            MetricStatus.Uyumlu => "Uyumlu",
            MetricStatus.KismenUyumlu => "Kısmen Uyumlu",
            MetricStatus.Uyumsuz => "Uyumsuz",
            MetricStatus.Degerlendirilemedi => "Değerlendirilemedi",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown metric status.")
        };

    private sealed record EvidenceLocation(string File, int StartLine, int EndLine);
}
