# Solution yapısı
Kaynak: [A4] — bu dosya bağlayıcı spec'tir.

**[A4] Solution yapısı (Tasarım kararı):**

```
Hackathon.Assessment.slnx
Directory.Build.props            (net10.0, Nullable enable, TreatWarningsAsErrors, AnalysisLevel latest,
                                  NuGetAudit + NuGetAuditMode=all, NU1901–NU1904 WarningsAsErrors)
Directory.Packages.props         (Central Package Management, sürümler sabit)
global.json                      (.NET 10 SDK, rollForward: latestFeature)
.editorconfig  .gitattributes  .gitignore  .dockerignore  Dockerfile  README.md
src/
  Hackathon.Assessment.Api/            ← TEK uygulama projesi (tek .csproj, tek deployable)
    Hackathon.Assessment.Api.csproj
    Program.cs
    Endpoints/        AskEndpoints.cs, HealthEndpoints.cs
    Contracts/        AskRequest, AskResponse (API DTO'ları)
    Auth/             Entra ID JWT, authorization policy
    Middleware/       correlation ID, ProblemDetails / exception handler
    Domain/           modeller, enum'lar, doğrulama kuralları (framework bağımlılığı yok)
    Orchestration/    AskOrchestrator, deadline yönetimi
    Agents/           AskRouterAgent, ProfilerAgent, MetricEvaluator, SynthesizerAgent
    Tools/            6 read-only tool, dispatcher, EvidenceLedger, record_finding
    Scoring/          deterministik puanlama
    Reporting/        JSON + Markdown rapor
    Snapshot/         repo indirme, filtreleme, in-memory index
    Scanners/         Generic/, DotNet/, Python/, Node/
    Ai/               ApimAiGatewayClient, credential provider
    Telemetry/        OpenTelemetry / Azure Monitor
    Masking/          ISecretMasker
    Safety/           InputGuard, OutputGuard, RefusalBuilder (model çağırmaz)
    Caching/          metrik sonuç cache'i (tamamlanan sonuç IMemoryCache + süren işler ConcurrentDictionary)
    Options/          Options sınıfları + doğrulama
    Health/           HealthProbe (container HEALTHCHECK modu)
prompts/
  system/  router.md  profiler.md  evaluator.md  synthesizer.md  safety-policy.md  refusal-templates.md
  guard/   injection-patterns.txt  metric-keywords.yaml  output-blocklist.txt
  metrics/ m01.md … m10.md
  rubrics/ m01.yaml … m10.yaml
tests/
  Hackathon.Assessment.Tests/          ← TEK test projesi (.NET'te testler ayrı .csproj ister)
    Unit/             her klasör için unit testler
    Integration/      WebApplicationFactory testleri
    Fixtures/         mini sentetik repo'lar
    GatewayLive/      canlı APIM doğrulamaları ([Trait("Category","GatewayLive")]; CI'da hariç)
docs/
  architecture.md  api.md  tools.md  scoring.md  prompts.md  sso.md  observability.md
  azure-architecture.md  cicd.md  security.md  operations.md  evidence-matrix.md  ai-safety.md  adr/
.github/  copilot-instructions.md  pull_request_template.md  ISSUE_TEMPLATE/  workflows/
```

P11'de Azure kaynakları Bicep yerine portalda ekip tarafından kurulur;
kurulum ve doğrulama kontrol listesi `docs/azure-architecture.md` içindedir.

- **Solution'da yalnız iki proje vardır:** `src/Hackathon.Assessment.Api` (uygulama) ve `tests/Hackathon.Assessment.Tests` (test). Ek class library, ek uygulama veya ek test projesi **açılmaz**. Katman ayrımı proje ile değil, klasör ve namespace ile yapılır (`Hackathon.Assessment.Api.Domain`, `...Agents`, `...Ai` vb.).
- Klasör bağımlılık kuralı yasak referans listesi olarak tanımlanır:
  - `...Domain` → `Microsoft.AspNetCore.*` ve diğer hiçbir kardeş namespace'e referans veremez.
  - `...Endpoints` → `...Ai`, `...Agents`, `...Snapshot`, `...Scanners`, `...Tools` namespace'lerine referans veremez.
  - `...Orchestration` → `System.Net.Http` ve `...Ai` implementasyonlarına referans veremez (model erişimi yalnız `Agents` üzerinden `Ai` arayüzüyle).
  - `HttpClient` yalnız `...Ai`, `...Snapshot` ve `...Health` (yalnız container `HealthProbe`'u) namespace'lerinde kullanılabilir.
  - `IApimAiGatewayClient`'a yalnız `...Agents` namespace'i referans verebilir. `...Safety` ve `...Caching` model çağırmaz ve `...Ai`'ye referans veremez.
- Bu kurallar **NetArchTest.Rules** ile (IL tabanlı) architecture unit testinde doğrulanır.
