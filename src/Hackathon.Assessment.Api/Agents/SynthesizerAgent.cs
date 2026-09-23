using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Agents;

public sealed record SynthesisResult(
    string Answer,
    AnswerType AnswerType,
    ImmutableArray<EvidenceReference> Evidence,
    string ExecutiveSummary,
    bool IsFallback = false);

public sealed class SynthesizerAgent(
    IApimAiGatewayClient gateway,
    ISecretMasker masker,
    SafetyMetrics safetyMetrics,
    IOptions<AssessmentOptions> options,
    PromptCatalog catalog)
{
    private const int MaximumEvidenceCount = 5;
    private const int MaximumWords = 250;
    private static readonly Regex SubCheckEvidence = new(
        @"\A(?<path>.+)#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TextReference = new(
        @"`?(?<path>[A-Za-z0-9_.@()+\-/\\]+\.[A-Za-z0-9]+)(?::(?<line>[1-9][0-9]*)|#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*))`?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Word = new(
        @"\S+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonElement SynthesisSchema = CreateSynthesisSchema();
    private readonly string _systemPrompt = catalog.SynthesizerSystemPrompt;

    public async Task<SynthesisResult> SynthesizeAsync(
        string question,
        string language,
        string questionType,
        AssessmentReport report,
        AiCallContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(context);

        var normalizedLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "tr";
        var normalizedQuestionType =
            string.Equals(questionType, "yes_no", StringComparison.OrdinalIgnoreCase)
                ? "yes_no"
                : "open";
        var allowedEvidence = BuildAllowedEvidence(report);
        var messages = new List<ChatMessage>
        {
            new("system", _systemPrompt),
            new("user", BuildUserMessage(
                question, normalizedLanguage, normalizedQuestionType, report))
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.SynthesizerTimeoutSeconds));

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var response = await gateway.ChatAsync(
                    new ChatRequest(
                        messages.ToArray(),
                        Temperature: 0,
                        ResponseFormatJsonSchema: SynthesisSchema),
                    ModelRole.Synthesizer,
                    context,
                    timeout.Token);

                if (TryParse(response.Content, out var parsed))
                {
                    return CreateResult(
                        parsed,
                        normalizedLanguage,
                        normalizedQuestionType,
                        report,
                        allowedEvidence);
                }

                if (attempt == 0)
                {
                    messages.Add(new ChatMessage(
                        "user",
                        normalizedLanguage == "tr"
                            ? "Önceki yanıt geçersizdi. Yalnız şemaya uygun JSON döndür."
                            : "The previous response was invalid. Return only JSON matching the schema."));
                }
            }

            return Fallback(normalizedLanguage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fallback(normalizedLanguage);
        }
        catch (GatewayException)
        {
            return Fallback(normalizedLanguage);
        }
    }

    private SynthesisResult CreateResult(
        ParsedSynthesis parsed,
        string language,
        string questionType,
        AssessmentReport report,
        ImmutableArray<AllowedEvidence> allowedEvidence)
    {
        if (parsed.AnswerType == AnswerType.InsufficientEvidence)
        {
            return InsufficientEvidence(language);
        }

        var validEvidence = ImmutableArray.CreateBuilder<EvidenceReference>(MaximumEvidenceCount);
        var seen = new HashSet<(string File, int StartLine, int EndLine)>();
        foreach (var citation in parsed.CitedEvidence)
        {
            var matched = MatchAllowedEvidence(
                citation.File, citation.StartLine, citation.EndLine, allowedEvidence);
            if (matched is null)
            {
                safetyMetrics.UnverifiedReferenceRemoved();
                continue;
            }

            if (validEvidence.Count >= MaximumEvidenceCount)
            {
                continue;
            }

            var key = (matched.File, citation.StartLine, citation.EndLine);
            if (!seen.Add(key))
            {
                continue;
            }

            var file = masker.Mask(matched.File);
            validEvidence.Add(new EvidenceReference(
                file,
                citation.StartLine,
                citation.EndLine,
                masker.Mask(citation.Reason),
                BuildEvidenceUrl(
                    report.RepositoryUrl,
                    report.CommitSha,
                    file,
                    citation.StartLine,
                    citation.EndLine)));
        }

        if (validEvidence.Count == 0
            || report.Metrics.IsDefaultOrEmpty
            || report.Metrics.All(metric => metric.Status == MetricStatus.Degerlendirilemedi))
        {
            return InsufficientEvidence(language);
        }

        var answer = RemoveUnknownReferences(parsed.Answer, allowedEvidence);
        var executiveSummary =
            RemoveUnknownReferences(parsed.ExecutiveSummary, allowedEvidence);
        answer = masker.Mask(answer).Trim();
        executiveSummary = masker.Mask(executiveSummary).Trim();

        if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(executiveSummary))
        {
            return InsufficientEvidence(language);
        }

        if (questionType == "yes_no" && parsed.AnswerType != AnswerType.InsufficientEvidence)
        {
            answer = EnsureYesNoOpening(answer, language);
        }

        return new SynthesisResult(
            LimitWords(answer),
            parsed.AnswerType,
            validEvidence.ToImmutable(),
            LimitWords(executiveSummary));
    }

    private string BuildUserMessage(
        string question,
        string language,
        string questionType,
        AssessmentReport report)
    {
        var metrics = report.Metrics.Select(metric => new
        {
            metricId = ToMetricId(metric.MetricId),
            metricName = metric.MetricName,
            status = ToMetricStatus(metric.Status),
            metric.Score,
            metric.Rationale,
            metric.Risk,
            coverage = metric.Coverage.ToString().ToLowerInvariant(),
            subChecks = metric.SubChecks.Select(subCheck => new
            {
                subCheck.Id,
                status = ToSubCheckStatus(subCheck.Status),
                subCheck.Reason,
                subCheck.EvidenceRefs
            }),
            findings = metric.Findings.Select(finding => new
            {
                finding.Id,
                finding.Title,
                severity = finding.Severity.ToString().ToLowerInvariant(),
                confidence = finding.Confidence == Confidence.Kesin
                    ? "kesin"
                    : "potansiyel",
                finding.StandardRef,
                finding.Rationale,
                finding.Impact,
                evidence = finding.Evidence.Select(item => new
                {
                    item.File,
                    item.StartLine,
                    item.EndLine
                }),
                finding.Recommendation
            }),
            metric.NotAssessableReason
        });
        var questionJson = JsonSerializer.Serialize(question);
        var assessmentJson = JsonSerializer.Serialize(new { metrics });
        return masker.Mask(
            $"<user_question language=\"{language}\" question_type=\"{questionType}\">"
            + questionJson
            + "</user_question>\n<assessment_data>"
            + assessmentJson
            + "</assessment_data>");
    }

    private string RemoveUnknownReferences(
        string value,
        ImmutableArray<AllowedEvidence> allowedEvidence) =>
        TextReference.Replace(value, match =>
        {
            var startText = match.Groups["line"].Success
                ? match.Groups["line"].Value
                : match.Groups["start"].Value;
            var endText = match.Groups["line"].Success
                ? startText
                : match.Groups["end"].Value;
            if (!int.TryParse(startText, out var start)
                || !int.TryParse(endText, out var end))
            {
                safetyMetrics.UnverifiedReferenceRemoved();
                return "";
            }

            if (MatchAllowedEvidence(match.Groups["path"].Value, start, end, allowedEvidence)
                is not null)
            {
                return match.Value;
            }

            safetyMetrics.UnverifiedReferenceRemoved();
            return "";
        });

    private static ImmutableArray<AllowedEvidence> BuildAllowedEvidence(AssessmentReport report)
    {
        var evidence = ImmutableArray.CreateBuilder<AllowedEvidence>();
        foreach (var metric in report.Metrics)
        {
            foreach (var finding in metric.Findings)
            {
                foreach (var item in finding.Evidence)
                {
                    evidence.Add(new AllowedEvidence(
                        NormalizePath(item.File),
                        item.File,
                        item.StartLine,
                        item.EndLine));
                }
            }

            foreach (var subCheck in metric.SubChecks)
            {
                foreach (var reference in subCheck.EvidenceRefs)
                {
                    var match = SubCheckEvidence.Match(reference);
                    if (!match.Success
                        || !int.TryParse(match.Groups["start"].Value, out var start)
                        || !int.TryParse(match.Groups["end"].Value, out var end)
                        || end < start)
                    {
                        continue;
                    }

                    var file = match.Groups["path"].Value;
                    evidence.Add(new AllowedEvidence(
                        NormalizePath(file), file, start, end));
                }
            }
        }

        return evidence.ToImmutable();
    }

    private static AllowedEvidence? MatchAllowedEvidence(
        string file,
        int startLine,
        int endLine,
        ImmutableArray<AllowedEvidence> allowedEvidence)
    {
        if (startLine < 1 || endLine < startLine)
        {
            return null;
        }

        var normalized = NormalizePath(file);
        foreach (var item in allowedEvidence)
        {
            if (string.Equals(item.NormalizedFile, normalized, StringComparison.Ordinal)
                && startLine >= item.StartLine
                && endLine <= item.EndLine)
            {
                return item;
            }
        }

        return null;
    }

    private static string BuildEvidenceUrl(
        string repositoryUrl,
        string commitSha,
        string file,
        int startLine,
        int endLine)
    {
        var encodedPath = string.Join(
            '/',
            NormalizePath(file).Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var baseUrl = repositoryUrl.TrimEnd('/');
        if (baseUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^4];
        }

        return $"{baseUrl}/blob/{Uri.EscapeDataString(commitSha)}/"
            + $"{encodedPath}#L{startLine}-L{endLine}";
    }

    private static bool TryParse(string? content, out ParsedSynthesis parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryAnswerType(root.GetProperty("answerType").GetString(), out var answerType)
                || root.GetProperty("riskPriorities").ValueKind != JsonValueKind.Array
                || root.GetProperty("citedEvidence").ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var answer = root.GetProperty("answer").GetString();
            var executiveSummary = root.GetProperty("executiveSummary").GetString();
            if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(executiveSummary))
            {
                return false;
            }

            var citations = ImmutableArray.CreateBuilder<Citation>();
            foreach (var node in root.GetProperty("citedEvidence").EnumerateArray())
            {
                var file = node.GetProperty("file").GetString();
                var reason = node.GetProperty("reason").GetString();
                if (string.IsNullOrWhiteSpace(file)
                    || string.IsNullOrWhiteSpace(reason)
                    || !node.GetProperty("startLine").TryGetInt32(out var startLine)
                    || !node.GetProperty("endLine").TryGetInt32(out var endLine))
                {
                    return false;
                }

                citations.Add(new Citation(file, startLine, endLine, reason));
            }

            parsed = new ParsedSynthesis(
                answer,
                executiveSummary,
                answerType,
                citations.ToImmutable());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAnswerType(string? value, out AnswerType answerType)
    {
        answerType = value switch
        {
            "assessment" => AnswerType.Assessment,
            "repo_answer" => AnswerType.RepoAnswer,
            "insufficient_evidence" => AnswerType.InsufficientEvidence,
            _ => (AnswerType)(-1)
        };
        return (int)answerType >= 0;
    }

    private static SynthesisResult Fallback(string language)
    {
        var message = language == "en"
            ? "The summary could not be generated; details are in the assessment table."
            : "Özet üretilemedi; ayrıntılar değerlendirme tablosundadır.";
        return new SynthesisResult(
            message,
            AnswerType.Assessment,
            ImmutableArray<EvidenceReference>.Empty,
            message,
            true);
    }

    private SynthesisResult InsufficientEvidence(string language)
    {
        var message = language == "en"
            ? "I cannot answer this from the repository evidence: no verified evidence was found."
            : "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: doğrulanmış kanıt bulunamadı.";
        message = masker.Mask(message);
        return new SynthesisResult(
            message,
            AnswerType.InsufficientEvidence,
            ImmutableArray<EvidenceReference>.Empty,
            message);
    }

    private static string EnsureYesNoOpening(string answer, string language)
    {
        var prefixes = language == "en"
            ? new[] { "Yes, because", "No, because", "Partially, because" }
            : new[] { "Evet, çünkü", "Hayır, çünkü", "Kısmen, çünkü" };
        if (prefixes.Any(prefix => answer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return answer;
        }

        return (language == "en" ? "Partially, because " : "Kısmen, çünkü ")
            + char.ToLowerInvariant(answer[0])
            + answer[1..];
    }

    private static string LimitWords(string value)
    {
        var matches = Word.Matches(value);
        if (matches.Count <= MaximumWords)
        {
            return value;
        }

        var last = matches[MaximumWords - 1];
        return value[..(last.Index + last.Length)].TrimEnd() + "…";
    }

    private static JsonElement CreateSynthesisSchema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"assessment_synthesis","strict":true,"schema":{"type":"object","additionalProperties":false,"required":["answer","executiveSummary","riskPriorities","citedEvidence","answerType"],"properties":{"answer":{"type":"string"},"executiveSummary":{"type":"string"},"riskPriorities":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["metricId","findingId","reason"],"properties":{"metricId":{"type":"string","enum":["m01","m02","m03","m04","m05","m06","m07","m08","m09","m10"]},"findingId":{"type":["string","null"]},"reason":{"type":"string"}}}},"citedEvidence":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["file","startLine","endLine","reason"],"properties":{"file":{"type":"string"},"startLine":{"type":"integer"},"endLine":{"type":"integer"},"reason":{"type":"string"}}}},"answerType":{"type":"string","enum":["assessment","repo_answer","insufficient_evidence"]}}}}}""");
        return document.RootElement.Clone();
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string ToMetricId(MetricId id) => $"m{(int)id + 1:00}";

    private static string ToMetricStatus(MetricStatus status) =>
        status switch
        {
            MetricStatus.Uyumlu => "Uyumlu",
            MetricStatus.KismenUyumlu => "Kısmen Uyumlu",
            MetricStatus.Uyumsuz => "Uyumsuz",
            MetricStatus.Degerlendirilemedi => "Değerlendirilemedi",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static string ToSubCheckStatus(SubCheckStatus status) =>
        status switch
        {
            SubCheckStatus.Karsilandi => "Karşılandı",
            SubCheckStatus.Ihlal => "İhlal",
            SubCheckStatus.KanitYok => "Kanıt yok",
            SubCheckStatus.Uygulanamaz => "Uygulanamaz",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private sealed record ParsedSynthesis(
        string Answer,
        string ExecutiveSummary,
        AnswerType AnswerType,
        ImmutableArray<Citation> CitedEvidence);

    private sealed record Citation(
        string File,
        int StartLine,
        int EndLine,
        string Reason);

    private sealed record AllowedEvidence(
        string NormalizedFile,
        string File,
        int StartLine,
        int EndLine);
}
