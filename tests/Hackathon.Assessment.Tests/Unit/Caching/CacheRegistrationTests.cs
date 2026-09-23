using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Caching;

public sealed class CacheRegistrationTests
{
    [Fact]
    public void ApplicationStartsWithOneCatalogAndSeparateMetricAndSnapshotCaches()
    {
        using var factory = new AssessmentApiFactory();
        _ = factory.CreateClient();

        Assert.Equal(12, factory.Services.GetRequiredService<PromptCatalog>()
            .PromptVersion.Length);
        Assert.Same(
            factory.Services.GetRequiredService<MetricResultCache>(),
            factory.Services.GetRequiredService<MetricResultCache>());
        Assert.IsType<SnapshotCache>(
            factory.Services.GetRequiredService<IRepositorySnapshotProvider>());
        Assert.NotNull(factory.Services.GetRequiredService<MetricEvaluator>());
        Assert.NotNull(factory.Services.GetRequiredService<ProfilerAgent>());
    }
}
