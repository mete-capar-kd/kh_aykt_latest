using System.ComponentModel.DataAnnotations;

namespace Hackathon.Assessment.Api.Options;

public sealed class AssessmentOptions
{
    [Range(1, int.MaxValue)]
    public int MaxFiles { get; set; } = 2000;

    [Range(1, long.MaxValue)]
    public long MaxTotalBytes { get; set; } = 52_428_800;

    [Range(1, int.MaxValue)]
    public int ProfilerTimeoutSeconds { get; set; } = 20;

    [Range(1, int.MaxValue)]
    public int RouterTimeoutSeconds { get; set; } = 4;

    [Range(1, int.MaxValue)]
    public int MetricTimeoutSeconds { get; set; } = 140;

    [Range(1, int.MaxValue)]
    public int SynthesizerTimeoutSeconds { get; set; } = 40;

    [Range(1, int.MaxValue)]
    public int GlobalTimeoutSeconds { get; set; } = 210;

    [Range(1, int.MaxValue)]
    public int MaxConcurrentFullAssessments { get; set; } = 2;
}
