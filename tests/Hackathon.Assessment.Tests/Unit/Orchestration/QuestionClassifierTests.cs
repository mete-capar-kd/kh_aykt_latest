using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Orchestration;

public sealed class QuestionClassifierTests
{
    [Theory]
    [InlineData("SSO var mı?", "tr", "yes_no")]
    [InlineData("uygulama sahibi kim?", "tr", "open")]
    [InlineData("Is SSO enabled?", "en", "yes_no")]
    [InlineData("Describe the architecture.", "en", "open")]
    public void ClassifiesLanguageAndQuestionType(
        string question, string language, string type)
    {
        Assert.Equal(language, QuestionClassifier.Language(question));
        Assert.Equal(type, QuestionClassifier.QuestionType(question, language));
    }

    [Fact]
    public void MissingFileSearchesSnapshotPathsWithoutInventingAFile()
    {
        var snapshot = new RepositorySnapshot(
            "org",
            "repo",
            new string('a', 40),
            new Dictionary<string, SnapshotFile>
            {
                ["src/Service.cs"] = new("src/Service.cs", "class Service {}", SnapshotFileRole.App),
                ["src/NotMissing.cs"] = new("src/NotMissing.cs", "class Other {}", SnapshotFileRole.App)
            });

        Assert.Null(QuestionClassifier.MissingFile("Service.cs nerede?", snapshot));
        Assert.Equal("Missing.cs", QuestionClassifier.MissingFile("Missing.cs nerede?", snapshot));
    }
}
