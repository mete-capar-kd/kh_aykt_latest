using System.Text.Json.Serialization;
using Hackathon.Assessment.Api.Tools;
using Microsoft.AspNetCore.Mvc;

namespace Hackathon.Assessment.Api.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AskRequest))]
[JsonSerializable(typeof(AskResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(OpenAiToolDefinition[]))]
public partial class AppJsonContext : JsonSerializerContext
{
}
