# API

Assessment endpoint'i `POST /api/ask`, anonim health endpoint'i `GET /health`'tir. API public olarak başka route açmaz. Model değerlendirmesi henüz etkin değildir; geçerli ask isteği sabit stub yanıtı ve `insufficient_evidence` döndürür.

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

`question` trim sonrası 3–2000 karakter olmalıdır. `repositoryUrl` verilmezse `Repository:DefaultUrl` kullanılır; URL HTTPS, izin verilen bir host üzerinde ve tam `/{owner}/{repo}` yolu olmalıdır. `ref` verilmezse `Repository:DefaultRef` kullanılır. `metrics` verilmezse stub değerlendirmesi m01–m10 sırasıyla döner; verilirse liste boş olamaz, yalnız m01–m10 değerlerini içermeli ve tekrar etmemelidir. Request gövdesi en fazla 32 KB'dır.

Yanıtın assessment kısmındaki tüm metrikler daima m01–m10 sırasındadır. Şimdiki stub için örnek:

```json
{
  "answer": "Değerlendirme motoru henüz etkin değil.",
  "answerType": "insufficient_evidence",
  "promptVersion": "stub",
  "evidence": [],
  "correlationId": "assessment-123",
  "assessment": {
    "metrics": [
      { "metricId": "m01", "metricName": "Kod ve Proje Yapısı Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m02", "metricName": "Kimlik Doğrulama ve Yetkilendirme", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m03", "metricName": "Uygulama Güvenliği ve Secret Yönetimi", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m04", "metricName": "Veri Yönetimi ve Entegrasyon Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m05", "metricName": "Loglama, İzlenebilirlik ve APM", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m06", "metricName": "Test ve Kod Kalitesi Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m07", "metricName": "CI/CD ve Kaynak Kod Yönetimi", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m08", "metricName": "Container ve Çalışma Ortamı Standartları", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m09", "metricName": "Performans, Dayanıklılık ve Ölçeklenebilirlik", "status": "Değerlendirilemedi", "score": null },
      { "metricId": "m10", "metricName": "Dokümantasyon ve Mimari Yönetişim", "status": "Değerlendirilemedi", "score": null }
    ],
    "overallScore": null
  }
}
```

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
| 401 | Kimlik yok/geçersiz (Entra auth P10 kapsamıdır) |
| 403 | Yetki yok (Entra auth P10 kapsamıdır) |
| 413 | Request body 32 KB sınırını aştı |
| 422 | Repository erişimi veya snapshot limiti hatası |
| 429 | Kimlik başına rate limit; `Retry-After` içerir |
| 500 | Beklenmeyen hata; iç detay içermez |
| 502 | APIM/model gateway hatası |
| 504 | Assessment timeout |

Request deadline 210 saniyedir. Azure App Service ön uç HTTP isteklerini yaklaşık 230 saniyede kesebileceğinden deadline bu sınırın altında tutulur.

El ile tutulan OpenAPI 3.1 belgesi: [openapi.json](openapi.json).
