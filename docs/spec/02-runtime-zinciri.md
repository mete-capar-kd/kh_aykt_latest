# Runtime zinciri
Kaynak: [A3] — bu dosya bağlayıcı spec'tir.

**[A3] Runtime zinciri (değişmez):**

```
Client / Değerlendirici
  → Microsoft Entra ID (SSO) bearer token
  → API: JWT authentication + authorization (başarısızsa 401/403; repo/model/APIM çağrısı BAŞLAMAZ)
  → rate limit (kimlik başına)
  → POST /api/ask
  → 1. Input Guard (deterministik: normalizasyon, uzunluk, injection işaretleri)
  → 2. Ask Router (ucuz model; niyet: assessment | repo_question | out_of_scope | unsafe)
        out_of_scope / unsafe → sabit güvenli ret yanıtı (snapshot/evaluator/Synthesizer çalışmaz) → 5. adım
  → 3. internal orkestrasyon: snapshot → Profiler → gerekli Evaluator'lar paralel [metrik sonuç cache'i]
        → deterministik puan → Synthesizer
  → 4. Output Guard (deterministik: maskeleme, canary/system prompt sızıntısı, kanıt doğrulama, çıktı blocklist'i)
  → 5. yanıt

Her model çağrısı (Router, Profiler, Evaluator, Synthesizer) 2. ve 3. adımlarda şu yolu izler:
  IApimAiGatewayClient → Organizasyonun APIM AI Gateway endpoint'i (organizasyonun verdiği caller kimlik doğrulaması)
  → APIM: kimlik doğrulama + PII/secret politikası + semantic cache → Azure AI Foundry model deployment → APIM → uygulama
```

- Foundry'ye doğrudan çağrı, Foundry SDK'sının Foundry endpoint'iyle kullanılması veya APIM'i bypass eden herhangi bir yol **yasaktır**.
- Gelen SSO kimliği ile giden APIM kimlik doğrulaması ayrı konulardır. Kullanıcının bearer token'ı APIM'e iletilmez.
- APIM auth şeması (subscription key header adı, managed identity/OAuth scope vb.), tenant, issuer, audience, rol ve scope değerleri organizasyondan gelir. **Uydurma**; `<ORGANİZASYONDAN-ALINACAK>` placeholder'ı kullan ve "External blocker" olarak yaz.
