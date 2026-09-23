using System.Net.Http;
using Hackathon.Assessment.Api.Options;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Ai;

public interface IApimCredentialProvider
{
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class SubscriptionKeyCredentialProvider : IApimCredentialProvider
{
    private readonly string _headerName;
    private readonly string _key;

    public SubscriptionKeyCredentialProvider(
        IOptions<ApimOptions> options)
    {
        _headerName = options.Value.Auth.HeaderName;
        _key = options.Value.Auth.Key ?? "";
    }

    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_headerName) || string.IsNullOrWhiteSpace(_key))
        {
            throw new InvalidOperationException(
                "APIM subscription-key credentials are not configured.");
        }

        if (!request.Headers.TryAddWithoutValidation(_headerName, _key))
        {
            throw new InvalidOperationException(
                "Apim:Auth:HeaderName is not a valid request header name.");
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class NoApimCredentialProvider : IApimCredentialProvider
{
    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
