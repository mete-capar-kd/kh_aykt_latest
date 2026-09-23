using System.Globalization;
using System.Threading.RateLimiting;
using Hackathon.Assessment.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Middleware;

public static class AskRateLimiting
{
    public const string PolicyName = "ask";

    public static void AddAskRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, context =>
            {
                var settings = context.RequestServices
                    .GetRequiredService<IOptions<RateLimitOptions>>()
                    .Value;
                var callerId = CallerIdentity.GetCallerId(context);
                if (settings.ExemptClientIds.Contains(callerId, StringComparer.Ordinal))
                {
                    return RateLimitPartition.GetNoLimiter(callerId);
                }

                var partitionKey = CallerIdentity.GetRateLimitPartitionKey(context);
                return RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey,
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = settings.PermitsPerMinute,
                        TokensPerPeriod = settings.PermitsPerMinute,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = settings.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            options.OnRejected = async (rejected, _) =>
            {
                var retryAfterSeconds = rejected.Lease.TryGetMetadata(
                    MetadataName.RetryAfter,
                    out TimeSpan retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                rejected.HttpContext.Response.Headers["Retry-After"] =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                await ApiProblemResults
                    .Problem(
                        rejected.HttpContext,
                        StatusCodes.Status429TooManyRequests,
                        "The request rate limit has been exceeded.")
                    .ExecuteAsync(rejected.HttpContext);
            };
        });
    }
}
