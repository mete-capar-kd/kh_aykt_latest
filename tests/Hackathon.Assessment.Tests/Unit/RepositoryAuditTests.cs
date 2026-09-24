using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Masking;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit;

public sealed class RepositoryAuditTests
{
    private static readonly Regex DevelopmentMarker = new(
        @"\b(TODO|FIXME|HACK)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex ConsoleWrite = new(
        @"Console\s*\.\s*WriteLine\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex WriteAllPermissions = new(
        @"(?im)^\s*permissions\s*:\s*write-all\s*(?:#.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Fact]
    public void ApplicationSourceHasNoDevelopmentMarkersDebugWritesProductionUrlsOrUnmaskedSecrets()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var masker = new SecretMasker();
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".json");

        foreach (var path in sourceFiles)
        {
            var content = File.ReadAllText(path);
            Assert.False(DevelopmentMarker.IsMatch(content), $"{RelativePath(path)} has a development marker.");
            Assert.False(ConsoleWrite.IsMatch(content), $"{RelativePath(path)} writes to the console.");
            Assert.DoesNotContain("azurewebsites.net", content, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(content, masker.Mask(content));
        }
    }

    [Fact]
    public void WorkflowsHaveNoPrivilegedPullRequestTargetOrWriteAllPermissions()
    {
        var workflowRoot = Path.Combine(RepositoryRoot(), ".github", "workflows");
        var workflows = Directory.EnumerateFiles(workflowRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .ToArray();

        foreach (var path in workflows)
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotContain(
                "pull_request_target",
                content,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(WriteAllPermissions.IsMatch(content), $"{RelativePath(path)} grants write-all.");
        }
    }

    [Fact]
    public void AtMostOneCodeQlWorkflowIsConfigured()
    {
        var workflowRoot = Path.Combine(RepositoryRoot(), ".github", "workflows");
        var codeQlWorkflows = Directory.EnumerateFiles(workflowRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .Where(path =>
            {
                var nameMatches = Path.GetFileNameWithoutExtension(path)
                    .Contains("codeql", StringComparison.OrdinalIgnoreCase);
                var contentMatches = File.ReadAllText(path)
                    .Contains("codeql-action", StringComparison.OrdinalIgnoreCase);
                return nameMatches || contentMatches;
            })
            .ToArray();

        Assert.True(codeQlWorkflows.Length <= 1, "More than one CodeQL workflow is configured.");
    }

    private static string RelativePath(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path);

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
