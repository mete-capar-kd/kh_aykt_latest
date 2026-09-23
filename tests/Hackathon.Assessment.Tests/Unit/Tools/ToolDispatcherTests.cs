using System.Text.Json;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Scanners;
using Hackathon.Assessment.Api.Tools;
using Hackathon.Assessment.Tests.Unit.Snapshot;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Tools;

public sealed class ToolDispatcherTests
{
    [Fact]
    public void SixSchemasSerializeViaTheSourceGeneratedContext()
    {
        var schemas = OpenAiToolDefinitions.All;
        Assert.Equal(6, schemas.Length);
        using var json = JsonDocument.Parse(OpenAiToolDefinitions.Serialize());
        Assert.Equal(6, json.RootElement.GetArrayLength());
        Assert.All(json.RootElement.EnumerateArray(), element =>
        {
            Assert.Equal("function", element.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object,
                element.GetProperty("function").GetProperty("parameters").ValueKind);
        });
    }

    [Fact]
    public async Task ManifestAndFileListExpose500And200Limits()
    {
        var snapshot = SnapshotTestData.Create(
            [.. Enumerable.Range(0, 501).Select(i => ($"src/f{i:000}.cs", "class Example {}"))]);
        var ctx = new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01);
        var dispatcher = CreateDispatcher();
        using var manifest = JsonDocument.Parse((await Dispatch(dispatcher,
            "get_repo_manifest", "{}", ctx)).Content);
        using var list = JsonDocument.Parse((await Dispatch(dispatcher,
            "list_files", """{"glob":"**/*.cs","limit":200}""", ctx)).Content);
        Assert.Equal(500, manifest.RootElement.GetProperty("paths").GetArrayLength());
        Assert.True(manifest.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(501, manifest.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(200, list.RootElement.GetProperty("files").GetArrayLength());
        Assert.True(list.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(501, list.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ReadFileCapsAt1500AndOnlyMarksReturnedLines()
    {
        var text = string.Join('\n', Enumerable.Range(1, 1501).Select(i => $"line {i}"));
        var ctx = new ToolContext(SnapshotTestData.Create(("file.txt", text)),
            new EvidenceLedger(), MetricId.M01);
        var result = await Dispatch(CreateDispatcher(), "read_file",
            """{"path":"file.txt"}""", ctx);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(result.Succeeded);
        Assert.Equal(1500, json.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal("1: line 1", json.RootElement.GetProperty("lines")[0].GetString());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(ctx.EvidenceLedger.HasSeen("file.txt", 1, 1500));
        Assert.False(ctx.EvidenceLedger.HasSeen("file.txt", 1501, 1501));
        var range = await Dispatch(CreateDispatcher(), "read_file",
            """{"path":"file.txt","start_line":1500,"end_line":1501}""", ctx);
        using var rangeJson = JsonDocument.Parse(range.Content);
        Assert.Equal(2, rangeJson.RootElement.GetProperty("lines").GetArrayLength());
        Assert.False(rangeJson.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SearchMasksAndOnlyCreditsReturnedMatches()
    {
        var snapshot = SnapshotTestData.Create(
            ("src/a.txt", "password=abcdefghijk\nneedle\nneedle"),
            ("src/b.txt", "needle"));
        var ctx = new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01);
        var result = await Dispatch(CreateDispatcher(), "search_code",
            """{"pattern":"needle","regex":false,"max_results":1}""", ctx);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(1, json.RootElement.GetProperty("results").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(ctx.EvidenceLedger.HasSeen("src/a.txt", 2, 2));
        Assert.False(ctx.EvidenceLedger.HasSeen("src/a.txt", 3, 3));
        var exposed = await Dispatch(CreateDispatcher(), "read_file",
            """{"path":"src/a.txt","start_line":1,"end_line":1}""", ctx);
        Assert.DoesNotContain("abcdefghijk", exposed.Content, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", exposed.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown_tool", "{}")]
    [InlineData("search_code", "{\"pattern\":\"(foo)\\\\1\"}")]
    [InlineData("search_code", "{\"pattern\":\"(?=foo)\"}")]
    [InlineData("read_file", "{\"path\":\"../escape\"}")]
    [InlineData("list_files", "{\"glob\":42}")]
    [InlineData("search_code", "{\"pattern\":\"foo\",\"regex\":\"yes\"}")]
    public async Task InvalidNamesOrArgumentsProduceExplicitToolErrors(string name, string arguments)
    {
        var ctx = new ToolContext(SnapshotTestData.Create(("a.txt", "foo")),
            new EvidenceLedger(), MetricId.M01);
        var result = await Dispatch(CreateDispatcher(), name, arguments, ctx);
        Assert.False(result.Succeeded);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ScannerReturnsOnlyCandidatesForRequestedMetric()
    {
        var ctx = new ToolContext(SnapshotTestData.Create(("a.txt", "foo")),
            new EvidenceLedger(), MetricId.M01);
        var result = await Dispatch(CreateDispatcher(), "run_scanner",
            """{"metric_id":"m01"}""", ctx);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(result.Succeeded);
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RunScannerToolReturnsOnlyRequestedMetricCandidates()
    {
        var snapshot = SnapshotTestData.Create(("Dockerfile", "FROM node\n"));
        var ctx = new ToolContext(snapshot, new EvidenceLedger(), MetricId.M01);
        var result = await Dispatch(CreateDispatcher(), "run_scanner",
            """{"metric_id":"m08"}""", ctx);
        using var json = JsonDocument.Parse(result.Content);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(json.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.All(json.RootElement.GetProperty("candidates").EnumerateArray(), candidate =>
            Assert.Contains("m08", candidate.GetProperty("metricIds").EnumerateArray()
                .Select(metric => metric.GetString())));
        Assert.True(json.RootElement.TryGetProperty("truncated", out _));
        Assert.True(json.RootElement.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task SearchTimeoutReturnsPartialResultsAndTruncation()
    {
        var masker = new SecretMasker();
        var globs = new GlobMatcher();
        var search = new SearchCodeTool(globs, masker, new AdvancingTimeProvider());
        var ctx = new ToolContext(SnapshotTestData.Create(
            ("file.txt", "needle\nneedle")),
            new EvidenceLedger(), MetricId.M01);
        using var args = JsonDocument.Parse("""{"pattern":"needle","regex":false}""");
        var result = search.Execute(args.RootElement, ctx, CancellationToken.None);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(result.Succeeded);
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.True(ctx.EvidenceLedger.HasSeen("file.txt", 1, 1));
        Assert.False(ctx.EvidenceLedger.HasSeen("file.txt", 2, 2));
    }

    [Fact]
    public async Task RecordFindingDispatchRequiresProvenance()
    {
        var ctx = new ToolContext(
            SnapshotTestData.Create(("src/app.cs", "dangerous call();")),
            new EvidenceLedger(), MetricId.M01);
        const string input = """
            {"finding":{"id":"f1","title":"Unsafe call","severity":"high",
            "confidence":"kesin","standardRef":"UseCase §6.1 — example",
            "file":"src/app.cs","startLine":1,"endLine":1,
            "snippet":"dangerous call();",
            "recommendation":"Add an explicit validation step before invoking the unsafe operation."}}
            """;
        var dispatcher = CreateDispatcher();
        var beforeReading = await Dispatch(dispatcher, "record_finding", input, ctx);
        using var rejected = JsonDocument.Parse(beforeReading.Content);
        Assert.False(rejected.RootElement.GetProperty("accepted").GetBoolean());
        await Dispatch(dispatcher, "read_file", """{"path":"src/app.cs"}""", ctx);
        var afterReading = await Dispatch(dispatcher, "record_finding", input, ctx);
        using var accepted = JsonDocument.Parse(afterReading.Content);
        Assert.True(accepted.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Single(ctx.EvidenceLedger.Findings);
    }

    private static IToolDispatcher CreateDispatcher()
    {
        var masker = new SecretMasker();
        var globs = new GlobMatcher();
        return new ToolDispatcher(
        [
            new GetRepoManifestTool(masker),
            new ListFilesTool(globs, masker),
            new SearchCodeTool(globs, masker, TimeProvider.System),
            new ReadFileTool(masker),
            new RunScannerTool(
                new ScannerRegistry(BuiltInScanners.Create(masker)),
                masker),
            new RecordFindingTool(new RecordFindingValidator(masker))
        ]);
    }

    private static async Task<ToolResult> Dispatch(
        IToolDispatcher dispatcher, string name, string arguments, ToolContext ctx)
    {
        using var json = JsonDocument.Parse(arguments);
        return await dispatcher.DispatchAsync(name, json.RootElement, ctx, CancellationToken.None);
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _tick = -1;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp()
        {
            var tick = Interlocked.Increment(ref _tick);
            return tick < 2 ? tick : 3;
        }
    }
}
