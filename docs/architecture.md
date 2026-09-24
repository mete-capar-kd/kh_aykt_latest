# Mimari genel bakış
```mermaid
flowchart LR
    Client["Client / evaluator"] -->|"Sign-in"| Entra["Microsoft Entra ID"]
    Entra -->|"JWT bearer"| Client
    Client -->|"POST /api/ask + JWT"| Ask["POST /api/ask"]
    Ask --> InputGuard["Input Guard"]
    InputGuard --> Router["Ask Router"]
    Router -->|"unsafe / out_of_scope"| Refusal["Fixed safe refusal"]
    Router -->|"assessment / repo_question"| Orchestrator["Orchestrator (no LLM calls)"]
    Orchestrator --> Snapshot["Pinned GitHub snapshot / process cache"]
    Snapshot --> Profiler["Repo Profiler"]
    Snapshot --> Evaluators["Metric Evaluator ×10 (isolated)"]
    Profiler --> Scoring["Deterministic scoring"]
    Evaluators --> Scoring
    Scoring --> Synthesizer["Report Synthesizer"]
    Refusal --> OutputGuard
    Synthesizer --> OutputGuard["Output Guard"]
    OutputGuard --> Response["Masked, evidence-validated response"]
    Response --> Client

    Router -. "model call" .-> GatewayClient["IApimAiGatewayClient"]
    Profiler -. "model call" .-> GatewayClient
    Evaluators -. "model calls" .-> GatewayClient
    Synthesizer -. "model call" .-> GatewayClient
    GatewayClient --> APIM["Organization APIM AI Gateway"]
    APIM --> Foundry["Azure AI Foundry"]
    Foundry --> APIM
    APIM --> GatewayClient

    Anonymous["Anonymous client"] --> Health["GET /health"]
```

| Bileşen | Klasör | Sorumlu issue |
|---|---|---|
| Solution ve CI/GHAS iskeleti | `src/`, `.github/workflows/` | P01 |
| Specs, README, mimari, ADR ve şablonlar | `docs/`, `.github/` | P02 |
| API, config, rate limit ve health | `src/Hackathon.Assessment.Api/Endpoints`, `Options`, `Health` | P03 |
| Snapshot ve repo erişimi | `Snapshot/` | P04 |
| Read-only tools, bulgu kaydı ve scannerlar | `Tools/`, `Scanners/` | P05 |
| APIM client ve telemetry | `Ai/`, `Telemetry/` | P06 |
| Evaluator, metrikler ve sonuç cache'i | `Agents/`, `Caching/` | P07 |
| Orkestrasyon, puanlama ve raporlama | `Orchestration/`, `Scoring/`, `Reporting/` | P08 |
| Masking ve AURA safety guard'ları | `Masking/`, `Safety/` | P09 |
| Entra kimlik doğrulama ve AURA bağlantısı | `Auth/` | P10 |
| CI/CD ve deploy | `.github/workflows/`, `infra/` | P11 |
| Sistem entegrasyonu ve operasyon kanıtı | `tests/`, `docs/` | P12 |

Tüm model trafiği `IApimAiGatewayClient` üzerinden ortak APIM AI Gateway'e gider;
`Apim:RouteStyle` `AzureDeployments` deployment URL'si veya OpenAI v1 route'unu
seçer. Foundry'ye doğrudan trafik yasaktır. `GET /health` anonimdir ve AI/APIM
çağrısı yapmaz. Ayrıntılar: [spec dizini](spec/README.md),
[solution yapısı](spec/03-solution-yapisi.md), [runtime zinciri](spec/02-runtime-zinciri.md),
[API sözleşmesi](spec/11-api-sozlesmesi.md), [ADR'ler](adr/README.md).
