using System.Text.Encodings.Web;
using System.Text.Unicode;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Auth;
using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Endpoints;
using Hackathon.Assessment.Api.Health;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Middleware;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Reporting;
using Hackathon.Assessment.Api.Safety;
using Hackathon.Assessment.Api.Scanners;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Telemetry;
using Hackathon.Assessment.Api.Tools;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

if (args.Contains("--healthcheck")) return await HealthProbe.RunAsync(null, default);

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEntraIdAuthentication(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApplicationUptime>();
builder.Services.AddSingleton<SafetyMetrics>();
builder.Services.AddSingleton<SafetyCanary>();
builder.Services.AddSingleton<IInputGuard, InputGuard>();
builder.Services.AddSingleton<AskOrchestrator>();
builder.Services.AddSingleton<IAskOrchestrator>(services =>
    services.GetRequiredService<AskOrchestrator>());
builder.Services.AddHostedService<CacheWarmupHostedService>();
builder.Services.AddSingleton(services =>
    new PromptCatalog(
        Path.Combine(AppContext.BaseDirectory, "prompts"),
        services.GetRequiredService<SafetyCanary>()));
builder.Services.AddSingleton<AskRouterAgent>();
builder.Services.AddSingleton<RefusalBuilder>();
builder.Services.AddSingleton<OutputGuard>();
builder.Services.AddSingleton<ProfilerAgent>();
builder.Services.AddSingleton<MetricEvaluator>();
builder.Services.AddSingleton<SynthesizerAgent>();
builder.Services.AddSingleton<ReportBuilder>();
builder.Services.AddSingleton(services => new ProfileResultCache(
    services.GetRequiredService<IConfiguration>().GetValue("Cache:MetricResultMaxEntries", 1000),
    TimeSpan.FromMinutes(services.GetRequiredService<IConfiguration>().GetValue(
        "Cache:MetricResultTtlMinutes", 120))));
builder.Services.AddScoped<AskEndpoints.AskRequestValidationFilter>();
builder.Services.AddHttpClient<IApimAiGatewayClient, ApimAiGatewayClient>(
        ApimAiGatewayClient.ConfigureHttpClient)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddHttpClient("github")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = IpAddressPolicy.ConnectAsync
    });
builder.Services.AddSingleton<GitHubRepositorySnapshotProvider>();
builder.Services.AddSingleton<IRepositorySnapshotProvider>(services =>
    new SnapshotCache(
        services.GetRequiredService<GitHubRepositorySnapshotProvider>(),
        new SnapshotCacheOptions
        {
            SnapshotMaxBytes = services.GetRequiredService<IConfiguration>().GetValue(
                "Cache:SnapshotMaxBytes", 536_870_912L)
        }));
builder.Services.AddSingleton(services => new MetricResultCache(
    new MetricResultCacheOptions
    {
        MetricResultTtlMinutes = services.GetRequiredService<IConfiguration>().GetValue(
            "Cache:MetricResultTtlMinutes", 120),
        MetricResultMaxEntries = services.GetRequiredService<IConfiguration>().GetValue(
            "Cache:MetricResultMaxEntries", 1000),
        MetricDeadline = TimeSpan.FromSeconds(services.GetRequiredService<IConfiguration>().GetValue(
            "Assessment:MetricTimeoutSeconds", 140))
    }));
builder.Services.AddSingleton<ISecretMasker, SecretMasker>();
builder.Services.AddSingleton<GlobMatcher>();
builder.Services.AddSingleton<IScannerRegistry>(services =>
    new ScannerRegistry(BuiltInScanners.Create(services.GetRequiredService<ISecretMasker>())));
builder.Services.AddSingleton<IScannerRunner>(services =>
    services.GetRequiredService<IScannerRegistry>());
builder.Services.AddSingleton<RecordFindingValidator>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, GetRepoManifestTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, ListFilesTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, SearchCodeTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, ReadFileTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, RunScannerTool>();
builder.Services.AddSingleton<IReadOnlyRepositoryTool, RecordFindingTool>();
builder.Services.AddSingleton<IToolDispatcher, ToolDispatcher>();
builder.Services.AddSingleton<IRetryDelayStrategy, RetryDelayStrategy>();
builder.Services.AddSingleton<IApimCredentialProvider>(services =>
{
    var options = services.GetRequiredService<IOptions<ApimOptions>>().Value;
    return options.Auth.Scheme switch
    {
        "SubscriptionKey" => new SubscriptionKeyCredentialProvider(
            services.GetRequiredService<IOptions<ApimOptions>>()),
        "None" or ApimOptions.OrganizationPlaceholder => new NoApimCredentialProvider(),
        _ => throw new InvalidOperationException(
            "Apim:Auth:Scheme is unsupported until the organization confirms its caller authentication scheme.")
    };
});

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
builder.Services.AddOptions<ApimOptions>()
    .BindConfiguration(ApimOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApimOptions>, ApimOptionsValidator>();
builder.Services.AddOptions<TelemetryOptions>()
    .BindConfiguration(TelemetryOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RepositoryOptions>, OrganizationPlaceholderValidator>();
builder.Services.AddSingleton<IValidateOptions<EntraIdOptions>, OrganizationPlaceholderValidator>();
builder.Services.AddAssessmentOpenTelemetry(builder.Configuration);
builder.Services.AddAskRateLimiter();

var app = builder.Build();

_ = app.Services.GetRequiredService<IInputGuard>();
_ = app.Services.GetRequiredService<PromptCatalog>();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.MapAskEndpoint();
app.MapHealthEndpoint();

app.Run();
return 0;

public partial class Program
{
}
