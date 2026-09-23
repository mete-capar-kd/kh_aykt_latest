using Hackathon.Assessment.Api.Masking;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Masking;

public sealed class SecretMaskerTests
{
    private readonly ISecretMasker _masker = new SecretMasker();

    [Theory]
    [InlineData("api_key", "abcDEF123456")]
    [InlineData("token", "abcDEF123456")]
    [InlineData("password", "abcDEF123456")]
    [InlineData("client_secret", "abcDEF123456")]
    [InlineData("AccountKey", "abcDEF123456")]
    [InlineData("User ID", "abcDEF123456")]
    [InlineData("Pwd", "abcDEF123456")]
    public void PreservesKeyNameButMasksAssignedValue(string key, string value)
    {
        var separator = key is "AccountKey" or "User ID" or "Pwd" ? "=" : ":";
        var result = _masker.Mask($"{key}{separator}{value};");
        Assert.Contains(key + separator, result, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", result, StringComparison.Ordinal);
        Assert.DoesNotContain(value, result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksPrivateKeyBlockAndJwt()
    {
        var privateKey = string.Join('\n',
            "-----BEGIN PRIVATE KEY-----", "synthetic-secret-material",
            "-----END PRIVATE KEY-----");
        var jwt = "eyJ" + new string('A', 8) + ".eyJ" + new string('B', 8)
            + "." + new string('C', 12);
        var output = _masker.Mask(privateKey + " " + jwt);
        Assert.DoesNotContain("synthetic-secret-material", output, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, output, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksAssembledProviderFormatsWithoutProviderShapedFixtureStrings()
    {
        var github = "gh" + "p_" + new string('A', 30);
        var fineGrained = "github_" + "pat_" + new string('B', 30);
        var aws = "AK" + "IA" + new string('C', 16);
        var output = _masker.Mask($"{github} {fineGrained} {aws}");
        Assert.Equal("***MASKED*** ***MASKED*** ***MASKED***", output);
    }

    [Fact]
    public void MasksContactInformationAndOnlyChecksumValidCitizenIds()
    {
        var syntheticCitizenId = BuildCitizenId();
        var result = _masker.Mask(
            $"me@example.test +90 532 123 45 67 {syntheticCitizenId} 12345678901");
        Assert.DoesNotContain("me@example.test", result, StringComparison.Ordinal);
        Assert.DoesNotContain("532 123 45 67", result, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticCitizenId, result, StringComparison.Ordinal);
        Assert.Contains("12345678901", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MasksConsecutivePhoneNumbers()
    {
        var input = "+90 532 123 45 67, 0532 987 65 43";
        var masked = _masker.Mask(input);
        Assert.Equal("***MASKED***, ***MASKED***", masked);
    }

    [Theory]
    [InlineData("The token format is described in the guide.")]
    [InlineData("api_key=")]
    [InlineData("Call 12345 for help")]
    [InlineData("This is ordinary architecture guidance.")]
    public void DoesNotMaskBenignText(string text) =>
        Assert.Equal(text, _masker.Mask(text));

    private static string BuildCitizenId()
    {
        var firstNine = "1" + new string('0', 8);
        var digits = firstNine.Select(character => character - '0').ToArray();
        var odd = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        var even = digits[1] + digits[3] + digits[5] + digits[7];
        var tenth = ((odd * 7 - even) % 10 + 10) % 10;
        return firstNine + tenth + ((digits.Sum() + tenth) % 10);
    }
}
