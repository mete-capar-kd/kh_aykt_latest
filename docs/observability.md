# Observability

## Signals

| Signal | Source | Recorded information |
|---|---|---|
| Requests | Azure Monitor OpenTelemetry ASP.NET Core instrumentation | HTTP method, route, status, duration, and W3C trace context. |
| Dependencies | OpenTelemetry HttpClient instrumentation | APIM HTTP dependency, status, and duration. |
| Traces | `ActivitySource` `Hackathon.Assessment.Ai` | One `chat {deployment}` span; model/deployment, finish reason, status, retry count, metric ID, correlation ID, and configured cache-status header only. |
| Metrics | `Meter` `Hackathon.Assessment` | Request duration, request status/count, token counts, and content-free safety counters. |
| Resources | `ConfigureResource` | `service.name`, `team`, `application`, `environment`, `service.version`, and `commit.sha`. |

The safety counters are `safety.injection_suspected`, `safety.refusal`,
`safety.leak_blocked`, `safety.unverified_ref_removed`, and
`safety.content_filtered`. They carry no question or response content.

The Azure Monitor exporter is registered only when
`APPLICATIONINSIGHTS_CONNECTION_STRING` is set. Without it, local and test runs
retain OpenTelemetry instrumentation without exporting. Sampling is configured
with `Telemetry:SamplingRatio`.

## Privacy and failure behavior

System prompts are static and deterministic. User and tool messages pass through
`ISecretMasker` before an APIM request. Prompt and response bodies, question text,
Authorization/cookie headers, API keys, secrets, and PII are never logged or added
as span tags. Exceptions contribute their type only; their message and body are
not recorded. Unknown APIM cache headers are not inferred; cache status remains
null unless the organization configures `Apim:CacheStatusHeader`.

Telemetry instrumentation is best-effort. Instrumentation failures emit only a
source-generated Debug log containing the exception type and do not fail an
assessment request.

## KQL examples

Total input/output/cached tokens by deployment:

```kusto
customMetrics
| where name in ("ai.tokens.input", "ai.tokens.output", "ai.tokens.cached")
| summarize tokens=sum(value) by name, deployment=tostring(customDimensions.deployment)
```

95th percentile model call duration by deployment:

```kusto
customMetrics
| where name == "ai.request.duration"
| summarize p95_ms=percentile(value, 95) by deployment=tostring(customDimensions.deployment)
```

Retry ratio by deployment (request events with one or more retries):

```kusto
customMetrics
| where name == "ai.requests"
| summarize requests=sum(value),
    retried=sumif(value, toint(customDimensions["retry.count"]) > 0)
    by deployment=tostring(customDimensions.deployment)
| extend retry_ratio=iff(requests == 0, 0.0, todouble(retried) / requests)
```

Content-filter event count:

```kusto
customMetrics
| where name == "safety.content_filtered"
| summarize filtered=sum(value)
```

Follow an HTTP request and APIM dependency by correlation ID:

```kusto
union requests, dependencies, traces
| where tostring(customDimensions["correlation.id"]) == "<correlation-id>"
| order by timestamp asc
```

Metric names and resource attributes describe observability intent; live Azure
export and organizational APIM behavior remain unverified until credentials and
endpoints are supplied.
