using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Hackathon.Assessment.Tests.Integration;

internal static class TestJwt
{
    public const string Instance = "https://login.microsoftonline.com/";
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string Audience = "api://assessment.example.test";
    public const string V2Issuer = $"{Instance}{TenantId}/v2.0";
    public const string V1Issuer = $"https://sts.windows.net/{TenantId}/";

    private static readonly RSA TestRsa = RSA.Create(2048);

    public static RsaSecurityKey SigningKey { get; } = new(TestRsa)
    {
        KeyId = "integration-test-key"
    };

    public static string CreateToken(
        string? issuer = null,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        IEnumerable<string>? roles = null,
        string? scope = null,
        SecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("oid", "test-object-id"),
            new("sub", "test-subject")
        };
        if (roles is not null)
        {
            claims.AddRange(roles.Select(role => new Claim("roles", role)));
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            claims.Add(new Claim("scp", scope));
        }

        var token = new JwtSecurityToken(
            issuer ?? V2Issuer,
            audience ?? Audience,
            claims,
            notBefore ?? now.AddMinutes(-1),
            expires ?? now.AddMinutes(5),
            new SigningCredentials(
                signingKey ?? SigningKey,
                SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
