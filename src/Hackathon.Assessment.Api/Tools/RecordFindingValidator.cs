using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Tools;

public sealed record FindingInput(
    string Id,
    string Title,
    string Severity,
    string Confidence,
    string StandardRef,
    string Rationale,
    string Impact,
    string File,
    int StartLine,
    int EndLine,
    string Snippet,
    string Recommendation);

public sealed record RecordResult(
    bool Accepted,
    string? Reason,
    string? FixHint,
    Evidence? Evidence,
    int RejectedCount,
    bool PermanentlyRejected);

public sealed class RecordFindingValidator(ISecretMasker masker)
{
    private static readonly string[] GenericRecommendations =
    [
        "iyileştirilmeli", "gözden geçirilmeli", "düzeltilmeli", "incelenmeli",
        "should be improved", "needs review", "consider refactoring", "fix this"
    ];

    public RecordResult Record(ToolContext context, FindingInput input)
    {
        var ledger = context.EvidenceLedger;
        var path = input.File;
        var fingerprint = Fingerprint(context.MetricId, path, input.Title);
        if (ledger.IsPermanentlyRejected(fingerprint))
        {
            return new(false, "PermanentlyRejected", "Use a different, verifiable technical finding.",
                null, ledger.RegisterRejectionAndCount(fingerprint), true);
        }

        (string Reason, string FixHint)? failure = null;
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Split('/').Any(segment => segment is "" or "." or "..")
            || !context.Snapshot.Files.TryGetValue(path, out var file))
        {
            failure = ("File not found in the pinned snapshot.", "Use a snapshot-relative path returned by list_files.");
        }
        else if (input.StartLine < 1 || input.StartLine > input.EndLine || input.EndLine > file.LineCount)
        {
            failure = ("Line range is outside the file.", "Read a valid one-based interval from this file.");
        }
        else if (input.EndLine - input.StartLine > 120)
        {
            failure = ("Evidence range exceeds 120 lines.", "Cite a range with endLine - startLine <= 120.");
        }
        else
        {
            var cited = new StringBuilder();
            for (var line = input.StartLine; line <= input.EndLine; line++)
            {
                cited.Append(file.GetLine(line));
                cited.Append('\n');
            }

            var maskedSource = Normalize(masker.Mask(cited.ToString()));
            var maskedSnippet = Normalize(masker.Mask(input.Snippet));
            if (maskedSnippet.Length == 0 || !maskedSource.Contains(maskedSnippet, StringComparison.Ordinal))
            {
                failure = ("Snippet does not match the cited lines.", "Use an exact masked snippet from the requested lines.");
            }
            else if (!ledger.HasSeen(path, input.StartLine, input.EndLine))
            {
                failure = ("Cited lines were not all seen by this evaluator.", "Read or search every cited line first.");
            }
            else if (!string.Equals(masker.Mask(path), path, StringComparison.Ordinal))
            {
                failure = ("The path contains sensitive data.", "Use a non-sensitive snapshot path.");
            }
            else if (!TryParseSeverity(input.Severity, out _)
                || !TryParseConfidence(input.Confidence, out _)
                || string.IsNullOrWhiteSpace(input.StandardRef))
            {
                failure = ("Severity, confidence, or standardRef is missing or invalid.",
                    "Provide a supported severity and confidence and a non-empty standardRef.");
            }
            else if (IsGeneric(input.Recommendation))
            {
                failure = ("Recommendation is too short or generic.",
                    "Provide at least 20 specific characters beyond generic suggestions.");
            }
        }

        if (failure is { } error)
        {
            var permanentlyRejected = ledger.RegisterRejection(fingerprint);
            return new(false, permanentlyRejected ? "PermanentlyRejected" : error.Reason,
                error.FixHint, null, ledger.RejectedCount, permanentlyRejected);
        }

        var snippet = Normalize(masker.Mask(input.Snippet));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snippet)));
        var escapedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var evidence = new Evidence(path, input.StartLine, input.EndLine, snippet, hash,
            $"https://github.com/{Uri.EscapeDataString(context.Snapshot.Owner)}/{Uri.EscapeDataString(context.Snapshot.Repo)}/blob/{context.Snapshot.CommitSha}/{escapedPath}#L{input.StartLine}-L{input.EndLine}");
        _ = TryParseSeverity(input.Severity, out var severity);
        _ = TryParseConfidence(input.Confidence, out var confidence);
        var finding = new Finding(
            masker.Mask(input.Id), masker.Mask(input.Title), severity, confidence,
            masker.Mask(input.StandardRef), masker.Mask(input.Rationale), masker.Mask(input.Impact),
            [evidence], masker.Mask(input.Recommendation));
        ledger.Record(fingerprint, finding);
        return new(true, null, null, evidence, ledger.RejectedCount, false);
    }

    private static string Fingerprint(MetricId metric, string path, string title) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"m{(int)metric + 1:00}|{path}|{Normalize(title).ToLowerInvariant()}")));

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsGeneric(string recommendation)
    {
        var remaining = Normalize(recommendation).ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        foreach (var phrase in GenericRecommendations)
        {
            remaining = remaining.Replace(phrase, "", StringComparison.Ordinal);
        }

        return remaining.Trim().Length < 20;
    }

    private static bool TryParseSeverity(string value, out Severity severity) =>
        Enum.TryParse(value, ignoreCase: true, out severity)
        && value is "critical" or "high" or "medium" or "low" or "info";

    private static bool TryParseConfidence(string value, out Confidence confidence)
    {
        confidence = value switch
        {
            "kesin" => Confidence.Kesin,
            "potansiyel" => Confidence.Potansiyel,
            _ => default
        };
        return value is "kesin" or "potansiyel";
    }
}
