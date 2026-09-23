using System.Collections.Immutable;
using System.Text.Json;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Synthesizer;

public sealed class SynthesizerAgentTests
{
    [Fact]
    public async Task UsesStrongRoleSchemaAndVerifiedMetricDataWithoutRawContent()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "The assessment is grounded in src/App.cs:2.",
            "Verified assessment.",
            [Citation("src/App.cs", 2, 2)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "How is the repository assessed?",
            "en",
            "open",
            Report(),
            Context(),
            CancellationToken.None);

        Assert.Equal(AnswerType.Assessment, result.AnswerType);
        Assert.Equal(ModelRole.Synthesizer, gateway.Roles.Single());
        var request = gateway.Requests.Single();
        Assert.Equal(0, request.Temperature);
        Assert.True(request.ResponseFormatJsonSchema.HasValue);
        var schema = request.ResponseFormatJsonSchema.Value
            .GetProperty("json_schema").GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var riskItem = schema.GetProperty("properties").GetProperty("riskPriorities")
            .GetProperty("items");
        Assert.Contains(
            "findingId",
            riskItem.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("<user_question", request.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("<assessment_data>", request.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("\"metrics\"", request.Messages[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("raw repository source", request.Messages[1].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"snippet\"", request.Messages[1].Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovesInventedReferencesAndKeepsCommitPinnedVerifiedCitation()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "Risk is shown at Foo.cs:12; verified at src/App.cs#L2-L3.",
            "Foo.cs#L99-L100 is invented; src/App.cs:3 is verified.",
            [
                Citation("Foo.cs", 12, 12),
                Citation("src/App.cs", 2, 3)
            ])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Risk nedir?", "tr", "open", Report(), Context(), CancellationToken.None);

        Assert.DoesNotContain("Foo.cs", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Foo.cs", result.ExecutiveSummary, StringComparison.Ordinal);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("src/App.cs", evidence.File);
        Assert.Equal(
            $"https://github.com/example/repo/blob/{CommitSha}/src/App.cs#L2-L3",
            evidence.Url);
    }

    [Fact]
    public async Task BareFileNamesRemainReadableAndOversizedLineNumbersAreRemoved()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "README.md refers to Foo.cs:999999999999999999999; src/App.cs:2 is verified.",
            "See src/App.cs:2.",
            [Citation("src/App.cs", 2, 2)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Describe the verified evidence.", "en", "open", Report(), Context(),
            CancellationToken.None);

        Assert.Equal(AnswerType.Assessment, result.AnswerType);
        Assert.Contains("README.md", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Foo.cs", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task ReturnsAtMostFiveUniqueVerifiedEvidenceItems()
    {
        var citations = Enumerable.Range(1, 6)
            .Select(line => Citation("src/App.cs", line, line))
            .ToArray();
        var gateway = new ScriptedGateway(Response(Output(
            "Assessment is supported by src/App.cs:1.",
            "Verified evidence is available.",
            citations)));

        var result = await Agent(gateway).SynthesizeAsync(
            "Assess it", "en", "open", Report(1, 10), Context(), CancellationToken.None);

        Assert.Equal(5, result.Evidence.Length);
        Assert.Equal([1, 2, 3, 4, 5], result.Evidence.Select(item => item.StartLine).ToArray());
    }

    [Fact]
    public async Task NoValidCitationForcesStandardInsufficientEvidenceResponse()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "The owner is Ada.",
            "An owner was identified.",
            [Citation("OWNERS", 1, 1)],
            "repo_answer")));

        var result = await Agent(gateway).SynthesizeAsync(
            "Who owns this application?",
            "en",
            "open",
            Report(),
            Context(),
            CancellationToken.None);

        Assert.Equal(AnswerType.InsufficientEvidence, result.AnswerType);
        Assert.Equal(
            "I cannot answer this from the repository evidence: no verified evidence was found.",
            result.Answer);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task AllNotAssessableMetricsForceTurkishInsufficientEvidence()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "Uydurma cevap src/App.cs:2.",
            "Uydurma özet.",
            [Citation("src/App.cs", 2, 2)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Uygun mu?",
            "tr",
            "yes_no",
            Report(allNotAssessable: true),
            Context(),
            CancellationToken.None);

        Assert.Equal(AnswerType.InsufficientEvidence, result.AnswerType);
        Assert.Equal(
            "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: doğrulanmış kanıt bulunamadı.",
            result.Answer);
    }

    [Fact]
    public async Task TimeoutReturnsEnglishAssessmentFallback()
    {
        var gateway = new ScriptedGateway(async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        });

        var result = await Agent(
            gateway,
            new AssessmentOptions { SynthesizerTimeoutSeconds = 1 }).SynthesizeAsync(
                "Summarize",
                "en",
                "open",
                Report(),
                Context(),
                CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal(AnswerType.Assessment, result.AnswerType);
        Assert.Equal(
            "The summary could not be generated; details are in the assessment table.",
            result.Answer);
    }

    [Fact]
    public async Task GatewayFailureReturnsTurkishAssessmentFallback()
    {
        var gateway = new ScriptedGateway((_, _, _, _) =>
            Task.FromException<ChatResponse>(new GatewayException("unavailable")));

        var result = await Agent(gateway).SynthesizeAsync(
            "Özetle", "tr", "open", Report(), Context(), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal(AnswerType.Assessment, result.AnswerType);
        Assert.Equal(
            "Özet üretilemedi; ayrıntılar değerlendirme tablosundadır.",
            result.Answer);
    }

    [Fact]
    public async Task TwoInvalidJsonResponsesUseOneRepairThenFallback()
    {
        var gateway = new ScriptedGateway(
            Response("not-json"),
            Response("""{"answer":"still invalid"}"""),
            Response(Output(
                "Unexpected third response src/App.cs:2.",
                "Unexpected.",
                [Citation("src/App.cs", 2, 2)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Özetle", "tr", "open", Report(), Context(), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Contains("Önceki yanıt geçersizdi", gateway.Requests[1].Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Equal(
            "Özet üretilemedi; ayrıntılar değerlendirme tablosundadır.",
            result.Answer);
    }

    [Fact]
    public async Task MasksAnswerSummaryAndEvidenceReason()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "token=abcdefghijk is referenced by src/App.cs:2.",
            "Contact owner@example.com.",
            [Citation("src/App.cs", 2, 2, "api_key=abcdefghijk")])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Özetle", "tr", "open", Report(), Context(), CancellationToken.None);

        Assert.DoesNotContain("abcdefghijk", result.Answer, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", result.Answer, StringComparison.Ordinal);
        Assert.Equal("Contact ***MASKED***.", result.ExecutiveSummary);
        Assert.DoesNotContain("abcdefghijk", result.Evidence[0].Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task YesNoAnswerGetsRequiredEnglishDirectOpening()
    {
        var gateway = new ScriptedGateway(Response(Output(
            "Authentication has partial evidence at src/App.cs:2.",
            "The assessment is partial.",
            [Citation("src/App.cs", 2, 2)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Is authentication compliant?",
            "en",
            "yes_no",
            Report(),
            Context(),
            CancellationToken.None);

        Assert.StartsWith("Partially, because ", result.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentFilteredExceptionPropagatesToOrchestrator()
    {
        var failure = new ContentFilteredException(400, "strong", "content_filter");
        var gateway = new ScriptedGateway((_, _, _, _) => Task.FromException<ChatResponse>(failure));

        var thrown = await Assert.ThrowsAsync<ContentFilteredException>(() =>
            Agent(gateway).SynthesizeAsync(
                "Özetle", "tr", "open", Report(), Context(), CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task EscapesEveryEvidencePathSegmentInBlobUrl()
    {
        const string file = "src/folder name/a#b.cs";
        var gateway = new ScriptedGateway(Response(Output(
            "Evidence is verified.",
            "Evidence is verified.",
            [Citation(file, 1, 1)])));

        var result = await Agent(gateway).SynthesizeAsync(
            "Show evidence",
            "en",
            "open",
            Report(1, 1, file),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            $"https://github.com/example/repo/blob/{CommitSha}/src/folder%20name/a%23b.cs#L1-L1",
            Assert.Single(result.Evidence).Url);
    }

    private static SynthesizerAgent Agent(
        IApimAiGatewayClient gateway,
        AssessmentOptions? options = null) =>
        new(
            gateway,
            new SecretMasker(),
            new SafetyMetrics(NullLogger<SafetyMetrics>.Instance),
            Options.Create(options ?? new AssessmentOptions()),
            new PromptCatalog(Path.Combine(AppContext.BaseDirectory, "prompts")));

    private static AiCallContext Context() => new("correlation-id", null, "synthesis");

    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static AssessmentReport Report(
        int evidenceStart = 1,
        int evidenceEnd = 10,
        string evidenceFile = "src/App.cs",
        bool allNotAssessable = false)
    {
        var finding = new Finding(
            "m01-001",
            "Verified finding",
            Severity.Medium,
            Confidence.Kesin,
            "UseCase §6.1 — structure",
            "The finding is verified.",
            "The impact is bounded.",
            [new Evidence(
                evidenceFile,
                evidenceStart,
                evidenceEnd,
                "raw repository source",
                "hash",
                "old-url")],
            "Apply the specific remediation.");
        var metrics = MetricNames.All.Select((id, index) =>
        {
            var unavailable = allNotAssessable || index > 0;
            return new MetricResult(
                id,
                MetricNames.GetName(id),
                unavailable ? MetricStatus.Degerlendirilemedi : MetricStatus.KismenUyumlu,
                unavailable ? null : 9m,
                unavailable ? "Not assessed." : "Verified rationale.",
                unavailable ? "Unknown." : "Medium risk.",
                unavailable ? Coverage.None : Coverage.Complete,
                index == 0
                    ? [new SubCheckResult(
                        "m01-sc01",
                        SubCheckStatus.Ihlal,
                        "Verified sub-check.",
                        [$"{evidenceFile}#L{evidenceStart}-L{evidenceEnd}"])]
                    : ImmutableArray<SubCheckResult>.Empty,
                index == 0 ? [finding] : ImmutableArray<Finding>.Empty,
                unavailable ? "Not selected." : null,
                1,
                1,
                0);
        }).ToImmutableArray();
        return new AssessmentReport(
            "job",
            "https://github.com/example/repo",
            "main",
            CommitSha,
            metrics,
            allNotAssessable ? null : 9m,
            "report table",
            DateTimeOffset.UnixEpoch,
            new ModelInfo("router", "profiler", "evaluator", "synthesizer", "v1"));
    }

    private static object Citation(
        string file,
        int startLine,
        int endLine,
        string reason = "Supports the answer.") =>
        new { file, startLine, endLine, reason };

    private static string Output(
        string answer,
        string executiveSummary,
        IReadOnlyCollection<object> citedEvidence,
        string answerType = "assessment") =>
        JsonSerializer.Serialize(new
        {
            answer,
            executiveSummary,
            riskPriorities = new[]
            {
                new { metricId = "m01", findingId = "m01-001", reason = "Priority." }
            },
            citedEvidence,
            answerType
        });

    private static Func<ChatRequest, ModelRole, AiCallContext, CancellationToken, Task<ChatResponse>>
        Response(string content) =>
        (_, _, _, _) => Task.FromResult(new ChatResponse(
            content,
            ImmutableArray<ChatToolCall>.Empty,
            "stop",
            null,
            "strong",
            0,
            0,
            null));

    private sealed class ScriptedGateway : IApimAiGatewayClient
    {
        private readonly Queue<Func<
            ChatRequest,
            ModelRole,
            AiCallContext,
            CancellationToken,
            Task<ChatResponse>>> _responses;

        public ScriptedGateway(params Func<
            ChatRequest,
            ModelRole,
            AiCallContext,
            CancellationToken,
            Task<ChatResponse>>[] responses)
        {
            _responses = new Queue<Func<
                ChatRequest,
                ModelRole,
                AiCallContext,
                CancellationToken,
                Task<ChatResponse>>>(responses);
        }

        public List<ChatRequest> Requests { get; } = [];

        public List<ModelRole> Roles { get; } = [];

        public Task<ChatResponse> ChatAsync(
            ChatRequest request,
            ModelRole role,
            AiCallContext context,
            CancellationToken ct)
        {
            Requests.Add(request);
            Roles.Add(role);
            return _responses.Dequeue()(request, role, context, ct);
        }
    }
}
