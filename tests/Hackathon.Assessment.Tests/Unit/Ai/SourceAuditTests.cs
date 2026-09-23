using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Ai;

public sealed class SourceAuditTests
{
    [Fact]
    public void ApplicationSourceHasNoDirectFoundryEndpointOrSdk()
    {
        var root = RepositoryRoot();
        var source = Directory.GetFiles(
                Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(
                Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories));
        var forbidden = new[]
        {
            string.Concat("openai.azure", ".com"),
            string.Concat("cognitiveservices", ".azure.com"),
            string.Concat("services.ai.azure", ".com"),
            string.Concat("Azure.AI.", "OpenAI"),
            string.Concat("Azure.AI.", "Inference")
        };

        foreach (var path in source)
        {
            var text = File.ReadAllText(path);
            Assert.All(forbidden, value =>
                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
