# AI safety

| AURA kategorisi | Savunma | Uygulama | Test kanıtı |
|---|---|---|---|
| Toksisite | Sabit davranış politikası, Router erken reddi, çıktı blocklist'i | `prompts/system/safety-policy.md`, `Safety/RefusalBuilder.cs`, `Safety/OutputGuard.cs` | `RouterTests`, `RefusalBuilderTests`, `OutputGuardTests` |
| Prompt injection ve sızıntı | Input Guard sinyali, fail-closed Router, talimat hiyerarşisi, canary ve 12 kelimelik prompt pencereleri | `Safety/InputGuard.cs`, `Agents/AskRouterAgent.cs`, `Safety/OutputGuard.cs` | `InputGuardTests`, `RouterTests`, `OutputGuardTests` |
| RAG doğruluğu | Yalnız doğrulanmış kanıt, Output Guard ile doğrulanmamış referans temizliği | `Agents/SynthesizerAgent.cs`, `Safety/OutputGuard.cs` | `SynthesizerAgentTests`, `OutputGuardTests`, `AskOrchestratorTests` |
| Halüsinasyon | Kanıt yoksa `insufficient_evidence`, tahmin yerine sabit belirsizlik ifadesi | `Orchestration/AskOrchestrator.cs`, `Agents/SynthesizerAgent.cs` | `AskOrchestratorTests`, `SynthesizerAgentTests` |

## Router ve fail-closed akışı

Router niyetleri `assessment`, `repo_question`, `out_of_scope` ve `unsafe` değerleridir. `out_of_scope` ile `unsafe`, snapshot veya başka model çağrısı başlamadan sabit ret döndürür. Router timeout, gateway hatası veya geçersiz JSON üretirse Input Guard injection sinyali güvenli `unsafe` reddine dönüşür. Injection sinyali yoksa `metric-keywords.yaml` deterministik olarak yalnız eşleşen metrikleri seçer; eşleşme yoksa kapsam dışı ret döner. İstekte `metrics` verilmişse Router metrik önerisi değil istek seçimi kullanılır.

## Output Guard sırası

Output Guard yalnız `answer` ve `executiveSummary` metinlerine uygulanır: secret/PII maskeleme, canary denetimi, 12 kelimelik system-prompt sızıntı denetimi, çıktı blocklist'i ve doğrulanmamış `file:line` temizliği. `evidence[].snippet` ve assessment gövdesi sızıntı veya blocklist denetimine tabi değildir.

Canary `Safety:CanaryToken` App Setting'inde tutulur ve repository'ye yazılmaz. Değer yoksa instance açılışında kriptografik rastgele değer üretilir. `promptVersion` canary eklenmeden önce hesaplanır.

## AURA koşusu

AURA istemci kimliği organizasyon tarafından verildiğinde `RateLimit:ExemptClientIds` ile muaf tutulabilir; aksi halde varsayılan limit 300 istek/dk ve kuyruk 100'dür. `Cache:WarmupOnStartup=true`, varsayılan repository ve ref geçerli olmalı, App Service tek instance çalışmalıdır. Cache kararlılığı aynı instance ve TTL ile sınırlıdır; cache yenilendiğinde LLM bulguları değişebilir.
