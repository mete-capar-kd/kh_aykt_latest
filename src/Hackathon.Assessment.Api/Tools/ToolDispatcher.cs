using System.Text.Json;

namespace Hackathon.Assessment.Api.Tools;

public sealed class ToolDispatcher(IEnumerable<IReadOnlyRepositoryTool> tools) : IToolDispatcher
{
    private readonly IReadOnlyDictionary<string, IReadOnlyRepositoryTool> _tools =
        tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public Task<ToolResult> DispatchAsync(
        string toolName,
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            return Task.FromResult(ToolResult.Error("Unknown tool."));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(ToolResult.Error("Arguments must be a JSON object."));
        }

        return Task.FromResult(tool.Execute(arguments, context, cancellationToken));
    }
}
