using System.Security.Cryptography;

namespace Hackathon.Assessment.Api.Safety;

public sealed class SafetyCanary
{
    private readonly string _token;

    public SafetyCanary(IConfiguration configuration)
    {
        var configured = configuration["Safety:CanaryToken"];
        _token = string.IsNullOrWhiteSpace(configured)
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
            : configured;
    }

    public string Token => _token;
}
