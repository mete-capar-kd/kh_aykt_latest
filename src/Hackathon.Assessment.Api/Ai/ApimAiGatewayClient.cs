using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Ai;

public sealed partial class ApimAiGatewayClient(
    HttpClient httpClient,
    IOptions<ApimOptions> options,
    IApimCredentialProvider credentialProvider,
    IRetryDelayStrategy retryDelayStrategy,
    ISecretMasker masker,
    SafetyMetrics safetyMetrics,
    ILogger<ApimAiGatewayClient> logger,
    TimeSpan? attemptTimeoutOverride = null) : IApimAiGatewayClient
{
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private readonly ApimOptions _options = options.Value;
    private readonly TimeSpan _attemptTimeout =
        attemptTimeoutOverride ?? TimeSpan.FromSeconds(options.Value.AttemptTimeoutSeconds);

    public static void ConfigureHttpClient(HttpClient client) =>
        client.Timeout = Timeout.InfiniteTimeSpan;

    public async Task<ChatResponse> ChatAsync(
        ChatRequest request,
        ModelRole role,
        AiCallContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (request.ToolChoice is not null
            && request.ToolChoice is not ("auto" or "none" or "required"))
        {
            throw new ArgumentException("ToolChoice must be auto, none, or required.", nameof(request));
        }

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(request));
        }

        var deployment = ResolveDeployment(role);
        if (IsPlaceholder(_options.BaseUrl)
            || IsPlaceholder(deployment)
            || IsPlaceholder(_options.RouteStyle)
            || (_options.RouteStyle == ApimOptions.AzureDeploymentsRouteStyle
                && (IsPlaceholder(_options.ApiVersion)
                    || string.IsNullOrWhiteSpace(_options.ApiVersion)))
            || IsPlaceholder(_options.Auth.Scheme))
        {
            throw new GatewayException(0, deployment, 0);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var state = new CallState();
        Activity? activity = null;
        AiTelemetry.TryRecord(logger, () =>
        {
            activity = AiTelemetry.ActivitySource.StartActivity(
                $"chat {deployment}", ActivityKind.Client);
        });
        if (activity is null)
        {
            AiTelemetry.TryRecord(logger, () =>
            {
                activity = new Activity($"chat {deployment}")
                    .SetIdFormat(ActivityIdFormat.W3C)
                    .Start();
            });
        }

        try
        {
            var response = await SendWithRetriesAsync(
                request,
                context,
                deployment,
                state,
                ct);
            state.LatencyMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            return response with { LatencyMs = state.LatencyMs };
        }
        catch (ContentFilteredException exception)
        {
            state.StatusCode = exception.StatusCode;
            state.FinishReason = exception.FinishReason;
            state.ExceptionType = exception.GetType().FullName;
            safetyMetrics.ContentFiltered();
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state.ExceptionType = typeof(OperationCanceledException).FullName;
            throw;
        }
        catch (GatewayException exception)
        {
            state.StatusCode = exception.StatusCode ?? state.StatusCode;
            state.RetryCount = exception.RetryCount;
            state.ExceptionType = exception.GetType().FullName;
            throw;
        }
        catch (Exception exception)
        {
            state.ExceptionType = exception.GetType().FullName;
            throw;
        }
        finally
        {
            state.LatencyMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            RecordTelemetry(activity, deployment, context, state);
            AiTelemetry.TryRecord(logger, () => activity?.Dispose());
        }
    }

    private async Task<ChatResponse> SendWithRetriesAsync(
        ChatRequest request,
        AiCallContext context,
        string deployment,
        CallState state,
        CancellationToken ct)
    {
        GatewayException? lastError = null;
        var messages = request.Messages
            .Select(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                ? message with { Content = masker.Mask(message.Content) }
                : message)
            .ToArray();

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = null;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(_attemptTimeout);
                using var message = CreateRequest(request, messages, deployment);
                await credentialProvider.ApplyAsync(message, attemptCts.Token);
                using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCts.Token);
                state.StatusCode = (int)response.StatusCode;
                state.CacheStatus = ReadCacheStatus(response);

                if (response.StatusCode == HttpStatusCode.BadRequest
                    && await GetContentFilterCodeAsync(response, attemptCts.Token) is { } filterCode)
                {
                    state.FinishReason = filterCode;
                    throw new ContentFilteredException(
                        (int)response.StatusCode, deployment, filterCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    lastError = new GatewayException(statusCode, deployment, state.RetryCount);
                    if (!IsRetryable(response.StatusCode) || attempt == _options.MaxAttempts)
                    {
                        throw lastError;
                    }

                    retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                    LogRetry(logger, statusCode, deployment, attempt);
                }
                else
                {
                    var wireResponse = await JsonSerializer.DeserializeAsync(
                        await response.Content.ReadAsStreamAsync(attemptCts.Token),
                        AppJsonContext.Default.OpenAiChatResponse,
                        attemptCts.Token);
                    if (wireResponse is null
                        || wireResponse.Choices.IsDefaultOrEmpty
                        || wireResponse.Choices[0].Message is null)
                    {
                        throw new GatewayException((int)response.StatusCode, deployment, state.RetryCount);
                    }

                    var choice = wireResponse.Choices[0];
                    state.FinishReason = choice.FinishReason;
                    state.InputTokens = wireResponse.Usage?.PromptTokens;
                    state.OutputTokens = wireResponse.Usage?.CompletionTokens;
                    state.CachedTokens = wireResponse.Usage?.PromptTokensDetails?.CachedTokens;
                    if (string.Equals(choice.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ContentFilteredException(
                            (int)response.StatusCode, deployment, choice.FinishReason ?? "content_filter");
                    }

                    var toolCalls = choice.Message.ToolCalls.IsDefault
                        ? System.Collections.Immutable.ImmutableArray<ChatToolCall>.Empty
                        : [.. choice.Message.ToolCalls.Select(toolCall =>
                            new ChatToolCall(
                                toolCall.Id,
                                toolCall.Function.Name,
                                toolCall.Function.Arguments))];
                    return new ChatResponse(
                        choice.Message.Content,
                        toolCalls,
                        choice.FinishReason,
                        wireResponse.Usage is null
                            ? null
                            : new ChatUsage(
                            wireResponse.Usage?.PromptTokens,
                            wireResponse.Usage?.CompletionTokens,
                            wireResponse.Usage?.PromptTokensDetails?.CachedTokens),
                        deployment,
                        state.LatencyMs,
                        state.RetryCount,
                        state.CacheStatus);
                }
            }
            catch (ContentFilteredException)
            {
                throw;
            }
            catch (GatewayException exception) when (exception.StatusCode == 0)
            {
                throw;
            }
            catch (GatewayException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastError = new GatewayException(0, deployment, state.RetryCount);
                LogRetry(logger, 0, deployment, attempt);
            }
            catch (HttpRequestException)
            {
                lastError = new GatewayException(0, deployment, state.RetryCount);
                LogRetry(logger, 0, deployment, attempt);
            }
            catch (JsonException)
            {
                throw new GatewayException(state.StatusCode, deployment, state.RetryCount);
            }

            if (attempt == _options.MaxAttempts)
            {
                throw lastError ?? new GatewayException(state.StatusCode, deployment, state.RetryCount);
            }

            var delay = retryDelayStrategy.GetDelay(attempt, retryAfter);
            state.RetryCount++;
            await retryDelayStrategy.DelayAsync(delay, ct);
        }

        throw lastError ?? new GatewayException(state.StatusCode, deployment, state.RetryCount);
    }

    private HttpRequestMessage CreateRequest(
        ChatRequest request,
        IReadOnlyList<ChatMessage> messages,
        string deployment)
    {
        var openAiV1 = _options.RouteStyle == ApimOptions.OpenAiV1RouteStyle;
        var wireRequest = new OpenAiChatRequest(
            messages,
            request.Tools,
            request.ToolChoice,
            request.Temperature,
            request.MaxOutputTokens,
            request.ResponseFormatJsonSchema,
            openAiV1 ? deployment : null);
        var url = BuildRequestUri(deployment, openAiV1);
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(
                wireRequest,
                AppJsonContext.Default.OpenAiChatRequest)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            message.Headers.TryAddWithoutValidation("traceparent", traceParent);
            if (Activity.Current.TraceStateString is { Length: > 0 } traceState)
            {
                message.Headers.TryAddWithoutValidation("tracestate", traceState);
            }
        }
        return message;
    }

    private Uri BuildRequestUri(string deployment, bool openAiV1)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        if (openAiV1)
        {
            return new Uri($"{baseUrl}/openai/v1/chat/completions");
        }

        return new Uri(
            $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(_options.ApiVersion)}");
    }

    private string ResolveDeployment(ModelRole role) =>
        role == ModelRole.Synthesizer
            ? _options.Deployments.Strong
            : _options.Deployments.Cheap;

    private static bool IsPlaceholder(string? value) =>
        string.Equals(value, ApimOptions.OrganizationPlaceholder, StringComparison.Ordinal);

    private bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private async Task<string?> GetContentFilterCodeAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(response, ct);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!TryGetPropertyIgnoreCase(json.RootElement, "error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetPropertyIgnoreCase(error, "code", out var code)
                && code.ValueKind == JsonValueKind.String
                && ContentFilterCode(code.GetString()) is { } errorCode)
            {
                return errorCode;
            }

            return TryGetPropertyIgnoreCase(error, "innererror", out var innerError)
                && innerError.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(innerError, "code", out var innerCode)
                && innerCode.ValueKind == JsonValueKind.String
                    ? ContentFilterCode(innerCode.GetString())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length <= MaximumErrorBodyBytes)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumErrorBodyBytes)
            {
                return [];
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return [];
    }

    private static string? ContentFilterCode(string? code) =>
        string.Equals(code, "content_filter", StringComparison.OrdinalIgnoreCase)
            ? "content_filter"
            : string.Equals(code, "ResponsibleAIPolicyViolation", StringComparison.OrdinalIgnoreCase)
                ? "ResponsibleAIPolicyViolation"
                : null;

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter?.Date is { } date
            ? date - DateTimeOffset.UtcNow
            : null;
    }

    private string? ReadCacheStatus(HttpResponseMessage response)
    {
        var headerName = _options.CacheStatusHeader;
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return null;
        }

        if (response.Headers.TryGetValues(headerName, out var values))
        {
            return MaskCacheStatus(values.FirstOrDefault());
        }

        return response.Content.Headers.TryGetValues(headerName, out values)
            ? MaskCacheStatus(values.FirstOrDefault())
            : null;
    }

    private string? MaskCacheStatus(string? value) =>
        value is null ? null : masker.Mask(value);

    private void RecordTelemetry(
        Activity? activity,
        string deployment,
        AiCallContext context,
        CallState state)
    {
        var tags = new TagList
        {
            { "deployment", deployment },
            { "status", state.FinishReason == "content_filter" ? "content_filtered" :
                state.ExceptionType is null ? "success" : "error" },
            { "retry.count", state.RetryCount }
        };
        if (context.MetricId is { } metricId)
        {
            tags.Add("metric.id", $"m{(int)metricId + 1:00}");
        }

        AiTelemetry.TryRecord(logger, () =>
        {
            activity?.SetTag("gen_ai.request.model", deployment);
            if (state.FinishReason is not null)
            {
                activity?.SetTag("gen_ai.response.finish_reason", state.FinishReason);
            }
            if (state.StatusCode > 0)
            {
                activity?.SetTag("http.response.status_code", state.StatusCode);
            }
            activity?.SetTag("retry.count", state.RetryCount);
            if (context.MetricId is { } id)
            {
                activity?.SetTag("metric.id", $"m{(int)id + 1:00}");
            }
            activity?.SetTag("correlation.id", context.CorrelationId);
            if (state.CacheStatus is not null)
            {
                activity?.SetTag("ai.cache_status", state.CacheStatus);
            }
            if (state.InputTokens is { } activityInputTokens)
            {
                activity?.SetTag("gen_ai.usage.input_tokens", activityInputTokens);
            }
            if (state.OutputTokens is { } activityOutputTokens)
            {
                activity?.SetTag("gen_ai.usage.output_tokens", activityOutputTokens);
            }
            if (state.CachedTokens is { } activityCachedTokens)
            {
                activity?.SetTag("gen_ai.usage.cached_tokens", activityCachedTokens);
            }
            if (state.ExceptionType is not null)
            {
                activity?.SetTag("exception.type", state.ExceptionType);
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            AiTelemetry.RequestDuration.Record(state.LatencyMs, tags);
            AiTelemetry.Requests.Add(1, tags);
            if (state.InputTokens is { } inputTokens)
            {
                AiTelemetry.InputTokens.Add(inputTokens, tags);
            }

            if (state.OutputTokens is { } outputTokens)
            {
                AiTelemetry.OutputTokens.Add(outputTokens, tags);
            }

            if (state.CachedTokens is { } cachedTokens)
            {
                AiTelemetry.CachedTokens.Add(cachedTokens, tags);
            }
        });
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "APIM request retry. StatusCode={StatusCode}, Deployment={Deployment}, Attempt={Attempt}.")]
    private static partial void LogRetry(
        ILogger logger,
        int statusCode,
        string deployment,
        int attempt);

    private sealed class CallState
    {
        public int StatusCode { get; set; }
        public int RetryCount { get; set; }
        public long LatencyMs { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? CachedTokens { get; set; }
        public string? CacheStatus { get; set; }
        public string? FinishReason { get; set; }
        public string? ExceptionType { get; set; }
    }
}
