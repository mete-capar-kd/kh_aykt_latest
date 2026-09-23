using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Orchestration;
using Hackathon.Assessment.Api.Snapshot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Hackathon.Assessment.Tests.Integration;

public sealed class AskOrchestratorTests
{
    [Fact]
    public async Task FiveConcurrentRequestsForSameCommitEvaluateOnceAndKeepTenOrderedMetrics()
    {
        var calls = new ConcurrentDictionary<MetricId, int>();
        using var factory = CreateFactory("clean-dotnet", calls);
        using var client = factory.CreateAuthenticatedClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/api/ask", new
            {
                question = "uygulama sahibi kim?",
                metrics = new[] { "m01" }
            })));
        try
        {
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var body = json.RootElement;
                Assert.Equal("insufficient_evidence", body.GetProperty("answerType").GetString());
                Assert.StartsWith(
                    "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum:",
                    body.GetProperty("answer").GetString());
                var metrics = body.GetProperty("assessment").GetProperty("metrics")
                    .EnumerateArray().ToArray();
                Assert.Equal(10, metrics.Length);
                Assert.Equal(
                    Enumerable.Range(1, 10).Select(number => $"m{number:00}"),
                    metrics.Select(metric => metric.GetProperty("metricId").GetString()));
                Assert.Equal(10m, metrics[0].GetProperty("score").GetDecimal());
                Assert.Equal(
                    "İstekte seçilmedi",
                    metrics[1].GetProperty("notAssessableReason").GetString());
            }

            Assert.Equal(1, calls[MetricId.M01]);
            using var anotherQuestion = await client.PostAsJsonAsync("/api/ask", new
            {
                question = "Uygulama sahibi kim?",
                metrics = new[] { "m01" }
            });
            Assert.Equal(HttpStatusCode.OK, anotherQuestion.StatusCode);
            Assert.Equal(1, calls[MetricId.M01]);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task UntrustedReadmeTextDoesNotChangeDeterministicScore()
    {
        var scores = new List<decimal>();
        foreach (var fixture in new[] { "clean-dotnet", "injected-readme" })
        {
            using var factory = CreateFactory(fixture, new ConcurrentDictionary<MetricId, int>());
            using var client = factory.CreateAuthenticatedClient();
            using var response = await client.PostAsJsonAsync("/api/ask", new
            {
                question = "uygulama sahibi kim?",
                metrics = new[] { "m01" }
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            scores.Add(json.RootElement.GetProperty("assessment").GetProperty("overallScore")
                .GetDecimal());
        }

        Assert.Equal([10m, 10m], scores);
    }

    [Fact]
    public async Task FailedMetricDoesNotDiscardOtherNine()
    {
        var calls = new ConcurrentDictionary<MetricId, int>();
        using var factory = CreateFactory("violating-dotnet", calls, failMetric: MetricId.M10);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "uygulama sahibi kim?"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var metrics = json.RootElement.GetProperty("assessment").GetProperty("metrics")
            .EnumerateArray().ToArray();
        Assert.Equal(9, metrics.Count(metric => metric.GetProperty("score").ValueKind == JsonValueKind.Number));
        Assert.Equal(
            "Değerlendirilemedi",
            metrics[9].GetProperty("status").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            metrics[9].GetProperty("score").ValueKind);
        Assert.Equal(1, calls[MetricId.M10]);
    }

    [Fact]
    public async Task MissingFileAvoidsAllModelCallsButRetainsAssessment()
    {
        var calls = new ConcurrentDictionary<MetricId, int>();
        using var factory = CreateFactory("unknown-stack", calls);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "MissingService.cs dosyası ne yapıyor?"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("bulunamadı", json.RootElement.GetProperty("answer").GetString());
        Assert.Equal(10,
            json.RootElement.GetProperty("assessment").GetProperty("metrics").GetArrayLength());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task SynthesizedAnswerAndMarkdownUseOnlyVerifiedCommitPinnedEvidence()
    {
        using var factory = CreateFactory(
            "clean-dotnet", new ConcurrentDictionary<MetricId, int>(), synthesize: true);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "Is the repository compliant?",
            metrics = new[] { "m01" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("assessment", root.GetProperty("answerType").GetString());
        Assert.StartsWith("Partially, because ", root.GetProperty("answer").GetString());
        Assert.DoesNotContain("Foo.cs", root.GetProperty("answer").GetString());
        var evidence = Assert.Single(root.GetProperty("evidence").EnumerateArray());
        Assert.Equal(
            $"https://github.com/org/repo/blob/{new string('a', 40)}/README.md#L1-L1",
            evidence.GetProperty("url").GetString());
        var assessment = root.GetProperty("assessment");
        Assert.Equal(10, assessment.GetProperty("metrics").GetArrayLength());
        Assert.Equal(10m, assessment.GetProperty("overallScore").GetDecimal());
        Assert.Contains(
            $"[README.md:1](https://github.com/org/repo/blob/{new string('a', 40)}/README.md#L1)",
            assessment.GetProperty("reportMarkdown").GetString());
    }

    [Fact]
    public async Task SynthesisContentFilterReturnsRefusalWithoutAssessment()
    {
        using var factory = CreateFactory(
            "clean-dotnet", new ConcurrentDictionary<MetricId, int>(),
            filterSynthesis: true);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "Assess this repository.",
            metrics = new[] { "m01" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("refusal", root.GetProperty("answerType").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("assessment").ValueKind);
        Assert.Equal(0, root.GetProperty("evidence").GetArrayLength());
    }

    [Fact]
    public async Task GlobalDeadlineReturns504RatherThanPartialAssessment()
    {
        var provider = Substitute.For<IRepositorySnapshotProvider>();
        provider.GetAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return new RepositorySnapshot("org", "repo", new string('a', 40),
                    new Dictionary<string, SnapshotFile>());
            });
        using var factory = new AssessmentApiFactory(
            configureServices: services =>
            {
                services.RemoveAll<IRepositorySnapshotProvider>();
                services.AddSingleton(provider);
            },
            settings: new Dictionary<string, string?> { ["Assessment:GlobalTimeoutSeconds"] = "1" },
            useRealOrchestrator: true);
        var orchestrator = factory.Services.GetRequiredService<IAskOrchestrator>();
        await Assert.ThrowsAsync<AssessmentTimeoutException>(() => orchestrator.AskAsync(
            new AskContext("test-correlation", "Are there architecture problems?", false, "test"),
            new AskRequest
            {
                Question = "Are there architecture problems?",
                RepositoryUrl = "https://github.com/org/repo",
                Ref = "main"
            },
            CancellationToken.None));
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "Are there architecture problems?"
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(504, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AtMostTwoDistinctCommitAssessmentsRunAtOnce()
    {
        var initial = LoadFixture("clean-dotnet");
        var provider = Substitute.For<IRepositorySnapshotProvider>();
        provider.GetAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new RepositorySnapshot(
                "org",
                "repo",
                new string(call.ArgAt<string>(1)[0], 40),
                initial.Files)));
        var gateway = Substitute.For<IApimAiGatewayClient>();
        var release = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        gateway.ChatAsync(
                Arg.Any<ChatRequest>(),
                Arg.Any<ModelRole>(),
                Arg.Any<AiCallContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<ModelRole>() == ModelRole.Profiler)
                {
                    return Task.FromException<ChatResponse>(
                        new GatewayException("Synthetic profiler fallback."));
                }

                if (Interlocked.Increment(ref started) == 2)
                {
                    twoStarted.TrySetResult();
                }

                return release.Task;
            });
        using var factory = new AssessmentApiFactory(
            configureServices: services =>
            {
                services.RemoveAll<IRepositorySnapshotProvider>();
                services.AddSingleton(provider);
                services.RemoveAll<IApimAiGatewayClient>();
                services.AddSingleton(gateway);
            },
            useRealOrchestrator: true);
        using var client = factory.CreateAuthenticatedClient();
        var first = client.PostAsJsonAsync("/api/ask", new
        {
            question = "uygulama sahibi kim?",
            @ref = "first",
            metrics = new[] { "m01" }
        });
        var second = client.PostAsJsonAsync("/api/ask", new
        {
            question = "uygulama sahibi kim?",
            @ref = "second",
            metrics = new[] { "m01" }
        });
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var third = client.PostAsJsonAsync("/api/ask", new
            {
                question = "uygulama sahibi kim?",
                @ref = "third",
                metrics = new[] { "m01" }
            });
            await Task.Delay(100);
            Assert.Equal(2, Volatile.Read(ref started));
            release.TrySetResult(new ChatResponse(
                """{"subChecks":[],"rationale":"Kanıt yok","risk":"Kanıt yok"}""",
                [],
                "stop",
                null,
                "test",
                0,
                0,
                null));
            var responses = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            Assert.Equal(3, Volatile.Read(ref started));
        }
        finally
        {
            release.TrySetResult(new ChatResponse(
                """{"subChecks":[],"rationale":"Kanıt yok","risk":"Kanıt yok"}""",
                [],
                "stop",
                null,
                "test",
                0,
                0,
                null));
        }
    }

    private static AssessmentApiFactory CreateFactory(
        string fixture,
        ConcurrentDictionary<MetricId, int> calls,
        MetricId? failMetric = null,
        bool synthesize = false,
        bool filterSynthesis = false)
    {
        var snapshot = LoadFixture(fixture);
        var provider = Substitute.For<IRepositorySnapshotProvider>();
        provider.GetAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(snapshot));
        var gateway = Substitute.For<IApimAiGatewayClient>();
        gateway.ChatAsync(
                Arg.Any<ChatRequest>(),
                Arg.Any<ModelRole>(),
                Arg.Any<AiCallContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<ChatRequest>();
                var role = call.Arg<ModelRole>();
                var context = call.Arg<AiCallContext>();
                if (role == ModelRole.Profiler)
                {
                    throw new GatewayException("Synthetic profiler fallback.");
                }

                if (role == ModelRole.Synthesizer)
                {
                    if (filterSynthesis)
                    {
                        throw new ContentFilteredException(400, "test-strong", "content_filter");
                    }

                    Assert.True(synthesize);
                    return new ChatResponse(
                        JsonSerializer.Serialize(new
                        {
                            answer = "Partially, because README.md:1 was reviewed; Foo.cs:12 was not.",
                            executiveSummary = "Evidence limited to README.md:1.",
                            riskPriorities = new[]
                            {
                                new { metricId = "m01", findingId = (string?)null, reason = "Review." }
                            },
                            citedEvidence = new[]
                            {
                                new { file = "README.md", startLine = 1, endLine = 1, reason = "Reviewed." },
                                new { file = "Foo.cs", startLine = 12, endLine = 12, reason = "Invented." }
                            },
                            answerType = "assessment"
                        }),
                        [],
                        "stop",
                        null,
                        "test-strong",
                        0,
                        0,
                        null);
                }

                Assert.Equal(ModelRole.Evaluator, role);
                var metric = context.MetricId!.Value;
                if (request.Messages.Any(message => message.Role == "tool"))
                {
                    if (metric == failMetric)
                    {
                        throw new GatewayException("Synthetic metric failure.");
                    }

                    var id = $"m{(int)metric + 1:00}";
                    return new ChatResponse(
                        JsonSerializer.Serialize(new
                        {
                            rationale = "Evidence limited to README.md.",
                            risk = "Unconfirmed controls require additional review.",
                            subChecks = new[]
                            {
                                new
                                {
                                    id = $"{id}-sc01",
                                    status = "Karşılandı",
                                    reason = "README line 1 was reviewed.",
                                    evidenceRefs = new[] { "README.md#L1-L1" }
                                }
                            },
                            notAssessableReason = (string?)null
                        }),
                        [],
                        "stop",
                        null,
                        "test",
                        0,
                        0,
                        null);
                }

                calls.AddOrUpdate(metric, 1, (_, count) => count + 1);
                await Task.Delay(25, call.Arg<CancellationToken>());
                return new ChatResponse(
                    null,
                    [new ChatToolCall(
                        "read-1",
                        "read_file",
                        """{"path":"README.md","start_line":1,"end_line":1}""")],
                    "tool_calls",
                    null,
                    "test",
                    0,
                    0,
                    null);
            });

        return new AssessmentApiFactory(
            configureServices: services =>
            {
                services.RemoveAll<IRepositorySnapshotProvider>();
                services.AddSingleton(provider);
                services.RemoveAll<IApimAiGatewayClient>();
                services.AddSingleton(gateway);
            },
            useRealOrchestrator: true);
    }

    private static RepositorySnapshot LoadFixture(string fixture)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => new SnapshotFile(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    File.ReadAllText(path),
                    SnapshotFileRole.App),
                StringComparer.Ordinal);
        return new RepositorySnapshot("org", "repo", new string('a', 40), files);
    }
}
