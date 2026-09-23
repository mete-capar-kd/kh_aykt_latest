using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace Hackathon.Assessment.Api.Agents;

public sealed record RouterDecision(
    string Intent,
    ImmutableArray<MetricId> Metrics,
    string Language,
    string QuestionType,
    string Deployment,
    bool IsFailClosed);

public sealed class AskRouterAgent : IDisposable
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly JsonElement RouterSchema = CreateRouterSchema();
    private readonly IApimAiGatewayClient _gateway;
    private readonly PromptCatalog _catalog;
    private readonly SafetyMetrics _metrics;
    private readonly TimeSpan _timeout;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly IReadOnlyDictionary<MetricId, string[]> _keywords;

    public AskRouterAgent(
        IApimAiGatewayClient gateway,
        PromptCatalog catalog,
        SafetyMetrics metrics,
        IOptions<AssessmentOptions> options)
        : this(gateway, catalog, metrics, TimeSpan.FromSeconds(options.Value.RouterTimeoutSeconds))
    {
    }

    public AskRouterAgent(
        IApimAiGatewayClient gateway,
        PromptCatalog catalog,
        SafetyMetrics metrics,
        TimeSpan timeout)
    {
        _gateway = gateway;
        _catalog = catalog;
        _metrics = metrics;
        _timeout = timeout;
        _keywords = ParseKeywords(catalog.MetricKeywords);
    }

    public async Task<RouterDecision> RouteAsync(
        string question,
        bool suspectedInjection,
        AiCallContext context,
        CancellationToken cancellationToken)
    {
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(question)));
        if (_cache.TryGetValue(cacheKey, out RouterDecision? cached) && cached is not null)
        {
            _metrics.RouterIntent(cached.Intent);
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var response = await _gateway.ChatAsync(
                new ChatRequest(
                    [
                        new ChatMessage("system", _catalog.RouterSystemPrompt),
                        new ChatMessage(
                            "user",
                            $"<user_question>{question}</user_question>\n"
                            + $"suspected_injection: {suspectedInjection.ToString().ToLowerInvariant()}")
                    ],
                    Temperature: 0,
                    MaxOutputTokens: 200,
                    ResponseFormatJsonSchema: RouterSchema),
                ModelRole.Router,
                context,
                timeout.Token);
            if (!TryParse(response.Content, response.Deployment, out var decision))
            {
                return FailClosed(question, suspectedInjection);
            }

            _cache.Set(
                cacheKey,
                decision,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(60),
                    Size = 1
                });
            _metrics.RouterIntent(decision.Intent);
            return decision;
        }
        catch (ContentFilteredException)
        {
            _metrics.ContentFiltered();
            return Record(new RouterDecision(
                "unsafe", [], DetectLanguage(question), DetectQuestionType(question), "cheap", true));
        }
        catch (GatewayException)
        {
            return FailClosed(question, suspectedInjection);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailClosed(question, suspectedInjection);
        }
        catch (JsonException)
        {
            return FailClosed(question, suspectedInjection);
        }
    }

    public void Dispose() => _cache.Dispose();

    private RouterDecision FailClosed(string question, bool suspectedInjection)
    {
        var language = DetectLanguage(question);
        var questionType = DetectQuestionType(question);
        if (suspectedInjection)
        {
            return Record(new RouterDecision("unsafe", [], language, questionType, "cheap", true));
        }

        var selected = MatchKeywords(question);
        return Record(selected.IsEmpty
            ? new RouterDecision("out_of_scope", [], language, questionType, "cheap", true)
            : new RouterDecision("repo_question", selected, language, questionType, "cheap", true));
    }

    private RouterDecision Record(RouterDecision decision)
    {
        _metrics.RouterIntent(decision.Intent);
        return decision;
    }

    private ImmutableArray<MetricId> MatchKeywords(string question)
    {
        var normalized = question.ToLower(TurkishCulture);
        var selected = ImmutableArray.CreateBuilder<MetricId>();
        foreach (var (metricId, keywords) in _keywords)
        {
            if (keywords.Any(keyword => IsMatch(normalized, keyword)))
            {
                selected.Add(metricId);
            }
        }

        return selected.ToImmutable();
    }

    private static bool IsMatch(string text, string keyword)
    {
        var escaped = Regex.Escape(keyword.ToLower(TurkishCulture));
        var pattern = keyword.Length < 4 || keyword == "yapı"
            ? $@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])"
            : $@"(?<![\p{{L}}\p{{N}}]){escaped}";
        return Regex.IsMatch(
            text,
            pattern,
            RegexOptions.CultureInvariant);
    }

    private static IReadOnlyDictionary<MetricId, string[]> ParseKeywords(string yaml)
    {
        var values = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, string[]>>(yaml);
        return values.ToDictionary(
            entry => ParseMetric(entry.Key),
            entry => entry.Value,
            EqualityComparer<MetricId>.Default);
    }

    private static bool TryParse(string? content, string deployment, out RouterDecision decision)
    {
        decision = default!;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 4
            || !TryEnum(root, "intent", ["assessment", "repo_question", "out_of_scope", "unsafe"], out var intent)
            || !TryEnum(root, "language", ["tr", "en", "other"], out var language)
            || !TryEnum(root, "questionType", ["yes_no", "open"], out var questionType)
            || !root.TryGetProperty("metrics", out var metricsNode)
            || metricsNode.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var metrics = ImmutableArray.CreateBuilder<MetricId>();
        foreach (var node in metricsNode.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.String
                || !TryParseMetric(node.GetString(), out var metric)
                || metrics.Contains(metric))
            {
                return false;
            }

            metrics.Add(metric);
        }

        decision = new RouterDecision(
            intent,
            metrics.ToImmutable(),
            language,
            questionType,
            deployment,
            false);
        return true;
    }

    private static bool TryEnum(
        JsonElement root,
        string name,
        string[] allowed,
        out string value)
    {
        value = "";
        return root.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.String
            && (value = node.GetString() ?? "") is { Length: > 0 }
            && allowed.Contains(value, StringComparer.Ordinal);
    }

    private static MetricId ParseMetric(string value) =>
        TryParseMetric(value, out var metric)
            ? metric
            : throw new InvalidDataException($"Unknown metric keyword key: {value}");

    private static bool TryParseMetric(string? value, out MetricId metric)
    {
        metric = default;
        return value is { Length: 3 }
            && value[0] == 'm'
            && int.TryParse(value.AsSpan(1), out var number)
            && number is >= 1 and <= 10
            && (metric = (MetricId)(number - 1)) >= 0;
    }

    private static string DetectLanguage(string question) => QuestionClassifier.Language(question);

    private static string DetectQuestionType(string question)
    {
        var language = DetectLanguage(question);
        return QuestionClassifier.QuestionType(question, language);
    }

    private static JsonElement CreateRouterSchema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"ask_router","strict":true,"schema":{"type":"object","additionalProperties":false,"required":["intent","metrics","language","questionType"],"properties":{"intent":{"type":"string","enum":["assessment","repo_question","out_of_scope","unsafe"]},"metrics":{"type":"array","uniqueItems":true,"items":{"type":"string","enum":["m01","m02","m03","m04","m05","m06","m07","m08","m09","m10"]}},"language":{"type":"string","enum":["tr","en","other"]},"questionType":{"type":"string","enum":["yes_no","open"]}}}}}""");
        return document.RootElement.Clone();
    }
}
