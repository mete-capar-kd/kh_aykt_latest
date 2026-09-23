# ADR 0002: AURA güvenliği ve güvenilirliği

## Context

AURA `/api/ask` endpoint'ine binlerce zorlayıcı istek gönderecek ve yanıtları LLM-as-a-Judge ile değerlendirecektir. Yalnız prompt talimatlarına güvenmek toksisite, prompt injection, kanıtsız cevap ve hassas veri sızıntısı risklerini karşılamaz. AURA'nın kimlik doğrulaması ve varsayılan repository bilgisi organizasyon tarafından sağlanacaktır.

## Decision

Birbirini tamamlayan deterministik ve model tabanlı savunmalar uygulanır. Uygulama ve sahip issue'ları:

| AURA kategorisi | Savunma | Sahip issue |
|---|---|---|
| Toksisite | Router/Synthesizer için sabit profesyonel dil politikası, güvenli sabit ret, içerik filtresi yanıtını 200 refusal'a çevirme ve kod cevabına uygulanmayan çıktı blocklist'i | P09 |
| Prompt injection | Normalize eden Input Guard, şüpheli içerik sinyali, kullanıcı/repository verisini sınırlandırılmış bloklarda sunma, Router ile erken ret ve read-only snapshot araçları | P09 |
| RAG doğruluğu | Yalnız doğrulanmış bulgu ve kapsam kanıtlarından synthesis, EvidenceLedger doğrulaması ve `ISecretMasker` | P05, P08, P09 |
| Halüsinasyon kontrolü | Kanıt yoksa `insufficient_evidence` / `Değerlendirilemedi`, doğrulanmayan dosya/satır iddialarını çıkarma ve tahmin yasağı | P08, P09 |
| Kararlılık, hız ve maliyet | Başarılı metrik sonuçları için TTL'li process içi cache, single-flight, deadline, eşzamanlılık ve rate limit | P03, P07, P08 |
| İzlenebilirlik | Correlation ID ve içeriksiz güvenlik sayaçları; prompt, soru, cevap, PII veya secret telemetry'ye yazılmaz | P06, P09 |

Ask Router gereklidir; repo içeriğini görmeden niyeti sınıflandırır, güvenli olmayan/kapsam dışı isteği erken kapatır ve belirsiz Router sonucunda güvenli, deterministik fallback sağlar. Router yetki vermez, tool kullanmaz ve repo değerlendirmesini başlatmaya tek başına izin vermez.

Canary token repo'da tutulmaz; App Setting'den gelir veya açılışta üretilir. Router ve Synthesizer system prompt'larına yalnız yükleme anında eklenir. `promptVersion`, canary eklenmeden önceki sabit prompt dosyalarından hesaplanır; Output Guard yalnız kullanıcıya görünen `answer` metnini kontrol eder. Bu, canary'nin kod kanıtı olarak dönen snippet'leri yanlışlıkla engellemesini önler.

Kimlik, APIM, AURA client ID ve varsayılan repository gibi kurumsal değerler uydurulmaz; dış önkoşul olarak belirtilir. Bu ADR tek başına bir savunmanın yeterli olduğunu iddia etmez.

## Consequences

Savunma katmanları ölçülebilir ve test edilebilir; ret ve sızıntı denetimleri telemetry'de yalnız içeriksiz sayaç üretir. Router erken retleri maliyet ve gecikmeyi azaltır ancak güvenlik sınırı değildir; diğer guard'lar ve kanıt doğrulaması yine zorunludur. Canary ve prompt hash'inin ayrılması semantic cache anahtarlarının deploy boyunca deterministik kalmasına yardımcı olur. AURA canlı bağlantısı kurumsal kimlik ve repository ayarları gelene kadar blokerdir.
