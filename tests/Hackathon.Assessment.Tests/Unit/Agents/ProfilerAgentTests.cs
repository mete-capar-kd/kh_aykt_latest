using System.Collections.Immutable;
using System.Text.Json;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Snapshot;
using Hackathon.Assessment.Api.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Agents;

public sealed class ProfilerAgentTests
{
    [Fact]
    public async Task ValidProfilerJsonProducesProfileWithAllMetricDecisions()
    {
        var response = JsonSerializer.Serialize(new
        {
            stacks = new[] { ".NET" },
            layers = new[]
            {
                new { name = "API", paths = new[] { "src/app.cs" } }
            },
            entryPoints = new[] { "src/app.cs" },
            applicableMetrics = MetricNames.All.Select(id => new
            {
                metricId = $"m{(int)id + 1:00}",
                applicable = true,
                reason = "Present in the repository."
            })
        });
        var gateway = new ProfilerGateway(response);

        var profile = await Profiler(gateway).ProfileAsync(
            Snapshot(), new AiCallContext("cid", null, "profile"), CancellationToken.None);

        Assert.Equal([".NET"], profile.Stacks.ToArray());
        Assert.Equal("API", Assert.Single(profile.Layers).Name);
        Assert.Equal(10, profile.ApplicableMetrics.Length);
        Assert.All(profile.ApplicableMetrics, metric => Assert.True(metric.Applicable));
        Assert.Equal(ModelRole.Profiler, gateway.SeenRole);
        Assert.Equal(0, gateway.LastRequest!.Temperature);
        Assert.True(gateway.LastRequest.ResponseFormatJsonSchema.HasValue);
        Assert.True(gateway.LastRequest.Messages[1].Content.Length <= 60_000);
        Assert.Contains("manifestFiles", gateway.LastRequest.Messages[1].Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("""{"stacks":[".NET"],"layers":[],"entryPoints":[],"applicableMetrics":[]}""")]
    [InlineData("""{"stacks":[],"layers":[],"entryPoints":["missing.cs"],"applicableMetrics":[]}""")]
    public async Task InvalidProfileFallsBackToStackSignalsAndAllMetricsApplicable(string answer)
    {
        var profile = await Profiler(new ProfilerGateway(answer)).ProfileAsync(
            Snapshot(), new AiCallContext("cid", null, "profile"), CancellationToken.None);

        Assert.Contains(".NET", profile.Stacks);
        Assert.Empty(profile.Layers);
        Assert.Equal(10, profile.ApplicableMetrics.Length);
        Assert.All(profile.ApplicableMetrics, item =>
        {
            Assert.True(item.Applicable);
            Assert.Equal("profil üretilemedi", item.Reason);
        });
    }

    [Fact]
    public async Task GatewayErrorAndProfilerDeadlineUseSameDeterministicFallback()
    {
        var gateway = new ProfilerGateway(null,
            new GatewayException("unavailable"));
        var errorProfile = await Profiler(gateway).ProfileAsync(
            Snapshot(), new AiCallContext("cid", null, "profile"), CancellationToken.None);
        Assert.Equal(10, errorProfile.ApplicableMetrics.Length);

        var hanging = new ProfilerGateway(null, null, hang: true);
        var timeout = await Profiler(hanging, new AssessmentOptions
        {
            ProfilerTimeoutSeconds = 1
        }).ProfileAsync(Snapshot(), new AiCallContext("cid", null, "profile"), CancellationToken.None);
        Assert.Equal(10, timeout.ApplicableMetrics.Length);
        Assert.Equal([".NET"], timeout.Stacks.ToArray());
    }

    [Fact]
    public async Task ProfilerInputMasksSecretsAndBoundsManifestExcerpt()
    {
        var secret = string.Concat("synthetic", "-", "secret");
        var file = new SnapshotFile(
            "package.json",
            "api_key=" + secret + "\n" +
            string.Join('\n', Enumerable.Repeat("line with text", 240)),
            SnapshotFileRole.Config);
        var snapshot = new RepositorySnapshot(
            "org", "repo", new string('a', 40),
            new Dictionary<string, SnapshotFile>
            {
                ["src/app.cs"] = new("src/app.cs", "class App {}", SnapshotFileRole.App),
                ["package.json"] = file
            });
        var gateway = new ProfilerGateway("not JSON");

        _ = await Profiler(gateway).ProfileAsync(
            snapshot, new AiCallContext("cid", null, "profile"), CancellationToken.None);

        Assert.DoesNotContain(secret, gateway.LastRequest!.Messages[1].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("201: line with text", gateway.LastRequest.Messages[1].Content,
            StringComparison.Ordinal);
        Assert.True(gateway.LastRequest.Messages[1].Content.Length <= 60_000);
    }

    private static ProfilerAgent Profiler(
        IApimAiGatewayClient gateway,
        AssessmentOptions? options = null)
    {
        var masker = new SecretMasker();
        return new ProfilerAgent(
            gateway,
            new ToolDispatcher([new GetRepoManifestTool(masker)]),
            masker,
            new PromptCatalog(Path.Combine(AppContext.BaseDirectory, "prompts")),
            Options.Create(options ?? new AssessmentOptions()));
    }

    private static RepositorySnapshot Snapshot() =>
        new(
            "org", "repo", new string('a', 40),
            new Dictionary<string, SnapshotFile>(StringComparer.Ordinal)
            {
                ["src/app.cs"] = new("src/app.cs", "class App {}",
                    SnapshotFileRole.App),
                ["app.csproj"] = new("app.csproj", "<Project/>",
                    SnapshotFileRole.Config)
            });

    private sealed class ProfilerGateway(
        string? response,
        Exception? failure = null,
        bool hang = false) : IApimAiGatewayClient
    {
        public ModelRole? SeenRole { get; private set; }
        public ChatRequest? LastRequest { get; private set; }

        public async Task<ChatResponse> ChatAsync(
            ChatRequest request,
            ModelRole role,
            AiCallContext context,
            CancellationToken ct)
        {
            SeenRole = role;
            LastRequest = request;
            if (hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            if (failure is not null)
            {
                throw failure;
            }

            return new ChatResponse(
                response,
                ImmutableArray<ChatToolCall>.Empty,
                "stop",
                null,
                "mock",
                0,
                0,
                null);
        }
    }
}
