using System.Collections.Immutable;
using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Contracts;

public sealed record AskResponse(
    string Answer,
    AnswerType AnswerType,
    string PromptVersion,
    ImmutableArray<EvidenceReference> Evidence,
    string CorrelationId,
    AssessmentReport? Assessment);
