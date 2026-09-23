namespace Hackathon.Assessment.Api.Snapshot;

public interface IRepositorySnapshotProvider
{
    Task<RepositorySnapshot> GetAsync(
        Uri repositoryUrl,
        string gitRef,
        CancellationToken ct);
}
