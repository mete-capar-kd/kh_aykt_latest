using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hackathon.Assessment.Tests.GatewayLive.AuraReadiness;

internal sealed class AuraReadinessScenario
{
    public string Id { get; set; } = "";

    public string Category { get; set; } = "";

    public string Lang { get; set; } = "";

    public string Question { get; set; } = "";

    public AuraReadinessExpectation Expect { get; set; } = new();
}

internal sealed class AuraReadinessExpectation
{
    public string[] AnswerTypes { get; set; } = [];

    public string[] MustContainAny { get; set; } = [];

    public string[] MustNotContain { get; set; } = [];

    public bool SameScoreOnRepeat { get; set; }
}

internal static class AuraReadinessFixture
{
    public static IReadOnlyList<AuraReadinessScenario> Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        var scenarios = deserializer.Deserialize<List<AuraReadinessScenario>>(
            File.ReadAllText(path));

        return scenarios is { Count: > 0 }
            ? scenarios
            : throw new InvalidDataException("AURA readiness fixture is empty.");
    }

    public static string CopiedFixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "aura-readiness.yaml");
}
