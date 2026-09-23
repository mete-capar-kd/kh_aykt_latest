using System.Collections.Immutable;

namespace Hackathon.Assessment.Api.Domain;

public sealed record AskContext(
    string CorrelationId,
    string NormalizedQuestion,
    bool SuspectedInjection,
    string CallerId);

public sealed record EvidenceReference(
    string File,
    int StartLine,
    int EndLine,
    string Reason,
    string Url);

public sealed record Evidence(
    string File,
    int StartLine,
    int EndLine,
    string Snippet,
    string SnippetSha256,
    string Url);

public sealed record SubCheckResult(
    string Id,
    SubCheckStatus Status,
    string Reason,
    ImmutableArray<string> EvidenceRefs);

public sealed record Finding
{
    public Finding(
        string id,
        string title,
        Severity severity,
        Confidence confidence,
        string standardRef,
        string rationale,
        string impact,
        ImmutableArray<Evidence> evidence,
        string recommendation)
    {
        if (evidence.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one evidence item is required.", nameof(evidence));
        }

        Id = id;
        Title = title;
        Severity = severity;
        Confidence = confidence;
        StandardRef = standardRef;
        Rationale = rationale;
        Impact = impact;
        Evidence = evidence;
        Recommendation = recommendation;
    }

    public string Id { get; }
    public string Title { get; }
    public Severity Severity { get; }
    public Confidence Confidence { get; }
    public string StandardRef { get; }
    public string Rationale { get; }
    public string Impact { get; }
    public ImmutableArray<Evidence> Evidence { get; }
    public string Recommendation { get; }
}

public sealed record MetricResult(
    MetricId MetricId,
    string MetricName,
    MetricStatus Status,
    decimal? Score,
    string Rationale,
    string Risk,
    Coverage Coverage,
    ImmutableArray<SubCheckResult> SubChecks,
    ImmutableArray<Finding> Findings,
    string? NotAssessableReason,
    int FilesExamined,
    int ToolCalls,
    int RejectedFindings);

public sealed record ModelInfo(
    string Router,
    string Profiler,
    string Evaluator,
    string Synthesizer,
    string PromptVersion);

public sealed record AssessmentReport(
    string JobId,
    string RepositoryUrl,
    string Ref,
    string CommitSha,
    ImmutableArray<MetricResult> Metrics,
    decimal? OverallScore,
    string ReportMarkdown,
    DateTimeOffset GeneratedAt,
    ModelInfo ModelInfo);

public sealed record RepoProfile(
    string RepositoryUrl,
    string CommitSha,
    ImmutableArray<string> Languages,
    ImmutableArray<string> EntryPoints,
    ImmutableArray<string> ManifestFiles);

public sealed record CandidateFinding(
    string RuleId,
    string File,
    int StartLine,
    int EndLine,
    string MaskedContext,
    string FileRole);

public static class MetricNames
{
    public static string GetName(MetricId metricId) =>
        metricId switch
        {
            MetricId.M01 => "Kod ve Proje Yapısı Standartları",
            MetricId.M02 => "Kimlik Doğrulama ve Yetkilendirme",
            MetricId.M03 => "Uygulama Güvenliği ve Secret Yönetimi",
            MetricId.M04 => "Veri Yönetimi ve Entegrasyon Standartları",
            MetricId.M05 => "Loglama, İzlenebilirlik ve APM",
            MetricId.M06 => "Test ve Kod Kalitesi Standartları",
            MetricId.M07 => "CI/CD ve Kaynak Kod Yönetimi",
            MetricId.M08 => "Container ve Çalışma Ortamı Standartları",
            MetricId.M09 => "Performans, Dayanıklılık ve Ölçeklenebilirlik",
            MetricId.M10 => "Dokümantasyon ve Mimari Yönetişim",
            _ => throw new ArgumentOutOfRangeException(nameof(metricId))
        };

    public static ImmutableArray<MetricId> All { get; } =
    [
        MetricId.M01, MetricId.M02, MetricId.M03, MetricId.M04, MetricId.M05,
        MetricId.M06, MetricId.M07, MetricId.M08, MetricId.M09, MetricId.M10
    ];
}
