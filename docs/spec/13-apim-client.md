# APIM client
Kaynak: [A17] — bu dosya bağlayıcı spec'tir.

**[A17] APIM client (`IApimAiGatewayClient`, typed HttpClient; tek model erişim noktası):**
- OpenAI uyumlu chat completions ve tool calling.
- Config: `Apim:BaseUrl`, `Apim:Auth:*` (organizasyon şemasına göre; değer yalnız secret'tan), `Apim:Deployments:Cheap` (Profiler/Evaluator) ve `Apim:Deployments:Strong` (Synthesizer).
- Retry: 408/429/5xx için toplam 3 deneme, exponential backoff + jitter, `Retry-After` dikkate alınır. Normal 4xx'te retry yok. Retry global deadline'ı aşamaz.
- Timeout'lar açıkça yapılandırılır:
  - Deneme başına timeout `Apim:AttemptTimeoutSeconds` (varsayılan 60 sn).
  - Toplam süre, çağıranın metrik/global deadline `CancellationToken`'ıyla sınırlanır.
  - `HttpClient.Timeout = Timeout.InfiniteTimeSpan`; sınırı handler ve token belirler.
  - `AddStandardResilienceHandler()` varsayılanlarıyla (10 sn deneme / 30 sn toplam) **kullanılmaz**; kullanılırsa bu değerlerle yapılandırılır.
- Authorization header'ı ve istek/yanıt gövdesi loglanmaz.
- Token usage (input/output/cached) normalize edilir; yanıtta yoksa null kalır, değer uydurulmaz.
- **Semantic cache dostu:** system prompt'lar sabit ve deterministiktir (timestamp, GUID, rastgele sıra yok); değişken kısım yalnız user mesajındadır. `promptVersion` = prompt dosyalarının hash'i.
