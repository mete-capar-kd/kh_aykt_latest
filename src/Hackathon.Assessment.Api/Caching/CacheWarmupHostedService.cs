using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Orchestration;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Caching;

public sealed partial class CacheWarmupHostedService(
    IServiceProvider services,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<RepositoryOptions> repositoryOptions,
    ILogger<CacheWarmupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")
            || !configuration.GetValue("Cache:WarmupOnStartup", true))
        {
            return;
        }

        var options = repositoryOptions.Value;
        if (string.IsNullOrWhiteSpace(options.DefaultUrl)
            || options.DefaultUrl.Contains("<ORGANİZASYONDAN-ALINACAK>", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            using var scope = services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AskOrchestrator>();
            await orchestrator.WarmupAsync(
                options.DefaultUrl,
                options.DefaultRef,
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            WarmupFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Repository cache warmup failed. ErrorType={ErrorType}")]
    private static partial void WarmupFailed(ILogger logger, string errorType);
}
