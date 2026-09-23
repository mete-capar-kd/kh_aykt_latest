# Ürün ve kapsam
Kaynak: [A1], [A2] — bu dosya bağlayıcı spec'tir.

**[A1] Ürün:** Bir GitHub repository'sinin kodunu, promptlarını, dokümanlarını, workflow ve config dosyalarını K2'nin 10 metriğine göre değerlendiren, her bulgu için dosya/satır kanıtı üreten AI ajanı.

**[A2] Tek repository, tek deploy edilen .NET 10 ASP.NET Core Web API, tek business endpoint:**
- `POST /api/ask` — işlevsel assessment endpoint'i (K1 s.5 §9).
- `GET /health` — altyapı istisnası.
- Başka public route (`/assess`, `/scan`, `/metrics`, `/cache`, `/pii` vb.) açılmaz. Analiz, AI orkestrasyonu, Gateway client'ı ve telemetry internal servistir.
- Microservice, worker, queue, veritabanı, dağıtık cache veya ek deployment yoktur. Persistence gerekmez; EF Core/DbContext/migration **eklenmez** (YAGNI). Tek istisna, `docs/spec/15-aura-guvenlik.md` 5. maddede ([A19]) tanımlanan **process içi** cache'tir (tamamlanan metrik sonuçları ve snapshot'lar `IMemoryCache`'te, süren hesaplamalar `ConcurrentDictionary`'de); dağıtık değildir.
