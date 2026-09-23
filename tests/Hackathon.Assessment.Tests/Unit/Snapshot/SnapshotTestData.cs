using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Tests.Unit.Snapshot;

internal static class SnapshotTestData
{
    public const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    public static RepositorySnapshot Create(params (string Path, string Content)[] files) =>
        new(
            "org",
            "repo",
            CommitSha,
            files.ToDictionary(
                file => file.Path,
                file => new SnapshotFile(file.Path, file.Content, SnapshotFileRole.App),
                StringComparer.Ordinal));
}
