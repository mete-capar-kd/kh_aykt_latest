using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Orchestration;

public interface IAskOrchestrator
{
    Task<AskResponse> AskAsync(
        AskContext context,
        AskRequest request,
        CancellationToken cancellationToken);
}
