using System.Text.RegularExpressions;

namespace Hackathon.Assessment.Api.Masking;

public sealed class SecretMasker : ISecretMasker
{
    private const string Replacement = "***MASKED***";
    private static readonly RegexOptions Flags =
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Pem = Create(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----");
    private static readonly Regex Jwt = Create(
        @"\beyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b");
    private static readonly Regex ConnectionValue = Create(
        @"(?<key>\b(?:Password|Pwd|AccountKey|SharedAccessKey|User ID)\s*=\s*)(?<value>[^;\s]+)");
    private static readonly Regex NamedValue = Create(
        @"(?<key>\b(?:api[_-]?key|secret|token|password|passwd|client[_-]?secret)\s*[:=]\s*[""']?)(?<value>[^""'\s;]{8,})");
    private static readonly Regex ProviderToken = Create(
        @"\b(?:ghp_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b");
    private static readonly Regex Email = Create(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b");
    private static readonly Regex Phone = Create(
        @"(?<before>^|[^\d])(?<phone>(?:\+90[\s-]?|0)?5\d{2}[\s-]?\d{3}[\s-]?\d{2}[\s-]?\d{2})\b");
    private static readonly Regex CitizenId = Create(@"\b[1-9]\d{10}\b");

    public string Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = Pem.Replace(value, Replacement);
        value = Jwt.Replace(value, Replacement);
        value = ConnectionValue.Replace(value, match =>
            match.Groups["key"].Value + Replacement);
        value = NamedValue.Replace(value, match =>
            match.Groups["key"].Value + Replacement);
        value = ProviderToken.Replace(value, Replacement);
        value = Email.Replace(value, Replacement);
        value = Phone.Replace(value, match =>
            match.Groups["before"].Value + Replacement);
        return CitizenId.Replace(value, match =>
            IsValidCitizenId(match.Value) ? Replacement : match.Value);
    }

    private static Regex Create(string pattern) => new(pattern, Flags, Timeout);

    private static bool IsValidCitizenId(string value)
    {
        Span<int> digits = stackalloc int[11];
        for (var index = 0; index < digits.Length; index++)
        {
            digits[index] = value[index] - '0';
        }

        var oddSum = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        var evenSum = digits[1] + digits[3] + digits[5] + digits[7];
        var tenth = ((oddSum * 7 - evenSum) % 10 + 10) % 10;
        var firstTen = 0;
        for (var index = 0; index < 10; index++)
        {
            firstTen += digits[index];
        }

        return digits[9] == tenth && digits[10] == firstTen % 10;
    }
}
