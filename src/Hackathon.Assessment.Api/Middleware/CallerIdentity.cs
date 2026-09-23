namespace Hackathon.Assessment.Api.Middleware;

public static class CallerIdentity
{
    public static string GetCallerId(HttpContext context)
    {
        var objectId = context.User.FindFirst("oid")?.Value;
        if (!string.IsNullOrWhiteSpace(objectId))
        {
            return objectId;
        }

        var subject = context.User.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return subject;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    public static string GetRateLimitPartitionKey(HttpContext context)
    {
        var objectId = context.User.FindFirst("oid")?.Value;
        if (!string.IsNullOrEmpty(objectId))
        {
            return $"oid:{objectId}";
        }

        var subject = context.User.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            return $"sub:{subject}";
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(remoteIp) ? "anonymous" : $"ip:{remoteIp}";
    }
}
