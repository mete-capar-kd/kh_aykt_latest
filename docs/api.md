# API

Assessment endpoint'i `POST /api/ask`, anonim health endpoint'i `GET /health`'tir. API public olarak başka route açmaz. Ask isteği snapshot alır, profiler ve seçilen metrik evaluator'larını çalıştırır, puanı deterministik hesaplar ve doğrulanmış kanıtlardan yanıt üretir. APIM ve GitHub erişimi ortam yapılandırmasına bağlıdır.

## Assessment isteği

```http
POST /api/ask
Content-Type: application/json
X-Correlation-Id: assessment-123
```

```json
{
  "question": "Bu repository SSO gereksinimlerini karşılıyor mu?",
  "repositoryUrl": "https://github.com/org/repo",
  "ref": "main",
  "metrics": ["m02", "m03"]
}
```

`question` trim sonrası 3–2000 karakter olmalıdır. `repositoryUrl` verilmezse `Repository:DefaultUrl` kullanılır; URL HTTPS, izin verilen bir host üzerinde ve tam `/{owner}/{repo}` yolu olmalıdır. `ref` verilmezse `Repository:DefaultRef` kullanılır. `metrics` verilmezse tüm metrikler değerlendirilir; verilirse liste boş olamaz, yalnız m01–m10 değerlerini içermeli ve tekrar etmemelidir. Request gövdesi en fazla 32 KB'dır.

Yanıtta metrikler daima m01–m10 sırasındadır. `metrics: ["m01"]` ile gönderilen sentetik `clean-dotnet` fixture'ında README satırı doğrulandığında aşağıdaki gibi bir değerlendirme döner. Örnek okunabilirlik için metrik alanlarını kısaltır; tam alan listesi [OpenAPI](openapi.json) belgesindedir. Gerçek puan ve kanıtlar snapshot ile modelin doğrulanmış çıktısına bağlıdır:

```json
{
  "answer": "Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: uygulama sahibine ilişkin kanıt bulunamadı.",
  "answerType": "insufficient_evidence",
  "promptVersion": "<prompt-hash>",
  "evidence": [],
  "correlationId": "assessment-123",
  "assessment": {
    "jobId": "<job-id>",
    "repositoryUrl": "https://github.com/org/repo",
    "ref": "main",
    "commitSha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "metrics": [
      { "metricId": "m01", "metricName": "Kod ve Proje Yapısı Standartları", "status": "Kısmen Uyumlu", "score": 10.0, "coverage": "partial", "subChecks": [{ "id": "m01-sc01", "status": "Karşılandı", "reason": "README satırı incelendi.", "evidenceRefs": ["README.md#L1-L1"] }] },
      { "metricId": "m02", "metricName": "Kimlik Doğrulama ve Yetkilendirme", "status": "Değerlendirilemedi", "score": null, "notAssessableReason": "İstekte seçilmedi" },
      { "metricId": "m03", "metricName": "Uygulama Güvenliği ve Secret Yönetimi", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m04", "metricName": "Veri Yönetimi ve Entegrasyon Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m05", "metricName": "Loglama, İzlenebilirlik ve APM", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m06", "metricName": "Test ve Kod Kalitesi Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m07", "metricName": "CI/CD ve Kaynak Kod Yönetimi", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m08", "metricName": "Container ve Çalışma Ortamı Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m09", "metricName": "Performans, Dayanıklılık ve Ölçeklenebilirlik", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m10", "metricName": "Dokümantasyon ve Mimari Yönetişim", "status": "Değerlendirilemedi", "score": null }
    ],
    "overallScore": 10.0,
    "reportMarkdown": "| Metrik | Durum | Puan | Gerekçe | Risk | Dosya/Satır | Öneri |\n| --- | --- | --- | --- | --- | --- | --- |\n...",
    "generatedAt": "2025-01-01T00:00:00+00:00",
    "modelInfo": { "router": null, "profiler": "cheap", "evaluator": "cheap", "synthesizer": "strong", "promptVersion": "<prompt-hash>" }
  }
}
```

Seçilmemiş metrikler `Değerlendirilemedi`, null puan ve `İstekte seçilmedi` gerekçesiyle görünür; ortalamaya katılmaz. Yanıt ve Markdown tablosu aynı metriklerden üretilir. Kanıt linkleri değişebilir ref yerine çözümlenen commit SHA'ya sabitlenir. Sorulan dosya snapshot'ta yoksa dosya `bulunamadı` yanıtı ve on metrikli değerlendirme döner; dosyaya ilişkin model çağrısı yapılmaz. Örneğin `{"question":"MissingService.cs dosyası ne yapıyor?"}` için `answerType: "insufficient_evidence"` ve `"MissingService.cs bulunamadı."` ifadesi döner. `{"question":"uygulama sahibi kim?"}` için sahipliğe ilişkin doğrulanmış kanıt yoksa standart `"Bu soruyu repository'deki kanıtlarla cevaplayamıyorum: ..."` yanıtı döner. Güvenlik içerik filtresi synthesis sırasında tetiklenirse `answerType: "refusal"`, `evidence: []`, `assessment: null` döner.

## Health

```http
GET /health
```

```json
{
  "status": "Healthy",
  "version": "0.0.0-local",
  "commitSha": "local",
  "uptimeSeconds": 0
}
```

Health kontrolü anonimdir, rate limit'e tabi değildir, `Cache-Control: no-store` döner ve dış servislere çağrı yapmaz.

## Hatalar

Hata yanıtları `application/problem+json` biçiminde `type`, `title`, `status`, `instance` ve `correlationId` içerir. Validation hataları ayrıca `errors` alanını taşır. Internal exception mesajı veya stack trace yanıta eklenmez.

| HTTP | Anlamı |
|---|---|
| 400 | JSON veya request validation hatası |
| 401 | Kimlik yok/geçersiz; ayrıntılar için [SSO davranışı](sso.md#http-error-behavior) |
| 403 | Yetki yok; ayrıntılar için [SSO davranışı](sso.md#http-error-behavior) |
| 413 | Request body 32 KB sınırını aştı |
| 422 | Repository erişimi veya snapshot limiti hatası |
| 429 | Kimlik başına rate limit; `Retry-After` içerir |
| 500 | Beklenmeyen hata; iç detay içermez |
| 502 | APIM/model gateway hatası |
| 504 | Assessment timeout |

Request deadline 210 saniyedir. Azure App Service ön uç HTTP isteklerini yaklaşık 230 saniyede kesebileceğinden deadline bu sınırın altında tutulur.

El ile tutulan OpenAPI 3.1 belgesi: [openapi.json](openapi.json).
