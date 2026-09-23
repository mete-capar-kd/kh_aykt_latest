using Hackathon.Assessment.Api.Contracts;

namespace Hackathon.Assessment.Api.Ai;

public interface IApimAiGatewayClient
{
    Task<ChatResponse> ChatAsync(
        ChatRequest request,
        ModelRole role,
        AiCallContext context,
        CancellationToken ct);
}
