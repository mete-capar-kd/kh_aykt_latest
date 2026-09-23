using Hackathon.Assessment.Api.Domain;
using NetArchTest.Rules;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Architecture;

public sealed class LayerDependencyTests
{
    private const string RootNamespace = "Hackathon.Assessment.Api.";

    [Fact]
    public void DomainDoesNotDependOnFrameworkOrSiblingNamespaces()
    {
        AssertNoDependency("Domain", "Microsoft.AspNetCore");
        foreach (var sibling in new[]
        {
            "Auth", "Caching", "Contracts", "Endpoints", "Agents", "Tools", "Scoring",
            "Reporting", "Snapshot", "Scanners", "Ai", "Telemetry", "Masking",
            "Safety", "Options", "Health", "Middleware", "Orchestration"
        })
        {
            AssertNoDependency("Domain", RootNamespace + sibling);
        }
    }

    [Fact]
    public void EndpointsDoNotDependOnImplementationLayers()
    {
        foreach (var forbidden in new[] { "Ai", "Agents", "Snapshot", "Scanners", "Tools" })
        {
            AssertNoDependency("Endpoints", RootNamespace + forbidden);
        }
    }

    [Fact]
    public void OrchestrationDoesNotDependOnHttpOrAiImplementations()
    {
        AssertNoDependency("Orchestration", "System.Net.Http");
        AssertNoDependency("Orchestration", RootNamespace + "Ai");
    }

    [Fact]
    public void HttpClientIsNotUsedOutsideApprovedLayers()
    {
        foreach (var forbidden in new[]
        {
            "Domain", "Contracts", "Auth", "Caching", "Endpoints", "Agents", "Tools",
            "Scoring", "Reporting", "Scanners", "Telemetry", "Masking", "Safety",
            "Options", "Middleware", "Orchestration"
        })
        {
            AssertNoDependency(forbidden, "System.Net.Http");
        }
    }

    [Fact]
    public void ApimGatewayNamespaceIsNotReferencedByNonAgentLayers()
    {
        foreach (var forbidden in new[]
        {
            "Domain", "Contracts", "Auth", "Caching", "Endpoints", "Tools", "Scoring",
            "Reporting", "Snapshot", "Scanners", "Telemetry", "Masking", "Safety",
            "Options", "Middleware", "Orchestration", "Health"
        })
        {
            AssertNoDependency(forbidden, RootNamespace + "Ai");
        }
    }

    private static void AssertNoDependency(string sourceNamespace, string forbiddenDependency)
    {
        var result = Types.InAssembly(typeof(MetricId).Assembly)
            .That()
            .ResideInNamespace(RootNamespace + sourceNamespace)
            .ShouldNot()
            .HaveDependencyOn(forbiddenDependency)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{RootNamespace}{sourceNamespace} must not depend on {forbiddenDependency}.");
    }
}
