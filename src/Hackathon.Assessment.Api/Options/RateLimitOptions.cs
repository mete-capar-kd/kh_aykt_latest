using System.ComponentModel.DataAnnotations;

namespace Hackathon.Assessment.Api.Options;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    [Range(1, int.MaxValue)]
    public int PermitsPerMinute { get; set; } = 300;

    [Range(0, int.MaxValue)]
    public int QueueLimit { get; set; } = 100;

    public string[] ExemptClientIds { get; set; } = [];
}
