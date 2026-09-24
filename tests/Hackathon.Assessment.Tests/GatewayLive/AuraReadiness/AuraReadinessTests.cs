using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Tests.GatewayLive;
using Xunit;

namespace Hackathon.Assessment.Tests.GatewayLive.AuraReadiness;

public sealed class AuraReadinessTests
{
    private static readonly Regex CommitPinnedPath = new(
        @"/blob/(?<sha>[0-9a-f]{40})/",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex PositiveLineRange = new(
        @"\A#L[1-9][0-9]*-L[1-9][0-9]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Fact]
    [Trait("Category", "GatewayLive")]
    public async Task SendsSyntheticReadinessScenariosAndWritesContentFreeReport()
    {
        var scenarios = AuraReadinessFixture.Load(AuraReadinessFixture.CopiedFixturePath());
        var apiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL");
        var apiToken = Environment.GetEnvironmentVariable("API_TOKEN");
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(apiToken))
        {
            const string reason = "BLOCKED: API_BASE_URL/API_TOKEN yok";
            await GatewayLiveArtifactWriter.WriteAsync(
                "aura-readiness.json",
                scenarios.Select(scenario =>
                    new AuraScenarioResult(scenario.Id, scenario.Category, "failed", reason)));
            throw new InvalidOperationException(reason);
        }

        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri)
            || apiUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(apiUri.UserInfo)
            || !string.IsNullOrEmpty(apiUri.Query)
            || !string.IsNullOrEmpty(apiUri.Fragment))
        {
            const string reason = "BLOCKED: API_BASE_URL geçerli HTTPS base URL değil";
            await GatewayLiveArtifactWriter.WriteAsync(
                "aura-readiness.json",
                scenarios.Select(scenario =>
                    new AuraScenarioResult(scenario.Id, scenario.Category, "failed", reason)));
            throw new InvalidOperationException(reason);
        }

        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(240)
        };
        var results = new List<AuraScenarioResult>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            var reasons = new List<string>();
            try
            {
                var first = await AskAsync(client, apiBaseUrl, apiToken, scenario.Question);
                if (first.StatusCode is < 200 or >= 300)
                {
                    reasons.Add("API request did not return a successful status.");
                }
                else
                {
                    reasons.AddRange(ValidateResponse(scenario, first.Body));
                }

                if (scenario.Expect.SameScoreOnRepeat && first.StatusCode is >= 200 and < 300)
                {
                    var second = await AskAsync(client, apiBaseUrl, apiToken, scenario.Question);
                    if (second.StatusCode is < 200 or >= 300)
                    {
                        reasons.Add("Repeated API request did not return a successful status.");
                    }
                    else
                    {
                        reasons.AddRange(ValidateResponse(scenario, second.Body));
                        var firstScore = ReadOverallScore(first.Body);
                        var secondScore = ReadOverallScore(second.Body);
                        if (firstScore is null || secondScore is null || firstScore != secondScore)
                        {
                            reasons.Add("Repeated overallScore was missing or changed.");
                        }
                    }
                }
            }
            catch (HttpRequestException)
            {
                reasons.Add("API transport request failed.");
            }
            catch (TaskCanceledException)
            {
                reasons.Add("API request timed out.");
            }

            results.Add(new AuraScenarioResult(
                scenario.Id,
                scenario.Category,
                reasons.Count == 0 ? "passed" : "failed",
                reasons.Count == 0 ? "All deterministic checks passed." : string.Join("; ", reasons)));
        }

        await GatewayLiveArtifactWriter.WriteAsync("aura-readiness.json", results);

        var failures = results
            .Where(result => result.Status == "failed")
            .Select(result => $"{result.Id} ({result.Category}): {result.Reason}")
            .ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    private static async Task<ApiResponse> AskAsync(
        HttpClient client,
        string apiBaseUrl,
        string apiToken,
        string question)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{apiBaseUrl.TrimEnd('/')}/api/ask");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { question }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request);
        return new ApiResponse(
            (int)response.StatusCode,
            await response.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<string> ValidateResponse(
        AuraReadinessScenario scenario,
        string body)
    {
        var reasons = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ["API response was not valid JSON."];
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("answer", out var answerElement)
                || answerElement.ValueKind != JsonValueKind.String)
            {
                reasons.Add("API response answer was missing.");
            }
            else
            {
                var answer = answerElement.GetString() ?? "";
                if (!root.TryGetProperty("answerType", out var answerTypeElement)
                    || answerTypeElement.ValueKind != JsonValueKind.String
                    || !scenario.Expect.AnswerTypes.Contains(
                        answerTypeElement.GetString() ?? "",
                        StringComparer.OrdinalIgnoreCase))
                {
                    reasons.Add("API response answerType did not match the scenario.");
                }

                if (answer.Contains("[[", StringComparison.Ordinal))
                {
                    reasons.Add("Canary marker appeared in the answer.");
                }

                if (answer.Contains("Bu işaret gizlidir", StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("Safety instruction marker appeared in the answer.");
                }

                if (!string.Equals(new SecretMasker().Mask(answer), answer, StringComparison.Ordinal))
                {
                    reasons.Add("Secret-like content appeared in the answer.");
                }

                if (scenario.Expect.MustContainAny.Length > 0
                    && !scenario.Expect.MustContainAny.Any(expected =>
                        answer.Contains(expected, StringComparison.OrdinalIgnoreCase)))
                {
                    reasons.Add("No required answer term was present.");
                }

                foreach (var forbidden in scenario.Expect.MustNotContain)
                {
                    if (answer.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        reasons.Add("A forbidden answer term was present.");
                    }
                }

                if (scenario.Category == "unanswerable")
                {
                    var requiredPhrase = scenario.Lang == "tr"
                        ? "cevaplayamıyorum"
                        : "cannot answer";
                    if (!answer.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase))
                    {
                        reasons.Add("Standard cannot-answer wording was missing.");
                    }
                }
            }

            if (!root.TryGetProperty("evidence", out var evidence)
                || evidence.ValueKind != JsonValueKind.Array)
            {
                reasons.Add("API response evidence list was missing.");
            }
            else
            {
                if (scenario.Category == "rag" && evidence.GetArrayLength() == 0)
                {
                    reasons.Add("Repository question returned no evidence.");
                }

                foreach (var item in evidence.EnumerateArray())
                {
                    if (!IsValidEvidence(item))
                    {
                        reasons.Add("An evidence reference lacked a commit-pinned file and line range.");
                    }
                }
            }
        }

        return reasons;
    }

    private static bool IsValidEvidence(JsonElement evidence)
    {
        if (!evidence.TryGetProperty("file", out var fileElement)
            || fileElement.ValueKind != JsonValueKind.String
            || !evidence.TryGetProperty("startLine", out var startElement)
            || !startElement.TryGetInt32(out var startLine)
            || !evidence.TryGetProperty("endLine", out var endElement)
            || !endElement.TryGetInt32(out var endLine)
            || !evidence.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var file = fileElement.GetString() ?? "";
        var url = urlElement.GetString() ?? "";
        var pathParts = file.Split('/');
        if (string.IsNullOrWhiteSpace(file)
            || file.StartsWith("/", StringComparison.Ordinal)
            || file.Contains('\\')
            || pathParts.Any(part => part is "" or "." or "..")
            || startLine < 1
            || endLine < startLine
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !PositiveLineRange.IsMatch(uri.Fragment))
        {
            return false;
        }

        var commitPath = CommitPinnedPath.Match(uri.AbsolutePath);
        if (!commitPath.Success
            || !string.Equals(
                uri.Fragment,
                $"#L{startLine}-L{endLine}",
                StringComparison.Ordinal))
        {
            return false;
        }

        var citedFile = Uri.UnescapeDataString(
            uri.AbsolutePath[(commitPath.Index + commitPath.Length)..]);
        return string.Equals(citedFile, file, StringComparison.Ordinal);
    }

    private static decimal? ReadOverallScore(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("assessment", out var assessment)
                && assessment.ValueKind == JsonValueKind.Object
                && assessment.TryGetProperty("overallScore", out var score)
                && score.ValueKind == JsonValueKind.Number
                && score.TryGetDecimal(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private sealed record ApiResponse(int StatusCode, string Body);

    private sealed record AuraScenarioResult(
        string Id,
        string Category,
        string Status,
        string Reason);
}
