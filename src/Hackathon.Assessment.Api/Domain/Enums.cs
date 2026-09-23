using System.Text.Json.Serialization;

namespace Hackathon.Assessment.Api.Domain;

public enum MetricStatus
{
    [JsonStringEnumMemberName("Uyumlu")]
    Uyumlu,
    [JsonStringEnumMemberName("Kısmen Uyumlu")]
    KismenUyumlu,
    [JsonStringEnumMemberName("Uyumsuz")]
    Uyumsuz,
    [JsonStringEnumMemberName("Değerlendirilemedi")]
    Degerlendirilemedi
}

public enum Severity
{
    [JsonStringEnumMemberName("critical")]
    Critical,
    [JsonStringEnumMemberName("high")]
    High,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("info")]
    Info
}

public enum Confidence
{
    [JsonStringEnumMemberName("kesin")]
    Kesin,
    [JsonStringEnumMemberName("potansiyel")]
    Potansiyel
}

public enum Coverage
{
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("none")]
    None
}

public enum AnswerType
{
    [JsonStringEnumMemberName("assessment")]
    Assessment,
    [JsonStringEnumMemberName("repo_answer")]
    RepoAnswer,
    [JsonStringEnumMemberName("insufficient_evidence")]
    InsufficientEvidence,
    [JsonStringEnumMemberName("refusal")]
    Refusal
}

public enum MetricId
{
    [JsonStringEnumMemberName("m01")]
    M01,
    [JsonStringEnumMemberName("m02")]
    M02,
    [JsonStringEnumMemberName("m03")]
    M03,
    [JsonStringEnumMemberName("m04")]
    M04,
    [JsonStringEnumMemberName("m05")]
    M05,
    [JsonStringEnumMemberName("m06")]
    M06,
    [JsonStringEnumMemberName("m07")]
    M07,
    [JsonStringEnumMemberName("m08")]
    M08,
    [JsonStringEnumMemberName("m09")]
    M09,
    [JsonStringEnumMemberName("m10")]
    M10
}

public enum SubCheckStatus
{
    [JsonStringEnumMemberName("Karşılandı")]
    Karsilandi,
    [JsonStringEnumMemberName("İhlal")]
    Ihlal,
    [JsonStringEnumMemberName("Kanıt yok")]
    KanitYok,
    [JsonStringEnumMemberName("Uygulanamaz")]
    Uygulanamaz
}
