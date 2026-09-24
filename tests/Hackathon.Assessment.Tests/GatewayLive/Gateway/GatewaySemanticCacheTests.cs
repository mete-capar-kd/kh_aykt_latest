using System.Text.Json;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Tests.GatewayLive;
using Xunit;

namespace Hackathon.Assessment.Tests.GatewayLive.Gateway;

public sealed class GatewaySemanticCacheTests
{
    private const string SystemPrompt =
        "Answer briefly in one sentence. Do not include identifiers or sensitive information.";

    [Fact]
    [Trait("Category", "GatewayLive")]
    public async Task VerifiesConfiguredMissToHitAndReportsUsageAndLatency()
    {
        LiveApimGatewayClient gateway;
        try
        {
            gateway = LiveApimGatewayClient.Create(requireCacheStatusHeader: true);
        }
        catch (LiveGatewayConfigurationException exception)
        {
            await GatewayLiveArtifactWriter.WriteAsync(
                "gateway-semantic-cache.json",
                new CacheProbeResult("failed", exception.Message, "", null, null, null, null, null));
            throw;
        }

        using (gateway)
        {
            LiveGatewayResponse? first = null;
            LiveGatewayResponse? second = null;
            var failures = new List<string>();

            try
            {
                first = await gateway.SendAsync(
                    SystemPrompt,
                    "Describe the color blue briefly in one sentence.");
                second = await gateway.SendAsync(
                    SystemPrompt,
                    "In one short sentence, describe blue.");
            }
            catch (HttpRequestException)
            {
                failures.Add("Direct APIM request failed.");
            }
            catch (TaskCanceledException)
            {
                failures.Add("Direct APIM request timed out.");
            }
            catch (InvalidDataException)
            {
                failures.Add("Direct APIM response exceeded the verification limit.");
            }

            var firstObservation = Observe(first);
            var secondObservation = Observe(second);
            if (first is null || first.StatusCode is < 200 or >= 300)
            {
                failures.Add("First cache probe did not return a successful response.");
            }

            if (second is null || second.StatusCode is < 200 or >= 300)
            {
                failures.Add("Second cache probe did not return a successful response.");
            }

            if (!string.Equals(NormalizeCacheStatus(first?.CacheStatus), "miss", StringComparison.Ordinal))
            {
                failures.Add("Configured cache-status header did not report a first-request miss.");
            }

            if (!string.Equals(NormalizeCacheStatus(second?.CacheStatus), "hit", StringComparison.Ordinal))
            {
                failures.Add("Configured cache-status header did not report a follow-up hit.");
            }

            if (firstObservation?.InputTokens is null || secondObservation?.InputTokens is null)
            {
                failures.Add("Gateway usage metadata was unavailable.");
            }

            var report = new CacheProbeResult(
                failures.Count == 0 ? "passed" : "failed",
                failures.Count == 0
                    ? "Miss-to-hit verified; usage and latency deltas are reported for review."
                    : string.Join("; ", failures),
                gateway.CacheStatusHeaderName,
                firstObservation,
                secondObservation,
                Difference(firstObservation?.InputTokens, secondObservation?.InputTokens),
                Difference(firstObservation?.CachedTokens, secondObservation?.CachedTokens),
                Difference(firstObservation?.LatencyMs, secondObservation?.LatencyMs));
            await GatewayLiveArtifactWriter.WriteAsync("gateway-semantic-cache.json", report);

            Assert.True(failures.Count == 0, report.Reason);
        }
    }

    private static GatewayProbeObservation? Observe(LiveGatewayResponse? response)
    {
        if (response is null)
        {
            return null;
        }

        GatewayUsage? usage = null;
        try
        {
            var wireResponse = JsonSerializer.Deserialize(
                response.Body,
                AppJsonContext.Default.OpenAiChatResponse);
            if (wireResponse is not null
                && !wireResponse.Choices.IsDefaultOrEmpty
                && wireResponse.Choices[0].Message is not null
                && !string.IsNullOrWhiteSpace(wireResponse.Choices[0].Message.Content))
            {
                usage = new GatewayUsage(
                    wireResponse.Usage?.PromptTokens,
                    wireResponse.Usage?.PromptTokensDetails?.CachedTokens);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return new GatewayProbeObservation(
            response.StatusCode,
            response.CacheStatus,
            usage?.InputTokens,
            usage?.CachedTokens,
            response.LatencyMs);
    }

    private static string? NormalizeCacheStatus(string? status) =>
        status?.Trim().Split(',', 2)[0].Trim().ToLowerInvariant();

    private static int? Difference(int? first, int? second) =>
        first.HasValue && second.HasValue ? second.Value - first.Value : null;

    private static long? Difference(long? first, long? second) =>
        first.HasValue && second.HasValue ? second.Value - first.Value : null;

    private sealed record GatewayUsage(int? InputTokens, int? CachedTokens);

    private sealed record GatewayProbeObservation(
        int StatusCode,
        string? CacheStatus,
        int? InputTokens,
        int? CachedTokens,
        long LatencyMs);

    private sealed record CacheProbeResult(
        string Status,
        string Reason,
        string CacheStatusHeader,
        GatewayProbeObservation? First,
        GatewayProbeObservation? Second,
        int? InputTokenDelta,
        int? CachedTokenDelta,
        long? LatencyDeltaMs);
}
