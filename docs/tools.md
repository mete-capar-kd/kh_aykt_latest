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
| `run_scanner(metric_id)` | One of `m01`–`m10` | Runs matching deterministic Generic and detected stack scanners; returns at most 200 sorted candidate findings and explicit `truncated` / `total`. A candidate is not a validated finding. |
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

## Deterministic scanner candidates

Scanners inspect pinned snapshot text only. Generic rules always run; a .NET,
Python, or Node package runs when its stack signal is present. Multiple stack
signals run together. A scanner result is a non-binding candidate for an evaluator
to verify; it is not a score or a recorded finding. Candidates in Test or Example
files are retained but marked `likelyFalsePositive` with a reason.

| Rule ID | Package | Metric | Severity hint | What it searches for | Known false positive |
|---|---|---|---|---|---|
| GEN-SECRET-001 | Generic | m03 | high | A source line changed by `ISecretMasker`. | Test/Example fixtures may intentionally contain synthetic or documented values. |
| GEN-ENV-001 | Generic | m03 | high | `.env` or `.env.*` file presence; `.env.example`, `.env.sample`, `.env.template` are excluded. | Test/Example role; environment templates are excluded. |
| GEN-DOCKER-001 | Generic | m08 | medium | Last Docker stage has no `USER` or selects `root`/`0`. | Test/Example Dockerfiles; some single-purpose build stages intentionally run as root. |
| GEN-DOCKER-002 | Generic | m08 | medium | `FROM` image is untagged or uses `:latest`, unless pinned by `@sha256`. | `scratch` is a valid special base and is flagged as likely false positive. |
| GEN-DOCKER-003 | Generic | m03, m08 | high | Assigned `ARG`/`ENV` value for password, secret, token, API key, or connection variable. | Test/Example fixtures and non-secret placeholder values. |
| GEN-DOCKER-004 | Generic | m08 | low | Dockerfile has no `HEALTHCHECK` directive. | Test/Example images or images whose orchestrator supplies the health probe. |
| GEN-WF-001 | Generic | m07 | medium | `permissions: write-all` or no explicit `permissions:` block. | Workflows may rely on a repository-level read-only default; inspect effective permissions. |
| GEN-WF-002 | Generic | m07, m03 | high | `pull_request_target` trigger. | Privileged automation can be intentional but requires careful trust-boundary review. |
| GEN-WF-003 | Generic | m07 | low | `uses:` action reference without a full 40-hex SHA. | `actions/*` and `github/*` version tags are retained as likely false positives; local actions and full SHAs are not reported. |
| GEN-DOC-001 | Generic | m10 | medium | Missing root `README*` file. | Generated repositories may document onboarding elsewhere. |
| GEN-DOC-002 | Generic | m10 | low | README has fewer than 30 non-empty lines or lacks installation/setup and architecture headings. | A deliberately minimal README may link to authoritative docs elsewhere. |
| GEN-DOC-003 | Generic | m10 | low | No file under `docs/adr/` or `adr/`. | Decisions may be recorded in another documented governance system. |
| GEN-GITIGNORE-001 | Generic | m07 | medium | Exact broad ignore lines: `*.md`, `*.yml`, `*.yaml`, `*.json`, `.github/`, `prompts/`, `docs/`, `config/`, `infra/`, `Dockerfile`. | A narrow nested ignore rule may be intentional; the match is exact-line only. |
| NET-AUTH-001 | .NET | m02 | medium | `[AllowAnonymous]`. | Test files and paths containing `health` are marked likely false positive. |
| NET-ASYNC-001 | .NET | m09 | medium | `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`. | `.Result` without `Task`, `Async`, or `await` on that line is marked likely false positive. |
| NET-EXC-001 | .NET | m05 | medium | Empty `catch` body, including multiline form. | Test/Example code may deliberately suppress an expected exception. |
| NET-CFG-001 | .NET | m03 | high | `appsettings*.json` `ConnectionStrings` credential assignments for password, pwd, account key, or shared-access key. | Test/Example and placeholder connection settings. |
| NET-CORS-001 | .NET | m03 | medium | `AllowAnyOrigin()`. | Test-only hosts or intentionally public resources still need policy review. |
| NET-DI-001 | .NET | m01 | low | `new HttpClient(` outside Test-role files. | A deliberately short-lived client in isolated utility code may be intentional. |
| NET-DI-002 | .NET | m01 | medium | `.BuildServiceProvider()` in `Program.cs` or `Startup.cs`. | Design-time/bootstrap code may use a separate provider deliberately. |
| PY-EVAL-001 | Python | m03 | high | `eval(` or `exec(` calls. | Test/Example code and parsing of trusted expressions. |
| PY-TLS-001 | Python | m03 | high | `verify=False`. | Local test endpoints may use a controlled certificate fixture. |
| PY-SQL-001 | Python | m03, m04 | high | SQL verb inside an f-string with interpolation braces. | The match does not establish reachability or whether interpolated data is trusted. |
| PY-EXC-001 | Python | m05 | medium | Bare `except:`. | Deliberate process-boundary cleanup can catch broadly, but should be justified. |
| PY-DEBUG-001 | Python | m03, m08 | medium | `DEBUG = True`. | Local-only example/test settings may be intentional. |
| NODE-ENV-001 | Node | m01, m03 | low | Direct `process.env.` use, except `config/`, `*.config.js|ts`, and `env.js|ts`. | Configuration/bootstrap modules and test fixtures. |
| NODE-EXEC-001 | Node | m03 | high | Interpolated or concatenated `exec`/`execSync` argument when file mentions `child_process`. | Constant commands or validated, constrained arguments may be safe. |
| NODE-CORS-001 | Node | m03 | medium | Argument-less `cors()` or wildcard `origin: '*'`. | Deliberately public APIs may allow cross-origin access. |
