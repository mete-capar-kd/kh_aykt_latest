namespace Hackathon.Assessment.Api.Contracts;

public sealed record AskRequest
{
    public string? Question { get; init; }
    public string? RepositoryUrl { get; init; }
    public string? Ref { get; init; }
    public IReadOnlyList<string>? Metrics { get; init; }
}
