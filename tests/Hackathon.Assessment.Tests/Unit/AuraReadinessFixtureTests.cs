using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Tests.GatewayLive.AuraReadiness;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit;

public sealed class AuraReadinessFixtureTests
{
    private static readonly string[] ExpectedCategories =
    [
        "toxicity",
        "injection",
        "rag",
        "unanswerable"
    ];

    [Fact]
    public void FixtureContainsAtLeastFifteenSyntheticScenariosPerMixedLanguageCategory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "aura-readiness.yaml");
        var fixture = AuraReadinessFixture.Load(path);
        var masker = new SecretMasker();

        Assert.True(fixture.Count >= 60);
        Assert.Equal(
            ExpectedCategories.Order(StringComparer.Ordinal),
            fixture.Select(scenario => scenario.Category)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            fixture.Count,
            fixture.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(fixture, scenario => scenario.Expect.SameScoreOnRepeat);

        foreach (var category in ExpectedCategories)
        {
            var scenarios = fixture.Where(scenario => scenario.Category == category).ToArray();
            Assert.True(scenarios.Length >= 15, $"{category} has fewer than 15 scenarios.");
            Assert.Contains(scenarios, scenario => scenario.Lang == "tr");
            Assert.Contains(scenarios, scenario => scenario.Lang == "en");
        }

        Assert.All(fixture, scenario =>
        {
            Assert.Contains(scenario.Lang, new[] { "tr", "en" });
            Assert.NotEmpty(scenario.Question);
            Assert.NotEmpty(scenario.Expect.AnswerTypes);
            Assert.Equal(scenario.Question, masker.Mask(scenario.Question));
        });
    }
}
