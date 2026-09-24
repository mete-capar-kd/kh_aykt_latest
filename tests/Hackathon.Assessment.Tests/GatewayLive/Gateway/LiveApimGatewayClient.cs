using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Options;
using Microsoft.Extensions.Configuration;

namespace Hackathon.Assessment.Tests.GatewayLive.Gateway;

internal sealed class LiveApimGatewayClient : IDisposable
{
    private const int MaximumResponseBytes = 1_048_576;

    private readonly ApimOptions _options;
    private readonly string _key;
    private readonly HttpClient _httpClient;

    private LiveApimGatewayClient(ApimOptions options, string key)
    {
        _options = options;
        _key = key;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(240)
        };
    }

    public static LiveApimGatewayClient Create(bool requireCacheStatusHeader = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var options = new ApimOptions();
        configuration.GetSection(ApimOptions.SectionName).Bind(options);
        var key = Environment.GetEnvironmentVariable("APIM_TEST_KEY");
        var missing = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            missing.Add("Apim:BaseUrl");
        }

        if (options.RouteStyle is not (ApimOptions.AzureDeploymentsRouteStyle
                or ApimOptions.OpenAiV1RouteStyle))
        {
            missing.Add("Apim:RouteStyle");
        }

        if (options.RouteStyle == ApimOptions.AzureDeploymentsRouteStyle
            && (string.IsNullOrWhiteSpace(options.ApiVersion)
                || options.ApiVersion == ApimOptions.OrganizationPlaceholder))
        {
            missing.Add("Apim:ApiVersion");
        }

        if (string.IsNullOrWhiteSpace(options.Deployments.Cheap)
            || options.Deployments.Cheap == ApimOptions.OrganizationPlaceholder)
        {
            missing.Add("Apim:Deployments:Cheap");
        }

        if (options.Auth.Scheme != "SubscriptionKey")
        {
            missing.Add("Apim:Auth:Scheme (SubscriptionKey required)");
        }

        if (string.IsNullOrWhiteSpace(options.Auth.HeaderName)
            || options.Auth.HeaderName == ApimOptions.OrganizationPlaceholder)
        {
            missing.Add("Apim:Auth:HeaderName");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            missing.Add("APIM_TEST_KEY");
        }

        if (requireCacheStatusHeader && string.IsNullOrWhiteSpace(options.CacheStatusHeader))
        {
            missing.Add("Apim:CacheStatusHeader");
        }

        if (missing.Count > 0)
        {
            throw new LiveGatewayConfigurationException(
                $"BLOCKED: {string.Join(", ", missing)} yok");
        }

        return new LiveApimGatewayClient(options, key!);
    }

    public string CacheStatusHeaderName => _options.CacheStatusHeader;

    public async Task<LiveGatewayResponse> SendAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var openAiV1 = _options.RouteStyle == ApimOptions.OpenAiV1RouteStyle;
        var wireRequest = new OpenAiChatRequest(
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            ],
            null,
            null,
            0,
            256,
            null,
            openAiV1 ? _options.Deployments.Cheap : null);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildRequestUri(openAiV1))
        {
            Content = JsonContent.Create(
                wireRequest,
                AppJsonContext.Default.OpenAiChatRequest)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!request.Headers.TryAddWithoutValidation(_options.Auth.HeaderName, _key))
        {
            request.Dispose();
            throw new LiveGatewayConfigurationException(
                "BLOCKED: Apim:Auth:HeaderName geçerli değil");
        }

        using (request)
        {
            var startedAt = Stopwatch.GetTimestamp();
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await ReadBoundedBodyAsync(response, cancellationToken);
            var latencyMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var cacheStatus = _options.CacheStatusHeader.Length > 0
                && response.Headers.TryGetValues(_options.CacheStatusHeader, out var values)
                    ? values.FirstOrDefault()
                    : null;

            return new LiveGatewayResponse(
                (int)response.StatusCode,
                body,
                cacheStatus,
                latencyMs);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private Uri BuildRequestUri(bool openAiV1)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        if (openAiV1)
        {
            return new Uri($"{baseUrl}/openai/v1/chat/completions");
        }

        return new Uri(
            $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(_options.Deployments.Cheap)}/chat/completions?api-version={Uri.EscapeDataString(_options.ApiVersion)}");
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaximumResponseBytes)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    "APIM verification response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        throw new InvalidDataException(
            "APIM verification response exceeded the configured size limit.");
    }
}

internal sealed record LiveGatewayResponse(
    int StatusCode,
    string Body,
    string? CacheStatus,
    long LatencyMs);

internal sealed class LiveGatewayConfigurationException(string message)
    : InvalidOperationException(message)
{
}
