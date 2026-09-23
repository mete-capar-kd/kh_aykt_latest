using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hackathon.Assessment.Api.Ai;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Masking;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Telemetry;
using Hackathon.Assessment.Api.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Ai;

public sealed class ApimAiGatewayClientTests
{
    [Fact]
    public async Task Retries429TwiceHonorsRetryAfterAndReturnsUsage()
    {
        var handler = new ScriptedHandler((attempt, _, _) => Task.FromResult(
            attempt < 3
                ? Response(HttpStatusCode.TooManyRequests, """{"error":{"code":"busy"}}""",
                    retryAfter: TimeSpan.FromSeconds(2))
                : Response(HttpStatusCode.OK, SuccessBody())));
        var delays = new RecordingRetryDelayStrategy();
        var client = CreateClient(handler, retryDelayStrategy: delays);

        var response = await client.ChatAsync(Request(), ModelRole.Profiler, Context(), CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, delays.RequestedDelays.Count);
        Assert.All(delays.RequestedDelays, delay => Assert.Equal(TimeSpan.FromSeconds(2), delay));
        Assert.Equal(2, response.RetryCount);
        Assert.Equal(11, response.Usage?.InputTokens);
        Assert.Equal(4, response.Usage?.OutputTokens);
        Assert.Equal(2, response.Usage?.CachedTokens);
    }

    [Fact]
    public async Task Retries503ThreeTimesThenThrowsSafeGatewayExceptionWithTwoRetries()
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.ServiceUnavailable, "DO_NOT_LEAK response body")));
        var options = CreateApimOptions();
        options.MaxAttempts = 3;
        var client = CreateClient(handler, apimOptions: options);

        var exception = await Assert.ThrowsAsync<GatewayException>(() =>
            client.ChatAsync(Request(), ModelRole.Evaluator, Context(), CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, exception.RetryCount);
        Assert.Equal(503, exception.StatusCode);
        Assert.DoesNotContain("DO_NOT_LEAK", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Retries408And5xxStatuses(HttpStatusCode retryableStatus)
    {
        var handler = new ScriptedHandler((attempt, _, _) =>
            Task.FromResult(attempt == 1
                ? Response(retryableStatus, "{}")
                : Response(HttpStatusCode.OK, SuccessBody())));
        var result = await CreateClient(handler).ChatAsync(
            Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, result.RetryCount);
    }

    [Fact]
    public async Task RetriesHttpRequestException()
    {
        var handler = new ScriptedHandler((attempt, _, _) =>
            attempt == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("DO_NOT_LEAK"))
                : Task.FromResult(Response(HttpStatusCode.OK, SuccessBody())));
        var client = CreateClient(handler);

        var response = await client.ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, response.RetryCount);
    }

    [Fact]
    public void ProductionRetryDelayUsesCappedExponentialBackoffWithJitter()
    {
        var delays = new RetryDelayStrategy();
        var first = delays.GetDelay(1, null);
        var second = delays.GetDelay(2, null);
        var eighth = delays.GetDelay(8, null);

        Assert.InRange(first, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1250));
        Assert.InRange(second, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2250));
        Assert.InRange(eighth, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SupportsRetryAfterHttpDate()
    {
        var handler = new ScriptedHandler((attempt, _, _) =>
        {
            if (attempt > 1)
            {
                return Task.FromResult(Response(HttpStatusCode.OK, SuccessBody()));
            }

            var response = Response(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddSeconds(2));
            return Task.FromResult(response);
        });
        var delays = new RecordingRetryDelayStrategy();
        var result = await CreateClient(handler, retryDelayStrategy: delays)
            .ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal(1, result.RetryCount);
        var delay = Assert.Single(delays.RequestedDelays);
        Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task DoesNotRetryNormalClientErrors(HttpStatusCode status)
    {
        var handler = new ScriptedHandler((_, _, _) => Task.FromResult(
            Response(status, "DO_NOT_LEAK response body")));
        var options = CreateApimOptions();
        options.MaxAttempts = 3;
        var client = CreateClient(handler, apimOptions: options);

        var exception = await Assert.ThrowsAsync<GatewayException>(() =>
            client.ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal((int)status, exception.StatusCode);
        Assert.Equal(0, exception.RetryCount);
        Assert.DoesNotContain("DO_NOT_LEAK", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationDoesNotRetry()
    {
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(HttpStatusCode.OK, SuccessBody());
        });
        var options = CreateApimOptions();
        options.MaxAttempts = 3;
        var client = CreateClient(handler, apimOptions: options);
        using var cts = new CancellationTokenSource();

        var request = client.ChatAsync(Request(), ModelRole.Router, Context(), cts.Token);
        await Task.Delay(20);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallerDeadlineCancellationDuringBackoffPreventsAnotherAttempt()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, "{}")));
        var delay = new CancellingRetryDelayStrategy(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(handler, retryDelayStrategy: delay)
                .ChatAsync(Request(), ModelRole.Router, Context(), cts.Token));

        Assert.Single(handler.Requests);
        Assert.Single(delay.RequestedDelays);
    }

    [Fact]
    public async Task AttemptTimeoutRetriesAndCanSucceedOnSecondAttempt()
    {
        var handler = new ScriptedHandler(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Response(HttpStatusCode.OK, SuccessBody());
        });
        var client = CreateClient(
            handler,
            apimOptions: CreateApimOptions(ApimOptions.AzureDeploymentsRouteStyle),
            retryDelayStrategy: new RecordingRetryDelayStrategy(),
            attemptTimeoutOverride: TimeSpan.FromMilliseconds(500));

        var response = await client.ChatAsync(Request(), ModelRole.Evaluator, Context(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, response.RetryCount);
    }

    [Fact]
    public void HttpClientTimeoutIsInfinite()
    {
        using var client = new HttpClient();

        ApimAiGatewayClient.ConfigureHttpClient(client);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void TypedHttpClientHasInfiniteTimeoutAndNoResilienceHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<IApimAiGatewayClient, ApimAiGatewayClient>(
            ApimAiGatewayClient.ConfigureHttpClient);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IApimAiGatewayClient));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.DoesNotContain(
            provider.GetServices<IHttpMessageHandlerBuilderFilter>(),
            filter => filter.GetType().Name.Contains("Resilience", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypedApimClientResolvesFromDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(
            Microsoft.Extensions.Options.Options.Create(CreateApimOptions()));
        services.AddSingleton<ISecretMasker, SecretMasker>();
        services.AddSingleton<IApimCredentialProvider, NoApimCredentialProvider>();
        services.AddSingleton<IRetryDelayStrategy, RetryDelayStrategy>();
        services.AddSingleton<SafetyMetrics>();
        services.AddHttpClient<IApimAiGatewayClient, ApimAiGatewayClient>(
            ApimAiGatewayClient.ConfigureHttpClient);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ApimAiGatewayClient>(
            provider.GetRequiredService<IApimAiGatewayClient>());
    }

    [Fact]
    public async Task AzureDeploymentRouteAndRoleUseConfiguredDeployment()
    {
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, SuccessBody())));
        var options = CreateApimOptions(ApimOptions.AzureDeploymentsRouteStyle);
        var client = CreateClient(handler, apimOptions: options);

        _ = await client.ChatAsync(Request(), ModelRole.Synthesizer, Context(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://apim.example.test/openai/deployments/strong-model/chat/completions?api-version=2025-01-01",
            request.Uri.AbsoluteUri);
        Assert.DoesNotContain("\"model\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiV1RoutePutsDeploymentInRequestModel()
    {
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, SuccessBody())));
        var options = CreateApimOptions(ApimOptions.OpenAiV1RouteStyle);
        var client = CreateClient(handler, apimOptions: options);

        _ = await client.ChatAsync(Request(), ModelRole.Profiler, Context(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://apim.example.test/openai/v1/chat/completions", request.Uri.AbsoluteUri);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("cheap-model", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ReadsConfiguredCacheStatusHeaderOnly()
    {
        var handler = new ScriptedHandler((_, _, _) =>
        {
            var response = Response(HttpStatusCode.OK, SuccessBody());
            response.Headers.TryAddWithoutValidation("X-APIM-Cache", "hit");
            return Task.FromResult(response);
        });
        var options = CreateApimOptions();
        options.CacheStatusHeader = "X-APIM-Cache";

        var result = await CreateClient(handler, apimOptions: options)
            .ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal("hit", result.CacheStatus);
        var noHeaderOptions = CreateApimOptions();
        var noHeader = await CreateClient(handler, apimOptions: noHeaderOptions)
            .ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None);
        Assert.Null(noHeader.CacheStatus);
    }

    [Fact]
    public async Task RoutesCheapRolesToCheapAndSynthesizerToStrong()
    {
        foreach (var role in new[] { ModelRole.Router, ModelRole.Profiler, ModelRole.Evaluator })
        {
            var handler = new ScriptedHandler((_, _, _) =>
                Task.FromResult(Response(HttpStatusCode.OK, SuccessBody())));
            _ = await CreateClient(handler).ChatAsync(
                Request(), role, Context(), CancellationToken.None);
            Assert.Contains("/deployments/cheap-model/", Assert.Single(handler.Requests).Uri.AbsolutePath);
        }
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"ok\",\"tool_calls\":[]},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":4,\"prompt_tokens_details\":{\"cached_tokens\":2}}}", 11, 4, 2, true)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"ok\",\"tool_calls\":[]},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":4}}", 11, 4, null, true)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"ok\",\"tool_calls\":[]},\"finish_reason\":\"stop\"}]}", null, null, null, false)]
    public async Task ParsesUsageAndLeavesMissingFieldsNull(
        string body,
        int? input,
        int? output,
        int? cached,
        bool usagePresent)
    {
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body)));
        var result = await CreateClient(handler).ChatAsync(
            Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal(usagePresent, result.Usage is not null);
        Assert.Equal(input, result.Usage?.InputTokens);
        Assert.Equal(output, result.Usage?.OutputTokens);
        Assert.Equal(cached, result.Usage?.CachedTokens);
    }

    [Fact]
    public async Task ParsesToolCallsAndSerializesOpenAiRequestShapes()
    {
        var responseBody = """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"README.md\"}"}}
            ]},"finish_reason":"tool_calls"}]}
            """;
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, responseBody)));
        using var schemaDocument = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object"}}}""");
        var schema = schemaDocument.RootElement.Clone();
        var request = new ChatRequest(
            [new ChatMessage("system", "fixed"), new ChatMessage("user", "question")],
            OpenAiToolDefinitions.All,
            "auto",
            0,
            256,
            schema);

        var response = await CreateClient(handler).ChatAsync(
            request, ModelRole.Evaluator, Context(), CancellationToken.None);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
        using var wire = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("auto", wire.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(256, wire.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("function", wire.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.True(wire.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task FiltersFinishReasonAnd400PolicyViolationWithoutRetry()
    {
        _ = AiTelemetry.Meter;
        long measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AiTelemetry.MeterName
                && instrument.Name == "safety.content_filtered")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "safety.content_filtered")
            {
                Interlocked.Add(ref measurements, measurement);
            }
        });
        listener.Start();

        var finishHandler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, FilteredBody())));
        var errorHandler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.BadRequest,
                """{"error":{"innererror":{"code":"ResponsibleAIPolicyViolation"}}}""")));
        var finishException = await Assert.ThrowsAsync<ContentFilteredException>(() =>
            CreateClient(finishHandler).ChatAsync(
                Request(), ModelRole.Synthesizer, Context(), CancellationToken.None));
        var errorException = await Assert.ThrowsAsync<ContentFilteredException>(() =>
            CreateClient(errorHandler).ChatAsync(
                Request(), ModelRole.Synthesizer, Context(), CancellationToken.None));

        Assert.Equal("content_filter", finishException.FinishReason);
        Assert.Equal("ResponsibleAIPolicyViolation", errorException.FinishReason);
        Assert.Single(finishHandler.Requests);
        Assert.Single(errorHandler.Requests);
        Assert.Equal(2, Volatile.Read(ref measurements));
    }

    [Fact]
    public async Task MasksUserAndToolContentAndNeverForwardsCallerAuthorization()
    {
        var key = string.Concat("synthetic", "-", "api", "-", "key-value");
        var secretAssignment = string.Concat(
            "api", "_key=", key, " contact person@example.test phone +90 532 123 45 67");
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, SuccessBody())));
        var options = CreateApimOptions(ApimOptions.AzureDeploymentsRouteStyle);
        options.Auth.Scheme = "SubscriptionKey";
        options.Auth.HeaderName = "X-Test-Key";
        options.Auth.Key = "synthetic-apim-key";
        var provider = new SubscriptionKeyCredentialProvider(
            Microsoft.Extensions.Options.Options.Create(options));
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(loggerProvider));
        var client = CreateClient(
            handler, apimOptions: options, credentialProvider: provider, logger: loggerFactory);
        var request = new ChatRequest(
        [
            new ChatMessage("system", "static system instruction"),
            new ChatMessage("user", secretAssignment),
            new ChatMessage("tool", secretAssignment)
        ]);

        _ = await client.ChatAsync(request, ModelRole.Router, Context(), CancellationToken.None);

        var captured = Assert.Single(handler.Requests);
        Assert.DoesNotContain(key, captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("532 123 45 67", captured.Body, StringComparison.Ordinal);
        Assert.Contains("api_key=***MASKED***", captured.Body, StringComparison.Ordinal);
        Assert.Equal("synthetic-apim-key", captured.TestSubscriptionKey);
        Assert.Null(captured.Authorization);
        Assert.DoesNotContain(key, string.Join('\n', loggerProvider.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityContainsOnlyApprovedTagsAndTraceContextPropagates()
    {
        var capturedTags = new ConcurrentDictionary<string, object?>(StringComparer.Ordinal);
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.TagObjects)
                {
                    capturedTags[tag.Key] = tag.Value;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        using (listener)
        {
            using var parent = new Activity("test-parent")
                .SetIdFormat(ActivityIdFormat.W3C)
                .Start();
            var prompt = "DO_NOT_LOG_PROMPT";
            var answer = "DO_NOT_LOG_RESPONSE";
            var handler = new ScriptedHandler((_, _, _) => Task.FromResult(
                Response(HttpStatusCode.OK, SuccessBody(answer))));
            var loggerProvider = new CapturingLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(loggerProvider));

            _ = await CreateClient(handler, logger: loggerFactory).ChatAsync(
                Request(prompt), ModelRole.Evaluator, Context(), CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.False(string.IsNullOrWhiteSpace(request.TraceParent));
            var telemetry = string.Join('\n', capturedTags.Select(pair => $"{pair.Key}={pair.Value}"))
                + "\n" + string.Join('\n', loggerProvider.Messages);
            Assert.DoesNotContain(prompt, telemetry, StringComparison.Ordinal);
            Assert.DoesNotContain(answer, telemetry, StringComparison.Ordinal);
            Assert.Contains("gen_ai.request.model", telemetry, StringComparison.Ordinal);
            Assert.Contains("retry.count", telemetry, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ErrorLogsAndSpanTagsNeverContainKeysPromptsOrResponses()
    {
        var apiKey = string.Concat("synthetic", "-", "subscription", "-", "key");
        var prompt = "sensitive-question-content";
        var responseText = "sensitive-model-response";
        var handler = new ScriptedHandler((_, _, _) =>
            Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, responseText)));
        var apimOptions = CreateApimOptions();
        apimOptions.Auth.Scheme = "SubscriptionKey";
        apimOptions.Auth.HeaderName = "X-Test-Key";
        apimOptions.Auth.Key = apiKey;
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(loggerProvider));
        var tags = new ConcurrentDictionary<string, object?>(StringComparer.Ordinal);
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.TagObjects)
                {
                    tags[tag.Key] = tag.Value;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        using (listener)
        {
            var client = CreateClient(
                handler,
                apimOptions,
                credentialProvider: new SubscriptionKeyCredentialProvider(
                    Microsoft.Extensions.Options.Options.Create(apimOptions)),
                logger: loggerFactory);
            await Assert.ThrowsAsync<GatewayException>(() => client.ChatAsync(
                Request(prompt), ModelRole.Router, Context(), CancellationToken.None));
        }

        var telemetry = string.Join('\n', loggerProvider.Messages)
            + "\n"
            + string.Join('\n', tags.Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.DoesNotContain(apiKey, telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt, telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain(responseText, telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", telemetry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulCallLongerThan30SecondsIsNotCutOff()
    {
        var handler = new ScriptedHandler(async (_, _, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30.1));
            return Response(HttpStatusCode.OK, SuccessBody());
        });
        var apimOptions = CreateApimOptions(ApimOptions.AzureDeploymentsRouteStyle);
        apimOptions.AttemptTimeoutSeconds = 60;

        var response = await CreateClient(handler, apimOptions: apimOptions)
            .ChatAsync(Request(), ModelRole.Router, Context(), CancellationToken.None);

        Assert.Equal("ok", response.Content);
        Assert.Single(handler.Requests);
        Assert.True(response.LatencyMs >= 30_000);
    }

    [Fact]
    public void ManagedIdentityAndOAuthSchemesFailOptionsValidation()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var validator = new ApimOptionsValidator(configuration, environment);

        foreach (var scheme in new[] { "ManagedIdentity", "OAuth" })
        {
            var options = CreateApimOptions();
            options.Auth.Scheme = scheme;
            var result = validator.Validate(null, options);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures!, failure =>
                failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NoneCredentialSchemeIsLimitedToTestingEnvironment()
    {
        var configuration = new ConfigurationBuilder().Build();
        var options = CreateApimOptions();
        options.Auth.Scheme = "None";
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns(Environments.Development);
        var testing = Substitute.For<IHostEnvironment>();
        testing.EnvironmentName.Returns("Testing");

        Assert.False(new ApimOptionsValidator(configuration, development)
            .Validate(null, options).Succeeded);
        Assert.True(new ApimOptionsValidator(configuration, testing)
            .Validate(null, options).Succeeded);
    }

    [Fact]
    public void TelemetryRegistrationIsOptionalAndSetsRequiredResourceAttributes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_VERSION"] = "test-version",
                ["GIT_COMMIT_SHA"] = "test-sha",
                ["Telemetry:Team"] = "test-team",
                ["Telemetry:Application"] = "test-app",
                ["Telemetry:Environment"] = "testing"
            })
            .Build();
        var attributes = TelemetryRegistration.GetResourceAttributes(configuration);
        var telemetryServices = new ServiceCollection();

        Assert.False(TelemetryRegistration.HasAzureMonitorConnectionString(configuration));
        Assert.False(telemetryServices.AddAssessmentOpenTelemetry(configuration));
        Assert.Equal("hackathon-assessment-api", attributes["service.name"]);
        Assert.Equal("test-version", attributes["service.version"]);
        Assert.Equal("test-sha", attributes["commit.sha"]);
        Assert.Equal("test-team", attributes["team"]);
        Assert.Equal("test-app", attributes["application"]);
        Assert.Equal("testing", attributes["environment"]);

        var connectedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Concat(
                    "InstrumentationKey=", new string('0', 32),
                    ";IngestionEndpoint=https://example.test/")
            })
            .Build();
        Assert.True(TelemetryRegistration.HasAzureMonitorConnectionString(connectedConfiguration));
        Assert.True(new ServiceCollection()
            .AddAssessmentOpenTelemetry(connectedConfiguration));
    }

    private static ChatRequest Request(string question = "test question") =>
        new([new ChatMessage("system", "stable system"), new ChatMessage("user", question)]);

    private static AiCallContext Context() =>
        new("correlation-test", MetricId.M03, "unit-test");

    private static ApimOptions CreateApimOptions(
        string routeStyle = ApimOptions.AzureDeploymentsRouteStyle,
        int attemptTimeoutSeconds = 60) =>
        new()
        {
            BaseUrl = "https://apim.example.test",
            RouteStyle = routeStyle,
            ApiVersion = "2025-01-01",
            Auth = new ApimAuthOptions { Scheme = "None", HeaderName = "X-Test-Key" },
            Deployments = new ApimDeploymentOptions { Cheap = "cheap-model", Strong = "strong-model" },
            AttemptTimeoutSeconds = attemptTimeoutSeconds,
            MaxAttempts = 3
        };

    private static ApimAiGatewayClient CreateClient(
        HttpMessageHandler handler,
        ApimOptions? apimOptions = null,
        IRetryDelayStrategy? retryDelayStrategy = null,
        IApimCredentialProvider? credentialProvider = null,
        ILoggerFactory? logger = null,
        TimeSpan? attemptTimeoutOverride = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            Microsoft.Extensions.Options.Options.Create(apimOptions ?? CreateApimOptions()),
            credentialProvider ?? new NoApimCredentialProvider(),
            retryDelayStrategy ?? new RecordingRetryDelayStrategy(),
            new SecretMasker(),
            new SafetyMetrics(NullLogger<SafetyMetrics>.Instance),
            (logger ?? NullLoggerFactory.Instance).CreateLogger<ApimAiGatewayClient>(),
            attemptTimeoutOverride);

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    private static string SuccessBody(string content = "ok") =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { content, tool_calls = Array.Empty<object>() },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 11,
                completion_tokens = 4,
                prompt_tokens_details = new { cached_tokens = 2 }
            }
        });

    private static string FilteredBody() =>
        """{"choices":[{"message":{"content":null,"tool_calls":[]},"finish_reason":"content_filter"}]}""";

    private sealed class RecordingRetryDelayStrategy : IRetryDelayStrategy
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public TimeSpan GetDelay(int retryNumber, TimeSpan? retryAfter) =>
            retryAfter ?? TimeSpan.FromSeconds(retryNumber);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestedDelays.Add(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingRetryDelayStrategy(CancellationTokenSource source)
        : IRetryDelayStrategy
    {
        public List<TimeSpan> RequestedDelays { get; } = [];
        public TimeSpan GetDelay(int retryNumber, TimeSpan? retryAfter) =>
            retryAfter ?? TimeSpan.FromSeconds(1);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestedDelays.Add(delay);
            source.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string Body,
        string? TestSubscriptionKey,
        string? Authorization,
        string? TraceParent);

    private sealed class ScriptedHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                body,
                request.Headers.TryGetValues("X-Test-Key", out var values)
                    ? values.Single()
                    : null,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("traceparent", out var traceParents)
                    ? traceParents.Single()
                    : null));
            return await callback(Requests.Count, request, cancellationToken);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}
