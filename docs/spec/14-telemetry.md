# Telemetry
Kaynak: [A18] — bu dosya bağlayıcı spec'tir.

**[A18] Telemetry (K1 s.5):**
- Azure Monitor OpenTelemetry (`Azure.Monitor.OpenTelemetry.AspNetCore`) ile HTTP request, dependency, exception ve performans telemetrisi.
- Her model çağrısı için: deployment/model, input/output/cached token, latency, status, correlationId, metricId, retry sayısı, cache durumu (yalnız Gateway gerçekten bildiriyorsa; header adı uydurulmaz).
- Resource attribute'ları: team, application, environment, version, commitSha.
- **Prompt, model cevabı, soru gövdesi, Authorization/cookie, PII ve secret telemetry'ye yazılmaz.**
- Connection string yalnız App Service configuration'dan gelir.
- Telemetry hatası isteği bozmaz.
- W3C trace context APIM'e propagate edilir.
- Azure Monitor exporter **yalnız connection string tanımlıysa** kaydedilir; test ve lokal ortamda uygulama exporter'sız açılır.
