using Hackathon.Assessment.Api.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Hackathon.Assessment.Api.Auth;

public static class EntraIdAuthentication
{
    public const string AskPolicyName = "AskPolicy";

    public static IServiceCollection AddEntraIdAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(EntraIdOptions.SectionName);

        services.AddOptions<EntraIdOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var settings = section.Get<EntraIdOptions>() ?? new EntraIdOptions();
                options.Authority = $"{settings.Instance}{settings.TenantId}/v2.0";
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuers =
                    [
                        $"https://login.microsoftonline.com/{settings.TenantId}/v2.0",
                        $"https://sts.windows.net/{settings.TenantId}/"
                    ],
                    ValidAudiences = GetValidAudiences(settings.Audience),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers["WWW-Authenticate"] = "Bearer";
                        await ApiProblemResults
                            .Problem(
                                context.HttpContext,
                                StatusCodes.Status401Unauthorized,
                                "Unauthorized")
                            .ExecuteAsync(context.HttpContext);
                    },
                    OnForbidden = async context =>
                    {
                        await ApiProblemResults
                            .Problem(
                                context.HttpContext,
                                StatusCodes.Status403Forbidden,
                                "Forbidden")
                            .ExecuteAsync(context.HttpContext);
                    }
                };
            });

        services.AddAuthorization(options =>
            options.AddPolicy(
                AskPolicyName,
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new AskPolicyRequirement());
                }));
        services.AddSingleton<IAuthorizationHandler, AskPolicyAuthorizationHandler>();

        return services;
    }

    private static string[] GetValidAudiences(string audience)
    {
        var validAudiences = new HashSet<string>(StringComparer.Ordinal) { audience };
        const string ApiPrefix = "api://";

        if (Guid.TryParse(audience, out _))
        {
            validAudiences.Add($"{ApiPrefix}{audience}");
        }
        else if (audience.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var applicationId = audience[ApiPrefix.Length..];
            if (Guid.TryParse(applicationId, out _))
            {
                validAudiences.Add(applicationId);
            }
        }

        return [.. validAudiences];
    }
}
