# Mimari genel bakış
```mermaid
sequenceDiagram
    participant C as Client / Değerlendirici
    participant E as Entra ID
    participant API as API /api/ask
    participant G as Input Guard
    participant R as Ask Router
    participant O as Orkestratör
    participant P as Repo Profiler
    participant M as Metric Evaluator ×10
    participant S as Report Synthesizer
    participant OG as Output Guard
    participant APIM as Organizasyon APIM AI Gateway
    participant F as Azure AI Foundry
    C->>E: SSO bearer token
    E-->>C: JWT
    C->>API: POST /api/ask + JWT
    API->>G: Doğrulanmış istek
    G->>R: Normalize soru
    R->>APIM: Router çağrısı (IApimAiGatewayClient)
    APIM->>F: Yetkili model isteği
    F-->>APIM: Router sonucu
    APIM-->>R: Router sonucu
    Note over R,S: Tüm roller IApimAiGatewayClient → APIM → Foundry kullanır; doğrudan Foundry çağrısı yasaktır
    R->>O: İzinli niyet / metrikler
    O->>P: Snapshot özeti
    P->>APIM: Profiler çağrısı
    APIM-->>P: Profil
    O->>M: İzole metrik değerlendirmeleri
    M->>APIM: Evaluator çağrıları
    APIM-->>M: Bulgular
    O->>S: Doğrulanmış bulgular + deterministik puan
    S->>APIM: Synthesis çağrısı
    APIM-->>S: Yanıt
    S->>OG: Yanıt + kanıtlar
    OG-->>API: Maskelenmiş ve doğrulanmış çıktı
    API-->>C: Assessment yanıtı
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

Bağlayıcı ayrıntılar: [spec dizini](spec/README.md), [solution yapısı](spec/03-solution-yapisi.md), [runtime zinciri](spec/02-runtime-zinciri.md), [API sözleşmesi](spec/11-api-sozlesmesi.md), [ADR'ler](adr/README.md).
