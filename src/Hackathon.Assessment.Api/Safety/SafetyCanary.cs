using System.Security.Cryptography;

namespace Hackathon.Assessment.Api.Safety;

public sealed class SafetyCanary
{
    public SafetyCanary(IConfiguration configuration)
    {
        var configured = configuration["Safety:CanaryToken"];
        Token = string.IsNullOrWhiteSpace(configured)
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
            : configured;
    }

    public string Token { get; }
}
