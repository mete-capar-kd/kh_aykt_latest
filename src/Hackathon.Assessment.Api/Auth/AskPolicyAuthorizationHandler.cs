using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Auth;

public sealed class AskPolicyRequirement : IAuthorizationRequirement;

public sealed class AskPolicyAuthorizationHandler(IOptions<EntraIdOptions> options)
    : AuthorizationHandler<AskPolicyRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AskPolicyRequirement requirement)
    {
        var settings = options.Value;
        var requiredRoles = settings.RequiredRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToArray();
        var requiredScopes = settings.RequiredScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .ToArray();

        if ((requiredRoles.Length == 0 && requiredScopes.Length == 0)
            || context.User.FindAll("roles")
                .Any(claim => requiredRoles.Contains(claim.Value, StringComparer.Ordinal))
            || context.User.FindAll("scp")
                .SelectMany(claim => claim.Value.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Any(scope => requiredScopes.Contains(scope, StringComparer.Ordinal)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
