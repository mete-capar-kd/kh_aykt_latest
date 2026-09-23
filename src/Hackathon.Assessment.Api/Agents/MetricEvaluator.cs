using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Scoring;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Tools;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Agents;

public sealed class MetricEvaluator(
    IApimAiGatewayClient gateway,
    IToolDispatcher dispatcher,
    PromptCatalog catalog,
    ISecretMasker masker,
    IOptions<AssessmentOptions> options)
{
    private const int MaximumToolResultCharacters = 12_000;
    private const int MaximumConversationCharacters = 400_000;
    private const string InvalidToolReason = "geçersiz tool çağrıları";
    private const string InvalidFinalReason = "geçersiz final yanıt";
    private static readonly Regex EvidencePath = new(
        @"\A(?<path>.+)#L(?<start>[1-9][0-9]*)-L(?<end>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonElement FinalSchema = CreateFinalSchema();

    public async Task<EvaluationOutcome> EvaluateAsync(
        MetricId id,
        RepositorySnapshot snapshot,
        RepoProfile profile,
        AiCallContext context,
        CancellationToken cancellationToken)
    {
        var metric = catalog.GetMetric(id);
        var ledger = new EvidenceLedger();
        var messages = new List<ChatMessage>
        {
            new("system", catalog.EvaluatorSystemPrompt + "\n\n"
                + metric.Prompt + "\n\n"
                + JsonSerializer.Serialize(metric.Rubric, JsonSerializerOptions.Web)),
            new("user", $"metricId: m{(int)id + 1:00}\n"
                + $"RepoProfile (veri): {JsonSerializer.Serialize(profile, AppJsonContext.Default.RepoProfile)}\n"
                + "Final yanıtta yalnızca JSON şemasındaki subChecks, rationale, risk ve notAssessableReason alanlarını döndür.")
        };
        var toolCalls = 0;
        var invalidCalls = 0;
        var repairAttempts = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.MetricTimeoutSeconds));

        try
        {
            while (true)
            {
                var response = await gateway.ChatAsync(
                    new ChatRequest(
                        messages.ToArray(),
                        OpenAiToolDefinitions.All,
                        "auto",
                        0,
                        ResponseFormatJsonSchema: FinalSchema),
                    ModelRole.Evaluator,
                    context,
                    deadline.Token);

                if (!response.ToolCalls.IsDefaultOrEmpty)
                {
                    messages.Add(new ChatMessage(
                        "assistant",
                        response.Content ?? "",
                        ToolCalls: [.. response.ToolCalls.Select(call =>
                            new ChatToolCallRequest(
                                call.Id,
                                "function",
                                new ChatFunctionCallRequest(call.Name, call.ArgumentsJson)))]));

                    foreach (var call in response.ToolCalls)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        toolCalls++;
                        var dispatched = await DispatchToolAsync(
                            id, snapshot, ledger, call, deadline.Token);
                        invalidCalls = dispatched.Valid ? 0 : invalidCalls + 1;
                        messages.Add(new ChatMessage(
                            "tool",
                            dispatched.Result,
                            ToolCallId: call.Id));
                        if (invalidCalls >= 3)
                        {
                            return EvaluationOutcome.NotAssessable(id, InvalidToolReason);
                        }
                    }

                    PruneToolHistory(messages);
                    continue;
                }

                if (TryParseFinal(
                    id, metric, snapshot, ledger, response.Content,
                    toolCalls, out var outcome))
                {
                    return outcome;
                }

                if (repairAttempts++ >= 2)
                {
                    return EvaluationOutcome.NotAssessable(id, InvalidFinalReason);
                }

                messages.Add(new ChatMessage(
                    "user",
                    "Önceki yanıt geçersizdi; yalnız şemaya uygun JSON döndür."));
                PruneToolHistory(messages);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EvaluationOutcome.NotAssessable(id, "Metrik zaman aşımı");
        }
        catch (GatewayException)
        {
            return EvaluationOutcome.NotAssessable(id, "Model erişim hatası");
        }
        catch (ContentFilteredException)
        {
            return EvaluationOutcome.NotAssessable(id, "içerik filtresi");
        }
    }

    private async Task<(bool Valid, string Result)> DispatchToolAsync(
        MetricId id,
        RepositorySnapshot snapshot,
        EvidenceLedger ledger,
        ChatToolCall call,
        CancellationToken cancellationToken)
    {
        ToolResult result;
        JsonElement arguments;
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson))
        {
            return (false, """{"toolResult":{"error":"Missing JSON tool arguments."}}""");
        }

        try
        {
            using var parsed = JsonDocument.Parse(call.ArgumentsJson);
            arguments = parsed.RootElement.Clone();
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return (false, """{"toolResult":{"error":"Tool arguments must be a JSON object."}}""");
            }
        }
        catch (JsonException)
        {
            return (false, """{"toolResult":{"error":"Invalid JSON tool arguments."}}""");
        }

        var stagingLedger = call.Name == "record_finding" ? ledger : new EvidenceLedger();
        result = await dispatcher.DispatchAsync(
            call.Name,
            arguments,
            new ToolContext(snapshot, stagingLedger, id),
            cancellationToken);
        var serialized = WrapToolResult(result.Content);
        if (serialized.Length > MaximumToolResultCharacters)
        {
            var length = Math.Min(serialized.Length, 10_000);
            string bounded;
            do
            {
                bounded = JsonSerializer.Serialize(new
                {
                    toolResult = new
                    {
                        truncated = true,
                        content = serialized[..length]
                    }
                });
                length /= 2;
            }
            while (bounded.Length > MaximumToolResultCharacters);
            serialized = bounded;
        }
        else if (result.Succeeded)
        {
            CreditVisibleEvidence(call.Name, result.Content, snapshot, ledger);
        }

        return (result.Succeeded, serialized);
    }

    private static string WrapToolResult(string content)
    {
        using var parsed = JsonDocument.Parse(content);
        return JsonSerializer.Serialize(new { toolResult = parsed.RootElement });
    }

    private static void CreditVisibleEvidence(
        string tool,
        string content,
        RepositorySnapshot snapshot,
        EvidenceLedger ledger)
    {
        if (tool is not ("read_file" or "search_code"))
        {
            return;
        }

        using var parsed = JsonDocument.Parse(content);
        if (tool == "read_file")
        {
            var root = parsed.RootElement;
            if (root.TryGetProperty("path", out var pathNode)
                && pathNode.GetString() is { } path
                && snapshot.Files.ContainsKey(path)
                && root.TryGetProperty("lines", out var lines))
            {
                foreach (var item in lines.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String
                        || item.GetString() is not { } numberedLine)
                    {
                        continue;
                    }

                    var separator = numberedLine.IndexOf(':');
                    if (separator > 0
                        && int.TryParse(numberedLine.AsSpan(0, separator), out var number)
                        && number is > 0
                        && number <= snapshot.Files[path].LineCount)
                    {
                        ledger.MarkSeen(path, number);
                    }
                }
            }
        }
        else
        {
            var root = parsed.RootElement;
            if (root.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("path", out var pathNode)
                        && pathNode.GetString() is { } path
                        && snapshot.Files.ContainsKey(path)
                        && item.TryGetProperty("line", out var lineNode)
                        && lineNode.TryGetInt32(out var number)
                        && item.TryGetProperty("text", out var textNode)
                        && textNode.ValueKind == JsonValueKind.String
                        && textNode.GetString() is { Length: < 300 })
                    {
                        ledger.MarkSeen(path, number);
                    }
                }
            }
        }
    }

    private static void PruneToolHistory(List<ChatMessage> messages)
    {
        var total = messages.Sum(message => message.Content.Length);
        if (total <= MaximumConversationCharacters)
        {
            return;
        }

        for (var index = 0; index < messages.Count && total > MaximumConversationCharacters; index++)
        {
            if (messages[index].Role != "tool")
            {
                continue;
            }

            total -= messages[index].Content.Length;
            messages[index] = messages[index] with
            {
                Content = "[önceki sonuç kısaltıldı]"
            };
            total += messages[index].Content.Length;
        }
    }

    private bool TryParseFinal(
        MetricId id,
        MetricPrompt metric,
        RepositorySnapshot snapshot,
        EvidenceLedger ledger,
        string? content,
        int toolCalls,
        out EvaluationOutcome outcome)
    {
        outcome = EvaluationOutcome.NotAssessable(id, InvalidFinalReason);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(content);
            var root = parsed.RootElement;
            var rationale = root.GetProperty("rationale").GetString()
                ?? throw new FormatException("Missing rationale.");
            var risk = root.GetProperty("risk").GetString()
                ?? throw new FormatException("Missing risk.");
            var knownIds = metric.Rubric.SubChecks
                .Select(check => check.Id)
                .ToHashSet(StringComparer.Ordinal);
            var supplied = root.GetProperty("subChecks").EnumerateArray()
                .Where(item => item.TryGetProperty("id", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && knownIds.Contains(value.GetString()!))
                .Select(ParseSubCheck)
                .GroupBy(result => result.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var findingIds = ledger.Findings.Select(finding => finding.Id)
                .ToHashSet(StringComparer.Ordinal);
            var subChecks = metric.Rubric.SubChecks
                .Select(check =>
                {
                    if (!check.CodeVerifiable)
                    {
                        return new SubCheckResult(
                            check.Id,
                            SubCheckStatus.Uygulanamaz,
                            "koddan doğrulanamaz",
                            ImmutableArray<string>.Empty);
                    }

                    if (!supplied.TryGetValue(check.Id, out var reported))
                    {
                        return Missing(check.Id);
                    }

                    if (reported.Status is SubCheckStatus.Karsilandi or SubCheckStatus.Ihlal
                        && (reported.EvidenceRefs.IsDefaultOrEmpty
                            || reported.EvidenceRefs.Any(reference =>
                                !IsVerifiedReference(reference, snapshot, ledger, findingIds))))
                    {
                        return Missing(check.Id);
                    }

                    if (reported.Status == SubCheckStatus.Uygulanamaz)
                    {
                        return string.IsNullOrWhiteSpace(reported.Reason)
                            ? Missing(check.Id)
                            : reported with
                            {
                                Reason = masker.Mask(reported.Reason),
                                EvidenceRefs = ImmutableArray<string>.Empty
                            };
                    }

                    return reported.Status == SubCheckStatus.KanitYok
                        ? Missing(check.Id)
                        : reported with { Reason = masker.Mask(reported.Reason) };
                })
                .ToImmutableArray();
            var coverage = CoverageCalculator.Evaluate(subChecks);
            var modelReason = root.TryGetProperty("notAssessableReason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
            outcome = new EvaluationOutcome(
                id,
                ledger.Findings,
                ledger.RejectedCount,
                coverage.SubChecks,
                coverage.Coverage,
                masker.Mask(rationale),
                masker.Mask(risk),
                ledger.FilesExamined,
                toolCalls,
                coverage.Coverage == Coverage.None
                    ? masker.Mask(string.IsNullOrWhiteSpace(modelReason)
                        ? "Kanıt yok"
                        : modelReason)
                    : null,
                false);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException
            or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private static SubCheckResult ParseSubCheck(JsonElement source)
    {
        var id = source.GetProperty("id").GetString()
            ?? throw new FormatException("Missing subcheck id.");
        var statusText = source.GetProperty("status").GetString();
        var status = statusText switch
        {
            "Karşılandı" => SubCheckStatus.Karsilandi,
            "İhlal" => SubCheckStatus.Ihlal,
            "Kanıt yok" => SubCheckStatus.KanitYok,
            "Uygulanamaz" => SubCheckStatus.Uygulanamaz,
            _ => throw new FormatException("Unknown subcheck status.")
        };
        var reason = source.GetProperty("reason").GetString() ?? "";
        var evidence = source.GetProperty("evidenceRefs").EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new FormatException("Invalid evidence reference."))
            .ToImmutableArray();
        return new SubCheckResult(id, status, reason, evidence);
    }

    private static SubCheckResult Missing(string id) =>
        new(id, SubCheckStatus.KanitYok, "Kanıt yok",
            ImmutableArray<string>.Empty);

    private static bool IsVerifiedReference(
        string reference,
        RepositorySnapshot snapshot,
        EvidenceLedger ledger,
        HashSet<string> findingIds)
    {
        if (findingIds.Contains(reference))
        {
            return true;
        }

        var match = EvidencePath.Match(reference);
        return match.Success
            && snapshot.Files.TryGetValue(match.Groups["path"].Value, out var file)
            && int.TryParse(match.Groups["start"].Value, out var start)
            && int.TryParse(match.Groups["end"].Value, out var end)
            && start >= 1
            && end >= start
            && end <= file.LineCount
            && ledger.HasSeen(file.Path, start, end);
    }

    private static JsonElement CreateFinalSchema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"metric_result","strict":true,"schema":{"type":"object","required":["subChecks","rationale","risk"],"properties":{"subChecks":{"type":"array","items":{"type":"object","required":["id","status","reason","evidenceRefs"],"properties":{"id":{"type":"string"},"status":{"type":"string","enum":["Karşılandı","İhlal","Kanıt yok","Uygulanamaz"]},"reason":{"type":"string"},"evidenceRefs":{"type":"array","items":{"type":"string"}}}}},"rationale":{"type":"string"},"risk":{"type":"string"},"notAssessableReason":{"type":["string","null"]}}}}}""");
        return document.RootElement.Clone();
    }
}
