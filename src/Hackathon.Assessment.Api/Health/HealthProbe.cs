using System.Net;

namespace Hackathon.Assessment.Api.Health;

public static class HealthProbe
{
    public static async Task<int> RunAsync(HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            using var response = await client.GetAsync(
                "http://localhost:8080/health", cancellationToken);
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }
}
