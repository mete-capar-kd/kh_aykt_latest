using System.Collections.ObjectModel;

namespace Hackathon.Assessment.Api.Snapshot;

public enum SnapshotFileRole
{
    App,
    Test,
    Config,
    Docs,
    Workflow,
    Other
}

public sealed class RepositorySnapshot
{
    public RepositorySnapshot(
        string owner,
        string repo,
        string commitSha,
        IReadOnlyDictionary<string, SnapshotFile> files)
    {
        Owner = owner;
        Repo = repo;
        CommitSha = commitSha;
        var copy = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        foreach (var (path, file) in files)
        {
            copy.Add(path, file);
        }

        Files = new ReadOnlyDictionary<string, SnapshotFile>(copy);
    }

    public string Owner { get; }
    public string Repo { get; }
    public string CommitSha { get; }
    public IReadOnlyDictionary<string, SnapshotFile> Files { get; }
}

public sealed class SnapshotFile
{
    private readonly int[] _lineStarts;

    public SnapshotFile(string path, string content, SnapshotFileRole role)
    {
        Path = path;
        Content = content;
        Role = role;
        var starts = new List<int>();
        if (content.Length > 0)
        {
            starts.Add(0);
        }

        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
            {
                index++;
            }
            else if (content[index] is not ('\r' or '\n'))
            {
                continue;
            }

            if (index + 1 < content.Length)
            {
                starts.Add(index + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    public string Path { get; }
    public string Content { get; }
    public SnapshotFileRole Role { get; }
    public int[] LineStarts => (int[])_lineStarts.Clone();
    public int LineCount => _lineStarts.Length;

    public ReadOnlySpan<char> GetLine(int oneBased)
    {
        if (oneBased < 1 || oneBased > LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBased));
        }

        var start = _lineStarts[oneBased - 1];
        var end = oneBased == LineCount ? Content.Length : _lineStarts[oneBased];
        while (end > start && Content[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return Content.AsSpan(start, end - start);
    }
}
