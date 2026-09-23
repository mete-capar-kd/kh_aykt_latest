using System.Collections.Concurrent;
using System.Collections.Immutable;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Caching;
using Hackathon.Assessment.Api.Contracts;
using Hackathon.Assessment.Api.Domain;
using Hackathon.Assessment.Api.Options;
using Hackathon.Assessment.Api.Reporting;
using Hackathon.Assessment.Api.Safety;
using Hackathon.Assessment.Api.Scoring;
using Hackathon.Assessment.Api.Snapshot;
using Microsoft.Extensions.Options;

namespace Hackathon.Assessment.Api.Orchestration;

public sealed partial class AskOrchestrator : IAskOrchestrator, IDisposable
{
    private readonly IRepositorySnapshotProvider _snapshots;
    private readonly ProfilerAgent _profiler;
    private readonly MetricEvaluator _evaluator;
    private readonly MetricResultCache _results;
    private readonly ProfileResultCache _profiles;
    private readonly SynthesizerAgent _synthesizer;
    private readonly AskRouterAgent _router;
    private readonly RefusalBuilder _refusals;
    private readonly OutputGuard _outputGuard;
    private readonly PromptCatalog _catalog;
    private readonly ReportBuilder _reports;
    private readonly ILogger<AskOrchestrator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly AssessmentOptions _options;
    private readonly SemaphoreSlim _fullAssessments;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<MetricId, EvaluationOutcome>>>
        _commitFlights = new(StringComparer.Ordinal);

    public AskOrchestrator(
        IRepositorySnapshotProvider snapshots,
        ProfilerAgent profiler,
        MetricEvaluator evaluator,
        MetricResultCache results,
        ProfileResultCache profiles,
        AskRouterAgent router,
        SynthesizerAgent synthesizer,
        RefusalBuilder refusals,
        OutputGuard outputGuard,
        PromptCatalog catalog,
        ReportBuilder reports,
        IOptions<AssessmentOptions> options,
        TimeProvider timeProvider,
        ILogger<AskOrchestrator> logger)
    {
        _snapshots = snapshots;
        _profiler = profiler;
        _evaluator = evaluator;
        _results = results;
        _profiles = profiles;
        _router = router;
        _synthesizer = synthesizer;
        _refusals = refusals;
        _outputGuard = outputGuard;
        _catalog = catalog;
        _reports = reports;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _fullAssessments = new SemaphoreSlim(_options.MaxConcurrentFullAssessments);
    }

    public async Task<AskResponse> AskAsync(
        AskContext context,
        AskRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.GlobalTimeoutSeconds));
        try
        {
            return await AssessAsync(context, request, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw new AssessmentTimeoutException("The assessment deadline was exceeded.");
        }
    }

    public void Dispose() => _fullAssessments.Dispose();

    public async Task WarmupAsync(
        string repositoryUrl,
        string reference,
        CancellationToken cancellationToken)
    {
        var snapshot = await _snapshots.GetAsync(
            new Uri(repositoryUrl, UriKind.Absolute),
            reference,
            cancellationToken);
        var profile = await _profiles.GetOrAddAsync(
            repositoryUrl,
            snapshot.CommitSha,
            _catalog.PromptVersion,
            () => _profiler.ProfileAsync(
                snapshot,
                new AiCallContext("cache-warmup", null, "profile"),
                CancellationToken.None),
            cancellationToken);
        var context = new AskContext("cache-warmup", "startup warmup", false, "system");
        _ = await EvaluateSelectedAsync(
            context,
            repositoryUrl,
            snapshot,
            profile,
            MetricNames.All,
            cancellationToken);
        _results.PinWarmupCommit(repositoryUrl, snapshot.CommitSha, _catalog.PromptVersion);
    }

    private async Task<AskResponse> AssessAsync(
        AskContext context,
        AskRequest request,
        CancellationToken cancellationToken)
    {
        var repositoryUrl = request.RepositoryUrl
            ?? throw new ArgumentException("A validated repository URL is required.", nameof(request));
        var reference = request.Ref
            ?? throw new ArgumentException("A validated repository ref is required.", nameof(request));
        var question = context.NormalizedQuestion;
        var route = await _router.RouteAsync(
            question,
            context.SuspectedInjection,
            new AiCallContext(context.CorrelationId, null, "routing"),
            cancellationToken);
        if (route.Intent is "unsafe" or "out_of_scope")
        {
            return _refusals.Build(route.Intent, route.Language, context.CorrelationId);
        }

        var language = route.Language == "other" ? "en" : route.Language;
        var questionType = route.QuestionType;
        var selected = request.Metrics is not null
            ? request.Metrics.Select(ParseMetric).ToImmutableArray()
            : route.Metrics.IsEmpty ? MetricNames.All : route.Metrics;
        var snapshot = await _snapshots.GetAsync(
            new Uri(repositoryUrl, UriKind.Absolute), reference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var missingFile = QuestionClassifier.MissingFile(question, snapshot);
        IReadOnlyDictionary<MetricId, EvaluationOutcome> outcomes;
        if (missingFile is not null)
        {
            outcomes = new Dictionary<MetricId, EvaluationOutcome>();
        }
        else
        {
            var profile = await _profiles.GetOrAddAsync(
                repositoryUrl,
                snapshot.CommitSha,
                _catalog.PromptVersion,
                () => _profiler.ProfileAsync(
                    snapshot,
                    new AiCallContext(context.CorrelationId, null, "profile"),
                    CancellationToken.None),
                cancellationToken);
            outcomes = await EvaluateSelectedAsync(
                context, repositoryUrl, snapshot, profile, selected, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var metrics = MetricNames.All.Select(id =>
        {
            if (!selected.Contains(id))
            {
                return NotAssessed(
                    id,
                    request.Metrics is null
                        ? "Bu soru için değerlendirilmedi"
                        : "İstekte seçilmedi");
            }

            if (missingFile is not null)
            {
                return NotAssessed(id, "Soruda belirtilen dosya bulunamadı");
            }

            return BuildMetric(outcomes[id]);
        }).ToImmutableArray();
        var report = new AssessmentReport(
            Guid.NewGuid().ToString("N"),
            repositoryUrl,
            reference,
            snapshot.CommitSha,
            metrics,
            ScoreCalculator.OverallScore(metrics),
            "",
            _timeProvider.GetUtcNow(),
            new ModelInfo(route.Deployment, "cheap", "cheap", "strong", _catalog.PromptVersion));

        var summary = "";
        var answer = "";
        var answerType = AnswerType.InsufficientEvidence;
        var evidence = ImmutableArray<EvidenceReference>.Empty;
        if (missingFile is not null)
        {
            answer = language == "tr"
                ? $"Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: {missingFile} bulunamadı."
                : $"I cannot answer this from the repository evidence: {missingFile} was not found.";
        }
        else if (QuestionClassifier.IsOwnerQuestion(question)
            && !metrics.SelectMany(metric => metric.Findings)
                .SelectMany(finding => finding.Evidence)
                .Any(item => QuestionClassifier.IsOwnerQuestion(item.Snippet)))
        {
            answer = language == "tr"
                ? "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: uygulama sahibine ilişkin kanıt bulunamadı."
                : "I cannot answer this from the repository evidence: no owner evidence was found.";
        }
        else
        {
            try
            {
                var synthesized = await _synthesizer.SynthesizeAsync(
                    question,
                    language,
                    questionType,
                    report,
                    new AiCallContext(context.CorrelationId, null, "synthesis"),
                    cancellationToken);
                answer = synthesized.Answer;
                answerType = synthesized.AnswerType;
                evidence = synthesized.Evidence;
                summary = synthesized.ExecutiveSummary;
            }
            catch (ContentFilteredException)
            {
                return _refusals.Build("content_filter", language, context.CorrelationId);
            }
            catch (GatewayException)
            {
                answer = language == "tr" ? "Özet üretilemedi." : "Summary could not be generated.";
            }
            catch (FormatException)
            {
                answer = language == "tr" ? "Özet üretilemedi." : "Summary could not be generated.";
            }
            catch (System.Text.Json.JsonException)
            {
                answer = language == "tr" ? "Özet üretilemedi." : "Summary could not be generated.";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var guarded = _outputGuard.Guard(answer, summary, report);
        if (guarded.RefusalReason is not null)
        {
            return _refusals.Build(guarded.RefusalReason, language, context.CorrelationId);
        }

        answer = guarded.Answer;
        summary = guarded.ExecutiveSummary;
        if (answerType != AnswerType.InsufficientEvidence)
        {
            answerType = route.Intent == "repo_question"
                ? AnswerType.RepoAnswer
                : AnswerType.Assessment;
        }

        report = report with { ReportMarkdown = _reports.Build(report, summary) };
        return new AskResponse(
            answer,
            answerType,
            _catalog.PromptVersion,
            evidence,
            context.CorrelationId,
            report);
    }

    private async Task<IReadOnlyDictionary<MetricId, EvaluationOutcome>> EvaluateSelectedAsync(
        AskContext context,
        string repositoryUrl,
        RepositorySnapshot snapshot,
        RepoProfile profile,
        ImmutableArray<MetricId> selected,
        CancellationToken cancellationToken)
    {
        var key = $"{repositoryUrl.ToLowerInvariant()}|{snapshot.CommitSha}|{_catalog.PromptVersion}";
        var collected = new Dictionary<MetricId, EvaluationOutcome>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = selected.Where(id => !collected.ContainsKey(id)).ToImmutableArray();
            if (remaining.IsEmpty)
            {
                return collected;
            }

            if (remaining.All(id =>
                _results.IsCached(repositoryUrl, snapshot.CommitSha, id, _catalog.PromptVersion)))
            {
                foreach (var (id, outcome) in await RunAllAsync(remaining))
                {
                    collected.Add(id, outcome);
                }

                return collected;
            }

            var leader = new TaskCompletionSource<IReadOnlyDictionary<MetricId, EvaluationOutcome>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var flight = _commitFlights.GetOrAdd(key, leader.Task);
            if (!ReferenceEquals(flight, leader.Task))
            {
                var shared = await flight.WaitAsync(cancellationToken);
                foreach (var (id, outcome) in shared)
                {
                    if (selected.Contains(id))
                    {
                        collected.TryAdd(id, outcome);
                    }
                }

                continue;
            }

            IReadOnlyDictionary<MetricId, EvaluationOutcome> completed =
                new Dictionary<MetricId, EvaluationOutcome>();
            try
            {
                await _fullAssessments.WaitAsync(cancellationToken);
                try
                {
                    completed = await RunAllAsync(remaining);
                    foreach (var (id, outcome) in completed)
                    {
                        collected.Add(id, outcome);
                    }

                    return collected;
                }
                finally
                {
                    _fullAssessments.Release();
                }
            }
            finally
            {
                _commitFlights.TryRemove(
                    new KeyValuePair<string, Task<IReadOnlyDictionary<MetricId, EvaluationOutcome>>>(
                        key, leader.Task));
                leader.TrySetResult(completed);
            }
        }

        async Task<IReadOnlyDictionary<MetricId, EvaluationOutcome>> RunAllAsync(
            ImmutableArray<MetricId> metricIds)
        {
            var tasks = metricIds.Select(async id => (
                Id: id,
                Outcome: await RunMetricAsync(id, context, repositoryUrl, snapshot, profile, cancellationToken)));
            var evaluated = await Task.WhenAll(tasks);
            return evaluated.ToDictionary(entry => entry.Id, entry => entry.Outcome);
        }
    }

    private async Task<EvaluationOutcome> RunMetricAsync(
        MetricId id,
        AskContext context,
        string repositoryUrl,
        RepositorySnapshot snapshot,
        RepoProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _results.GetOrAddAsync(
                repositoryUrl,
                snapshot.CommitSha,
                id,
                _catalog.PromptVersion,
                token => _evaluator.EvaluateAsync(
                    id, snapshot, profile, new AiCallContext(context.CorrelationId, id, "evaluation"), token),
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EvaluationOutcome.NotAssessable(id, "Metrik zaman aşımı");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MetricFailed(_logger, id, error.GetType().Name);
            return EvaluationOutcome.NotAssessable(id, "Metrik değerlendirme hatası");
        }
    }

    private static MetricResult BuildMetric(EvaluationOutcome outcome)
    {
        var coverage = CoverageCalculator.Evaluate(outcome.SubChecks);
        var reason = outcome.IsTimeoutOrError
            ? outcome.NotAssessableReason ?? "Metrik değerlendirme hatası"
            : outcome.NotAssessableReason;
        var score = ScoreCalculator.Calculate(outcome.Findings);
        var status = StatusResolver.Resolve(score, outcome.Findings, coverage.Coverage, reason);
        return new MetricResult(
            outcome.MetricId,
            MetricNames.GetName(outcome.MetricId),
            status.Status,
            status.Score,
            outcome.Rationale,
            outcome.Risk,
            coverage.Coverage,
            coverage.SubChecks,
            outcome.Findings,
            status.NotAssessableReason,
            outcome.FilesExamined,
            outcome.ToolCalls,
            outcome.RejectedCount);
    }

    private static MetricResult NotAssessed(MetricId id, string reason) =>
        new(
            id,
            MetricNames.GetName(id),
            MetricStatus.Degerlendirilemedi,
            null,
            reason,
            "",
            Coverage.None,
            [],
            [],
            reason,
            0,
            0,
            0);

    private static MetricId ParseMetric(string value)
    {
        if (value is not { Length: 3 }
            || value[0] != 'm'
            || !int.TryParse(value.AsSpan(1), out var number)
            || number is < 1 or > 10)
        {
            throw new ArgumentException("A validated metric identifier is required.", nameof(value));
        }

        return (MetricId)(number - 1);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metric {MetricId} failed with {ErrorType}.")]
    private static partial void MetricFailed(ILogger logger, MetricId metricId, string errorType);
}
