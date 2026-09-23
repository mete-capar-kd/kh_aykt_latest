using System.Collections.Immutable;
using System.Text.Json;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Snapshot;

namespace Hackathon.Assessment.Api.Tools;

public sealed record ToolContext(
    RepositorySnapshot Snapshot,
    EvidenceLedger EvidenceLedger,
    MetricId MetricId);

public sealed record ToolResult(bool Succeeded, string Content)
{
    public static ToolResult Error(string reason) =>
        new(false, JsonSerializer.Serialize(new { error = reason }));

    public static ToolResult From(object value) =>
        new(true, JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
}

public interface IToolDispatcher
{
    Task<ToolResult> DispatchAsync(
        string toolName,
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken);
}

public interface IScannerRunner
{
    ScannerRunResult Run(MetricId metricId, RepositorySnapshot snapshot);
}

public sealed record ScannerRunResult(
    ImmutableArray<CandidateFinding> Candidates,
    bool Truncated,
    int Total);

public interface IReadOnlyRepositoryTool
{
    string Name { get; }
    ToolResult Execute(JsonElement arguments, ToolContext context, CancellationToken cancellationToken);
}

public sealed record OpenAiToolDefinition(string Type, OpenAiFunctionDefinition Function);
public sealed record OpenAiFunctionDefinition(string Name, string Description, JsonElement Parameters);

public static class OpenAiToolDefinitions
{
    public static ImmutableArray<OpenAiToolDefinition> All { get; } =
    [
        Create("get_repo_manifest", "Return a bounded manifest of the pinned snapshot.", """{"type":"object","properties":{},"additionalProperties":false}"""),
        Create("list_files", "List files matching a glob in the pinned snapshot.", """{"type":"object","properties":{"glob":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":200}},"required":["glob"],"additionalProperties":false}"""),
        Create("search_code", "Search the snapshot with a bounded regex or literal.", """{"type":"object","properties":{"pattern":{"type":"string"},"glob":{"type":"string"},"regex":{"type":"boolean"},"max_results":{"type":"integer","minimum":1,"maximum":200}},"required":["pattern"],"additionalProperties":false}"""),
        Create("read_file", "Read masked numbered lines from a snapshot file.", """{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer","minimum":1},"end_line":{"type":"integer","minimum":1}},"required":["path"],"additionalProperties":false}"""),
        Create("run_scanner", "Run deterministic candidate scanner for a metric.", """{"type":"object","properties":{"metric_id":{"type":"string","enum":["m01","m02","m03","m04","m05","m06","m07","m08","m09","m10"]}},"required":["metric_id"],"additionalProperties":false}"""),
        Create("record_finding", "Validate evidence and record a metric finding.", """{"type":"object","properties":{"finding":{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"severity":{"type":"string","enum":["critical","high","medium","low","info"]},"confidence":{"type":"string","enum":["kesin","potansiyel"]},"standardRef":{"type":"string"},"rationale":{"type":"string"},"impact":{"type":"string"},"file":{"type":"string"},"startLine":{"type":"integer"},"endLine":{"type":"integer"},"snippet":{"type":"string"},"recommendation":{"type":"string"}},"required":["title","severity","confidence","standardRef","file","startLine","endLine","snippet","recommendation"],"additionalProperties":false}},"required":["finding"],"additionalProperties":false}""")
    ];

    public static string Serialize() =>
        JsonSerializer.Serialize(All.ToArray(), AppJsonContext.Default.OpenAiToolDefinitionArray);

    private static OpenAiToolDefinition Create(string name, string description, string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        return new("function", new(name, description, document.RootElement.Clone()));
    }
}
