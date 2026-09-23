# Repository tools

All tools read only an in-memory snapshot pinned to a Git commit. Repository code,
build scripts, hooks, and commands are never executed. Tool responses are masked
before they are returned. A response that omits entries or clips a line sets
`truncated: true` and includes `total`.

## Tool contracts

| Tool | Arguments | Result and limits |
|---|---|---|
| `get_repo_manifest()` | none | Alphabetical paths (at most 500), extension-based language/file/line totals and manifest files. |
| `list_files(glob, limit=200)` | Required glob; optional positive limit | Matching paths, byte sizes, and line counts; result count is capped at 200. |
| `search_code(pattern, glob?, regex=true, max_results=200)` | Required pattern; optional glob, regex mode, and positive limit | Masked `{path, line, text}` matches; at most 200 results, text clipped to 300 characters, 2-second execution budget. Unsupported regex constructs return an error. When truncated, `total` is the number of matches encountered before stopping. |
| `read_file(path, start_line?, end_line?)` | Required relative path; optional 1-based line range | Masked lines formatted as `n: text`; at most 1,500 lines per call. |
| `run_scanner(metric_id)` | One of `m01`–`m10` | Scanner contract placeholder. Deterministic scanner implementations belong to P05. |
| `record_finding(finding)` | Finding fields, including severity, confidence, standard reference, relative path/range, snippet, and recommendation | Accepts only findings with verified snapshot provenance and returns the masked evidence hash and commit-pinned blob URL. |

The OpenAI function schemas are exposed by `OpenAiToolDefinitions.All`, serialized
through `AppJsonContext`. `IToolDispatcher`
dispatches only these six names; unknown names and malformed arguments return an
explicit `{ "error": "..." }` result.

## Snapshot and safety limits

`IRepositorySnapshotProvider` accepts HTTPS GitHub repository URLs whose host is in
`Repository:AllowedInputHosts`. It resolves the requested ref through the GitHub API,
then downloads the archive by pinned commit SHA. Each request and redirect is checked
against `Repository:AllowedDownloadHosts`; connections re-resolve and reject private,
loopback, link-local, reserved, and metadata-service addresses. Automatic redirects are
disabled. The optional `Repository:GitHubToken` is sent only as an authorization header
to `api.github.com`, never placed in a URL or forwarded to a redirected host.

The gzip response is read as a stream, never written to disk. `Assessment:MaxFiles`
and `Assessment:MaxTotalBytes` limit included text files (default 2,000 and 50 MiB);
the compressed response is limited to 50 MiB and expanded tar data to 200 MiB.
Exceeding a limit raises `SnapshotLimitExceededException` (HTTP 422); no partial
snapshot is returned. Invalid archive paths, links and non-regular files are skipped.
Binary files and files beneath `.git`, `node_modules`, `.venv`, `bin`, `obj`, or
`dist` are excluded. UTF-8/UTF-16 BOMs are handled and CRLF/CR/LF line starts
are tracked without copying individual lines.

## Evidence provenance and recording

`EvidenceLedger` is created separately per evaluator and is thread-safe. Only lines included in a returned
`read_file` or `search_code` result are marked as seen. A finding is accepted only if
its file exists in the pinned snapshot, its valid line range is no wider than a
120-line difference, its masked whitespace-normalized snippet occurs in that range,
and every cited line was returned to the same evaluator. Severity, confidence, and
standard reference must be present and supported; recommendations must be specific
and at least 20 characters.

Rejected findings return a reason and a correction path. The technical fingerprint
is SHA-256 of `metricId|path|normalized title (lower invariant)`. It excludes the
caller-provided finding ID, so changing an ID does not reset the failure counter.
The sixth failed attempt for one fingerprint is permanently rejected. Rejection
counts are exposed for the caller to populate
`MetricResult.RejectedFindings`.

Accepted evidence stores the whitespace-normalized, masked snippet, its lowercase
SHA-256 digest, and a GitHub blob URL pinned to the resolved commit and line range.
The hash is calculated over UTF-8 bytes of that normalized masked snippet; it is not
a hash of the raw source.

## Masking

`ISecretMasker` replaces API keys, access tokens, credential assignments, connection
string secrets, private-key blocks, JWTs, e-mail addresses, Turkish phone numbers,
and checksum-valid 11-digit TCKNs with `***MASKED***`. For keyed assignments,
the key name is retained and only its value is replaced. It is applied to tool output and every
field retained by `record_finding`; raw secret and PII values are not returned in
tool results.
