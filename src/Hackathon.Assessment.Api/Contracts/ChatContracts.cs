using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Tools;

namespace Hackathon.Assessment.Api.Contracts;

public enum ModelRole
{
    Router,
    Profiler,
    Evaluator,
    Synthesizer
}

public sealed record AiCallContext(
    string CorrelationId,
    MetricId? MetricId,
    string Purpose);

public sealed record ChatMessage(
    string Role,
    string Content,
    [property: JsonPropertyName("tool_call_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<ChatToolCallRequest> ToolCalls = default);

public sealed record ChatToolCallRequest(
    string Id,
    string Type,
    ChatFunctionCallRequest Function);

public sealed record ChatFunctionCallRequest(string Name, string Arguments);

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<OpenAiToolDefinition>? Tools = null,
    string? ToolChoice = null,
    double Temperature = 0,
    int? MaxOutputTokens = null,
    JsonElement? ResponseFormatJsonSchema = null);

public sealed record ChatToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record ChatUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CachedTokens);

public sealed record ChatResponse(
    string? Content,
    ImmutableArray<ChatToolCall> ToolCalls,
    string? FinishReason,
    ChatUsage? Usage,
    string Deployment,
    long LatencyMs,
    int RetryCount,
    string? CacheStatus);

public sealed record OpenAiChatRequest(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("tools")] IReadOnlyList<OpenAiToolDefinition>? Tools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("tool_choice")] string? ToolChoice,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("max_tokens")] int? MaxOutputTokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("response_format")] JsonElement? ResponseFormatJsonSchema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("model")] string? Model);

public sealed record OpenAiChatResponse(
    [property: JsonPropertyName("choices")] ImmutableArray<OpenAiChatChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiChatUsage? Usage);

public sealed record OpenAiChatChoice(
    [property: JsonPropertyName("message")] OpenAiChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] ImmutableArray<OpenAiChatToolCall> ToolCalls);

public sealed record OpenAiChatToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] OpenAiChatFunction Function);

public sealed record OpenAiChatFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

public sealed record OpenAiChatUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("prompt_tokens_details")] OpenAiPromptTokenDetails? PromptTokensDetails);

public sealed record OpenAiPromptTokenDetails(
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens);
