using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Safety;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Safety;

public sealed class RefusalBuilderTests
{
    [Theory]
    [InlineData("out_of_scope", "tr", "repository")]
    [InlineData("unsafe", "en", "repository")]
    [InlineData("leak_blocked", "other", "repository")]
    [InlineData("blocklist", "en", "repository")]
    [InlineData("content_filter", "tr", "repository")]
    public void BuildsStableRefusalContract(string reason, string language, string expected)
    {
        var catalog = new PromptCatalog(Path.Combine(AppContext.BaseDirectory, "prompts"));
        var builder = new RefusalBuilder(
            catalog,
            new SafetyMetrics(NullLogger<SafetyMetrics>.Instance));

        var response = builder.Build(reason, language, "correlation");

        Assert.Equal(AnswerType.Refusal, response.AnswerType);
        Assert.Contains(expected, response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(response.Evidence);
        Assert.Null(response.Assessment);
        Assert.Equal(catalog.PromptVersion, response.PromptVersion);
        Assert.Equal("correlation", response.CorrelationId);
    }
}
