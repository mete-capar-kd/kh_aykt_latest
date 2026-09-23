using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Safety;

public sealed class CacheWarmupTests
{
    [Fact]
    public async Task TestingEnvironmentDoesNotStartWarmup()
    {
        var services = Substitute.For<IServiceProvider>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Testing");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:WarmupOnStartup"] = "true"
            })
            .Build();
        var service = new CacheWarmupHostedService(
            services,
            configuration,
            environment,
            Options.Create(new RepositoryOptions
            {
                DefaultUrl = "https://github.com/org/repo",
                DefaultRef = "main"
            }),
            NullLogger<CacheWarmupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        services.DidNotReceive().GetService(typeof(IServiceScopeFactory));
    }
}
