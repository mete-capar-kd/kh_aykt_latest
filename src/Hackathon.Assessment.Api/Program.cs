using System.Text.Encodings.Web;
using System.Text.Unicode;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Endpoints;
using Hackathon.Assessment.Api.Health;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Middleware;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Safety;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Tools;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApplicationUptime>();
builder.Services.AddSingleton<IInputGuard, InputGuard>();
builder.Services.AddSingleton<IAskOrchestrator, StubAskOrchestrator>();
builder.Services.AddScoped<AskEndpoints.AskRequestValidationFilter>();
builder.Services.AddHttpClient("github")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = IpAddressPolicy.ConnectAsync
    });
builder.Services.AddSingleton<IRepositorySnapshotProvider, GitHubRepositorySnapshotProvider>();
builder.Services.AddSingleton<ISecretMasker, SecretMasker>();
builder.Services.AddSingleton<GlobMatcher>();
builder.Services.AddSingleton<IScannerRunner, EmptyScannerRunner>();
builder.Services.AddSingleton<RecordFindingValidator>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, GetRepoManifestTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, ListFilesTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, SearchCodeTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, ReadFileTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, RunScannerTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, RecordFindingTool>();
builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();

builder.Services.AddOptions<AssessmentOptions>()
    .BindConfiguration("Assessment")
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RepositoryOptions>()
    .BindConfiguration(RepositoryOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>()
    .BindConfiguration(RateLimitOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RepositoryOptions>, OrganizationPlaceholderValidator>();
builder.Services.AddAskRateLimiter();

var app = builder.Build();

_ = app.Services.GetRequiredService<IInputGuard>();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.MapAskEndpoint();
app.MapHealthEndpoint();

app.Run();

public partial class Program
{
}
