using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hackathon.Assessment.Api.Auth;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Orchestration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;
using Xunit;

namespace Hackathon.Assessment.Tests.Integration;

public sealed class AskEndpointsTests
{
    [Fact]
    public async Task ValidRequestReturnsContractFakeWithOrderedMetricsAndCorrelationId()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "assessment-123");

        using var response = await client.PostAsJsonAsync("/api/ask", new
        {
            question = "Bu repository SSO gereksinimlerini karşılıyor mu?"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("assessment-123", response.Headers.GetValues("X-Correlation-Id").Single());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("insufficient_evidence", root.GetProperty("answerType").GetString());
        Assert.Equal("stub", root.GetProperty("promptVersion").GetString());
        Assert.Equal("assessment-123", root.GetProperty("correlationId").GetString());
        Assert.Equal(
            "https://github.com/org/repo",
            root.GetProperty("assessment").GetProperty("repositoryUrl").GetString());
        Assert.Equal("main", root.GetProperty("assessment").GetProperty("ref").GetString());
        Assert.Equal(
            ["m01", "m02", "m03", "m04", "m05", "m06", "m07", "m08", "m09", "m10"],
            root.GetProperty("assessment").GetProperty("metrics")
                .EnumerateArray()
                .Select(metric => metric.GetProperty("metricId").GetString()));
    }

    [Theory]
    [InlineData(2, HttpStatusCode.BadRequest)]
    [InlineData(3, HttpStatusCode.OK)]
    [InlineData(2000, HttpStatusCode.OK)]
    [InlineData(2001, HttpStatusCode.BadRequest)]
    public async Task QuestionLengthBoundariesAreEnforced(int length, HttpStatusCode expectedStatus)
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await PostJsonAsync(
            client,
            JsonSerializer.Serialize(new { question = new string('q', length) }));

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task QuestionIsTrimmedAndInputGuardContextReachesOrchestrator()
    {
        AskContext? receivedContext = null;
        var orchestrator = CreateOrchestrator(context =>
        {
            receivedContext = context;
            return EmptyResponse(context.CorrelationId);
        });
        using var factory = new AssessmentApiFactory(orchestrator);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await PostJsonAsync(
            client,
            """{"question":"  SSO?  "}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedContext);
        Assert.Equal("SSO?", receivedContext.NormalizedQuestion);
        Assert.False(receivedContext.SuspectedInjection);
        Assert.False(string.IsNullOrWhiteSpace(receivedContext.CallerId));
    }

    [Fact]
    public async Task ValidExplicitMetricsAndMaximumLengthRefAreAccepted()
    {
        AskRequest? receivedRequest = null;
        var orchestrator = Substitute.For<IAskOrchestrator>();
        orchestrator.AskAsync(
                Arg.Any<AskContext>(),
                Arg.Any<AskRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedRequest = call.Arg<AskRequest>();
                return Task.FromResult(EmptyResponse(call.Arg<AskContext>().CorrelationId));
            });
        using var factory = new AssessmentApiFactory(orchestrator);
        using var client = factory.CreateAuthenticatedClient();
        var body = JsonSerializer.Serialize(new
        {
            question = "valid question",
            repositoryUrl = "https://github.com/org/repo.git",
            @ref = new string('r', 200),
            metrics = new[] { "m01", "m10" }
        });

        using var response = await PostJsonAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedRequest);
        Assert.Equal("https://github.com/org/repo.git", receivedRequest.RepositoryUrl);
        Assert.Equal(new string('r', 200), receivedRequest.Ref);
        Assert.Equal(["m01", "m10"], receivedRequest.Metrics);
    }

    [Fact]
    public async Task InvalidCorrelationIdIsReplacedWithGuidN()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "contains spaces");

        using var response = await PostJsonAsync(client, """{"question":"valid question"}""");
        var correlationId = response.Headers.GetValues("X-Correlation-Id").Single();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(Guid.TryParseExact(correlationId, "N", out _));
        Assert.Equal(correlationId, json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ValidEnumsSerializeAsExactUnescapedContractStrings()
    {
        var orchestrator = CreateOrchestrator(context =>
        {
            var metrics = MetricNames.All
                .Select((metricId, index) => new MetricResult(
                    metricId,
                    MetricNames.GetName(metricId),
                    index == 0 ? MetricStatus.KismenUyumlu : MetricStatus.Degerlendirilemedi,
                    index == 0 ? 7.0m : null,
                    "Stub rationale",
                    "Stub risk",
                    Coverage.None,
                    ImmutableArray<SubCheckResult>.Empty,
                    index == 0
                        ?
                        [
                            new Finding(
                                "m01-001",
                                "Example",
                                Severity.Critical,
                                Confidence.Potansiyel,
                                "UseCase §6.1 — example",
                                "Example rationale",
                                "Example impact",
                                [new Evidence(
                                    "README.md",
                                    1,
                                    1,
                                    "***MASKED***",
                                    "hash",
                                    "https://github.com/org/repo/blob/commit/README.md#L1")],
                                "Use an actionable and specific recommendation.")
                        ]
                        : ImmutableArray<Finding>.Empty,
                    "Not assessed by stub.",
                    0,
                    0,
                    0))
                .ToImmutableArray();
            var assessment = new AssessmentReport(
                "job",
                "https://github.com/org/repo",
                "main",
                "commit",
                metrics,
                null,
                "",
                DateTimeOffset.UnixEpoch,
                new ModelInfo("stub", "stub", "stub", "stub", "stub"));
            return new AskResponse(
                "Answer",
                AnswerType.Assessment,
                "stub",
                ImmutableArray<EvidenceReference>.Empty,
                context.CorrelationId,
                assessment);
        });
        using var factory = new AssessmentApiFactory(orchestrator);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await PostJsonAsync(client, """{"question":"valid question"}""");
        var rawJson = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"Kısmen Uyumlu\"", rawJson, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"critical\"", rawJson, StringComparison.Ordinal);
        Assert.Contains("\"confidence\":\"potansiyel\"", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0131", rawJson, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(rawJson);
        Assert.Equal(
            ["m01", "m02", "m03", "m04", "m05", "m06", "m07", "m08", "m09", "m10"],
            json.RootElement.GetProperty("assessment").GetProperty("metrics")
                .EnumerateArray()
                .Select(metric => metric.GetProperty("metricId").GetString()));
    }

    [Fact]
    public void HandMaintainedOpenApiListsOnlyPublicRoutes()
    {
        var root = RepositoryRoot();
        using var openApi = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "openapi.json")));
        var paths = openApi.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["/api/ask", "/health"], paths);
    }

    [Fact]
    public async Task InvalidRepositoryUrlsReturnValidationProblem()
    {
        string[] invalidUrls =
        [
            "http://github.com/org/repo",
            "https://untrusted.example/org/repo",
            "https://user@github.com/org/repo",
            "https://@github.com/org/repo",
            "https://github.com/org/repo?tab=readme",
            "https://github.com/org/repo?",
            "https://github.com/org/repo#readme",
            "https://github.com/org/repo#",
            "https://github.com/org",
            "https://github.com//org/repo",
            "https://github.com/org/repo/extra",
            "https://github.com/org_/repo",
            "https://github.com/org/repo with space",
            " https://github.com/org/repo ",
            $"https://github.com/{new string('o', 40)}/repo",
            $"https://github.com/org/{new string('r', 101)}"
        ];
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        foreach (var url in invalidUrls)
        {
            using var response = await PostJsonAsync(
                client,
                JsonSerializer.Serialize(new
                {
                    question = "valid question",
                    repositoryUrl = url
                }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("repositoryUrl", out _));
        }
    }

    [Fact]
    public async Task ValidRepositoryUrlAllowsGitSuffixOrTrailingSlash()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var urls = new[]
        {
            "https://github.com/org/repo.git",
            "https://github.com/org/repo/",
            $"https://github.com/{new string('o', 39)}/{new string('r', 100)}"
        };
        foreach (var url in urls)
        {
            using var response = await PostJsonAsync(
                client,
                JsonSerializer.Serialize(new { question = "valid question", repositoryUrl = url }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task InvalidReferencesAndMetricSelectionsReturnValidationProblem()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var invalidRequests = new[]
        {
            JsonSerializer.Serialize(new { question = "valid question", @ref = ".." }),
            JsonSerializer.Serialize(new { question = "valid question", @ref = "refs/../main" }),
            JsonSerializer.Serialize(new { question = "valid question", @ref = "bad ref" }),
            JsonSerializer.Serialize(new { question = "valid question", @ref = new string('r', 201) }),
            """{"question":"valid question","metrics":[]}""",
            """{"question":"valid question","metrics":["m11"]}""",
            """{"question":"valid question","metrics":["m01","m01"]}"""
        };

        foreach (var body in invalidRequests)
        {
            using var response = await PostJsonAsync(client, body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.TryGetProperty("errors", out _));
        }
    }

    [Fact]
    public async Task EmptyAndMalformedJsonReturn400ValidationProblem()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var missingQuestion = await PostJsonAsync(client, "{}");
        using var malformedJson = await PostJsonAsync(client, "{");

        Assert.Equal(HttpStatusCode.BadRequest, missingQuestion.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformedJson.StatusCode);
        using var missingProblem = JsonDocument.Parse(
            await missingQuestion.Content.ReadAsStringAsync());
        using var malformedProblem = JsonDocument.Parse(
            await malformedJson.Content.ReadAsStringAsync());
        Assert.True(missingProblem.RootElement.GetProperty("errors").TryGetProperty("question", out _));
        Assert.True(malformedProblem.RootElement.GetProperty("errors").TryGetProperty("body", out _));
    }

    [Fact]
    public async Task OversizedRequestReturns413ProblemDetails()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var body = JsonSerializer.Serialize(new { question = new string('q', 33 * 1024) });

        using var response = await PostJsonAsync(client, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(413, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("correlationId", out _));
    }

    [Theory]
    [InlineData("repository", 422)]
    [InlineData("snapshot", 422)]
    [InlineData("gateway", 502)]
    [InlineData("timeout", 504)]
    [InlineData("unexpected", 500)]
    public async Task TypedAndUnexpectedExceptionsMapToSafeProblemDetails(
        string exceptionKind,
        int expectedStatus)
    {
        Exception exception = exceptionKind switch
        {
            "repository" => new RepositoryAccessException("DO_NOT_LEAK repository detail"),
            "snapshot" => new SnapshotLimitExceededException("DO_NOT_LEAK snapshot detail"),
            "gateway" => new GatewayException("DO_NOT_LEAK gateway detail"),
            "timeout" => new AssessmentTimeoutException("DO_NOT_LEAK timeout detail"),
            _ => new InvalidOperationException("DO_NOT_LEAK unexpected detail")
        };
        var orchestrator = Substitute.For<IAskOrchestrator>();
        orchestrator.AskAsync(
                Arg.Any<AskContext>(),
                Arg.Any<AskRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AskResponse>(exception));
        using var factory = new AssessmentApiFactory(orchestrator);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await PostJsonAsync(client, """{"question":"valid question"}""");
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.DoesNotContain("DO_NOT_LEAK", rawBody, StringComparison.Ordinal);
        using var problem = JsonDocument.Parse(rawBody);
        var root = problem.RootElement;
        Assert.Equal(expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal("/api/ask", root.GetProperty("instance").GetString());
        Assert.True(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty("title", out _));
        Assert.True(root.TryGetProperty("correlationId", out _));
    }

    private static IAskOrchestrator CreateOrchestrator(Func<AskContext, AskResponse> responseFactory)
    {
        var orchestrator = Substitute.For<IAskOrchestrator>();
        orchestrator.AskAsync(
                Arg.Any<AskContext>(),
                Arg.Any<AskRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(responseFactory(call.Arg<AskContext>())));
        return orchestrator;
    }

    private static AskResponse EmptyResponse(string correlationId) =>
        new(
            "Answer",
            AnswerType.InsufficientEvidence,
            "test",
            ImmutableArray<EvidenceReference>.Empty,
            correlationId,
            null);

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string json) =>
        client.PostAsync(
            "/api/ask",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}

internal sealed class AssessmentApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?>? _settings;
    private readonly Action<IServiceCollection> _configureServices;
    private readonly Action<ILoggingBuilder>? _configureLogging;

    public AssessmentApiFactory(
        IAskOrchestrator? orchestrator = null,
        string environment = "Testing",
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null,
        bool useRealOrchestrator = false)
    {
        _environment = environment;
        _settings = settings;
        _configureLogging = configureLogging;
        _configureServices = services =>
        {
            if (orchestrator is not null || !useRealOrchestrator)
            {
                services.RemoveAll<IAskOrchestrator>();
                services.AddSingleton(orchestrator ?? CreateContractFake());
            }

            configureServices?.Invoke(services);
        };
    }

    private static IReadOnlyDictionary<string, string?> DefaultTestSettings { get; } =
        new Dictionary<string, string?>
        {
            ["Repository:DefaultUrl"] = "https://github.com/org/repo",
            ["EntraId:Instance"] = TestJwt.Instance,
            ["EntraId:TenantId"] = TestJwt.TenantId,
            ["EntraId:Audience"] = TestJwt.Audience
        };

    public static IReadOnlyDictionary<string, string?> ProductionSettings { get; } =
        new Dictionary<string, string?>
        {
            ["Repository:DefaultUrl"] = "https://github.com/org/repo",
            ["Apim:BaseUrl"] = "https://apim.example.test",
            ["Apim:RouteStyle"] = "AzureDeployments",
            ["Apim:ApiVersion"] = "2025-01-01",
            ["Apim:CacheStatusHeader"] = "",
            ["Apim:Auth:Scheme"] = "SubscriptionKey",
            ["Apim:Auth:HeaderName"] = "X-Test-Key",
            ["Apim:Auth:Key"] = string.Concat("synthetic", "-", "test", "-", "key"),
            ["Apim:Auth:Scope"] = "api://assessment.example.test/.default",
            ["Apim:Deployments:Cheap"] = "test-cheap",
            ["Apim:Deployments:Strong"] = "test-strong",
            ["EntraId:Instance"] = TestJwt.Instance,
            ["EntraId:TenantId"] = TestJwt.TenantId,
            ["EntraId:Audience"] = TestJwt.Audience,
            ["Telemetry:Team"] = "assessment-test-team"
        };

    public HttpClient CreateAuthenticatedClient(string? token = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            token ?? TestJwt.CreateToken());
        return client;
    }

    private static IAskOrchestrator CreateContractFake()
    {
        var fake = Substitute.For<IAskOrchestrator>();
        fake.AskAsync(
                Arg.Any<AskContext>(),
                Arg.Any<AskRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var context = call.Arg<AskContext>();
                var request = call.Arg<AskRequest>();
                var metrics = MetricNames.All.Select(id => new MetricResult(
                    id,
                    MetricNames.GetName(id),
                    MetricStatus.Degerlendirilemedi,
                    null,
                    "Değerlendirme motoru henüz etkin değil.",
                    "Değerlendirme motoru henüz etkin değil.",
                    Coverage.None,
                    [],
                    [],
                    "Test contract fake.",
                    0,
                    0,
                    0)).ToImmutableArray();
                return Task.FromResult(new AskResponse(
                    "Değerlendirme motoru henüz etkin değil.",
                    AnswerType.InsufficientEvidence,
                    "stub",
                    [],
                    context.CorrelationId,
                    new AssessmentReport(
                        "job",
                        request.RepositoryUrl!,
                        request.Ref!,
                        "test-commit",
                        metrics,
                        null,
                        "",
                        DateTimeOffset.UnixEpoch,
                        new ModelInfo(null, "test", "test", "test", "stub"))));
            });
        return fake;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(DefaultTestSettings);
            if (_settings is not null)
            {
                configuration.AddInMemoryCollection(_settings);
            }
        });
        builder.ConfigureLogging(logging => _configureLogging?.Invoke(logging));
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    var testConfiguration = new OpenIdConnectConfiguration
                    {
                        Issuer = TestJwt.V2Issuer
                    };
                    testConfiguration.SigningKeys.Add(TestJwt.SigningKey);
                    options.Configuration = testConfiguration;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(testConfiguration);
                });
            _configureServices(services);
        });
    }
}
