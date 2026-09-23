using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Hackathon.Assessment.Api.Middleware;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public const long MaxBodyBytes = 32 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/api/ask", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxBodyBytes;
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ApiProblemResults
                .Problem(context, StatusCodes.Status413PayloadTooLarge, "The request body is too large.")
                .ExecuteAsync(context);
            return;
        }

        if (IsJsonContentType(context.Request.ContentType))
        {
            context.Request.EnableBuffering((int)MaxBodyBytes, MaxBodyBytes);
            try
            {
                using var json = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await WriteInvalidJsonAsync(context);
                    return;
                }
            }
            catch (JsonException)
            {
                await WriteInvalidJsonAsync(context);
                return;
            }
            finally
            {
                if (context.Request.Body.CanSeek)
                {
                    context.Request.Body.Position = 0;
                }
            }
        }

        await next(context);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static Task WriteInvalidJsonAsync(HttpContext context) =>
        ApiProblemResults.ValidationProblem(
                context,
                new Dictionary<string, string[]>
                {
                    ["body"] = ["The JSON request body is invalid."]
                })
            .ExecuteAsync(context);
}
