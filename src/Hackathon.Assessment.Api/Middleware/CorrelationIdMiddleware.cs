using System.Text.RegularExpressions;

namespace Hackathon.Assessment.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public static readonly object ItemKey = new();

    private static readonly Regex ValidCorrelationId = new(
        @"\A[A-Za-z0-9._-]{1,64}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task InvokeAsync(HttpContext context)
    {
        var requestedId = context.Request.Headers[HeaderName].ToString();
        var correlationId = ValidCorrelationId.IsMatch(requestedId)
            ? requestedId
            : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await next(context);
        }
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string correlationId
            ? correlationId
            : Guid.NewGuid().ToString("N");
}
