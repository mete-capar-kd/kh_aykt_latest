using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Hackathon.Assessment.Api.Telemetry;

public static partial class AiTelemetry
{
    public const string ActivitySourceName = "Hackathon.Assessment.Ai";
    public const string MeterName = "Hackathon.Assessment";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
    public static Meter Meter { get; } = new(MeterName);

    internal static Histogram<double> RequestDuration { get; } =
        Meter.CreateHistogram<double>("ai.request.duration", "ms");
    internal static Counter<long> Requests { get; } =
        Meter.CreateCounter<long>("ai.requests");
    internal static Counter<long> InputTokens { get; } =
        Meter.CreateCounter<long>("ai.tokens.input", "token");
    internal static Counter<long> OutputTokens { get; } =
        Meter.CreateCounter<long>("ai.tokens.output", "token");
    internal static Counter<long> CachedTokens { get; } =
        Meter.CreateCounter<long>("ai.tokens.cached", "token");
    internal static Counter<long> InjectionSuspectedCounter { get; } =
        Meter.CreateCounter<long>("safety.injection_suspected");
    internal static Counter<long> RefusalCounter { get; } =
        Meter.CreateCounter<long>("safety.refusal");
    internal static Counter<long> LeakBlockedCounter { get; } =
        Meter.CreateCounter<long>("safety.leak_blocked");
    internal static Counter<long> UnverifiedReferenceRemovedCounter { get; } =
        Meter.CreateCounter<long>("safety.unverified_ref_removed");
    internal static Counter<long> ContentFilteredCounter { get; } =
        Meter.CreateCounter<long>("safety.content_filtered");

    internal static void TryRecord(ILogger logger, Action record)
    {
        try
        {
            record();
        }
        catch (Exception exception)
        {
            LogTelemetryFailure(logger, exception.GetType().FullName ?? "Exception");
        }
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Debug,
        Message = "Telemetry instrumentation failed. ExceptionType={ExceptionType}")]
    private static partial void LogTelemetryFailure(ILogger logger, string exceptionType);
}

public sealed class SafetyMetrics(ILogger<SafetyMetrics> logger)
{
    public void InjectionSuspected() =>
        Add(AiTelemetry.InjectionSuspectedCounter);

    public void Refusal() =>
        Add(AiTelemetry.RefusalCounter);

    public void LeakBlocked() =>
        Add(AiTelemetry.LeakBlockedCounter);

    public void UnverifiedReferenceRemoved() =>
        Add(AiTelemetry.UnverifiedReferenceRemovedCounter);

    public void ContentFiltered() =>
        Add(AiTelemetry.ContentFilteredCounter);

    private void Add(Counter<long> counter) =>
        AiTelemetry.TryRecord(logger, () => counter.Add(1));
}
