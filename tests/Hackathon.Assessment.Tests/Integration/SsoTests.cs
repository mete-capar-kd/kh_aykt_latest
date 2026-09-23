using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Orchestration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace Hackathon.Assessment.Tests.Integration;

public sealed class SsoTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    [InlineData("malformed")]
    public async Task InvalidBearerTokensReturn401WithoutCallingDownstream(string tokenKind)
    {
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(downstream.Orchestrator);
        using var client = tokenKind == "missing"
            ? factory.CreateClient()
            : factory.CreateAuthenticatedClient(CreateInvalidToken(tokenKind));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            header => string.Equals(header.Scheme, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal)
                && header.Parameter is null);
        await AssertProblemDetailsAsync(response, HttpStatusCode.Unauthorized, "Unauthorized");
        downstream.AssertNotCalled();
    }

    [Fact]
    public async Task MissingRequiredRoleReturns403WithoutCallingDownstream()
    {
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(
            downstream.Orchestrator,
            settings: new Dictionary<string, string?>
            {
                ["EntraId:RequiredRoles:0"] = "Assessment.Run"
            });
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemDetailsAsync(response, HttpStatusCode.Forbidden, "Forbidden");
        downstream.AssertNotCalled();
    }

    [Fact]
    public async Task RequiredRoleAllowsMatchingApplicationToken()
    {
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(
            downstream.Orchestrator,
            settings: new Dictionary<string, string?>
            {
                ["EntraId:RequiredRoles:0"] = "Assessment.Run"
            });
        using var client = factory.CreateAuthenticatedClient(
            TestJwt.CreateToken(roles: ["Assessment.Run"]));

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, downstream.ApimHandler.CallCount);
    }

    [Fact]
    public async Task RequiredScopeAllowsMatchingDelegatedToken()
    {
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(
            downstream.Orchestrator,
            settings: new Dictionary<string, string?>
            {
                ["EntraId:RequiredScopes:0"] = "access_as_user"
            });
        using var client = factory.CreateAuthenticatedClient(
            TestJwt.CreateToken(scope: "access_as_user other"));

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, downstream.ApimHandler.CallCount);
    }

    [Fact]
    public async Task EmptyRoleAndScopeConfigurationAllowsAnyValidToken()
    {
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(downstream.Orchestrator);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, downstream.ApimHandler.CallCount);
    }

    [Fact]
    public async Task V1IssuerAndApiUriForGuidAudienceAreAccepted()
    {
        const string audienceId = "22222222-2222-2222-2222-222222222222";
        using var downstream = new DownstreamSpy();
        using var factory = new AssessmentApiFactory(
            downstream.Orchestrator,
            settings: new Dictionary<string, string?>
            {
                ["EntraId:Audience"] = audienceId
            });
        using var client = factory.CreateAuthenticatedClient(
            TestJwt.CreateToken(
                issuer: TestJwt.V1Issuer,
                audience: $"api://{audienceId}"));

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, downstream.ApimHandler.CallCount);
    }

    [Fact]
    public async Task HealthIsAnonymous()
    {
        using var factory = new AssessmentApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotLogTokenOrAuthorizationHeader()
    {
        using var logs = new CapturingLoggerProvider();
        var token = CreateInvalidToken("wrong-issuer");
        using var factory = new AssessmentApiFactory(
            configureLogging: logging => logging
                .AddProvider(logs)
                .SetMinimumLevel(LogLevel.Trace));
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.PostAsJsonAsync(
            "/api/ask",
            new { question = "valid question" });
        var loggedText = string.Join(Environment.NewLine, logs.Messages);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(token, loggedText, StringComparison.Ordinal);
        Assert.DoesNotContain($"Bearer {token}", loggedText, StringComparison.Ordinal);
        Assert.False(IdentityModelEventSource.ShowPII);
    }

    private static string CreateInvalidToken(string tokenKind) =>
        tokenKind switch
        {
            "wrong-issuer" => TestJwt.CreateToken(issuer: "https://issuer.invalid/v2.0"),
            "wrong-audience" => TestJwt.CreateToken(audience: "api://wrong-audience"),
            "expired" => TestJwt.CreateToken(
                notBefore: DateTime.UtcNow.AddMinutes(-20),
                expires: DateTime.UtcNow.AddMinutes(-15)),
            "wrong-signature" => CreateWronglySignedToken(),
            "malformed" => "abc",
            _ => throw new ArgumentOutOfRangeException(nameof(tokenKind))
        };

    private static string CreateWronglySignedToken()
    {
        using var rsa = RSA.Create(2048);
        return TestJwt.CreateToken(signingKey: new RsaSecurityKey(rsa)
        {
            KeyId = "wrong-test-key"
        });
    }

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle)
    {
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.StartsWith(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType,
            StringComparison.OrdinalIgnoreCase);
        using var problem = JsonDocument.Parse(rawBody);
        var root = problem.RootElement;

        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedTitle, root.GetProperty("title").GetString());
        Assert.True(root.TryGetProperty("correlationId", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));
        Assert.DoesNotContain("stackTrace", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", rawBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DownstreamSpy : IDisposable
    {
        private readonly HttpClient _apimClient;

        public DownstreamSpy()
        {
            ApimHandler = new CountingApimHandler();
            _apimClient = new HttpClient(ApimHandler);
            Orchestrator = Substitute.For<IAskOrchestrator>();
            Orchestrator.AskAsync(
                    Arg.Any<AskContext>(),
                    Arg.Any<AskRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    using var apimResponse = await _apimClient.GetAsync(
                        "https://apim.example.test/ask",
                        call.Arg<CancellationToken>());
                    apimResponse.EnsureSuccessStatusCode();
                    return new AskResponse(
                        "Answer",
                        AnswerType.InsufficientEvidence,
                        "test",
                        ImmutableArray<EvidenceReference>.Empty,
                        call.Arg<AskContext>().CorrelationId,
                        null);
                });
        }

        public IAskOrchestrator Orchestrator { get; }

        public CountingApimHandler ApimHandler { get; }

        public void AssertNotCalled()
        {
            _ = Orchestrator.DidNotReceive().AskAsync(
                Arg.Any<AskContext>(),
                Arg.Any<AskRequest>(),
                Arg.Any<CancellationToken>());
            Assert.Equal(0, ApimHandler.CallCount);
        }

        public void Dispose() => _apimClient.Dispose();
    }

    private sealed class CountingApimHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public string[] Messages => [.. _messages];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                messages.Enqueue(exception is null ? message : $"{message} {exception}");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
