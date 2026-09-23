# ADR 0001: Uygulama mimarisi

## Context

Hackathon ürünü tek bir GitHub repository'sini analiz eden, kanıta dayalı cevaplar veren .NET Web API olmalıdır. K1 ve K2 gereksinimleri; çalışma zamanı sınırlarını, tek iş endpoint'ini, güvenlik katmanlarını, süre hedeflerini ve dokümantasyonun yeniden kullanımını belirler. Issue taslağındaki branch/PR talimatı K1 s.2 §4 ile çelişir.

## Decision

- .NET 10 ASP.NET Core Web API tek deployable ve tek uygulama projesidir; tek test projesi dışında yeni `.csproj` açılmaz. Katmanlar klasör/namespace ile ayrılır.
- Tek business endpoint `POST /api/ask`; yalnız altyapı istisnası `GET /health`'tir.
- Dört LLM rolü Ask Router, Repo Profiler, Metric Evaluator ve Report Synthesizer'dır. Orkestratör LLM çağırmaz; puan deterministik hesaplanır.
- Global deadline 210 saniyedir; App Service ön uç sınırı yaklaşık 230 saniye olduğundan timeout'lar bu üst sınıra göre yapılandırılır.
- Persistence, EF Core ve veritabanı yoktur. Tamamlanan metrik sonuçları ve snapshot'lar için yalnız process içi cache kullanılır.
- `standardRef`, Holding standardı kataloğu sağlanana kadar `UseCase §6.x — <alt kontrol>` biçimini kullanır; verilmemiş madde numaraları uydurulmaz.
- `.github/copilot-instructions.md` kısa ve sabittir; ayrıntılar issue'ların seçtiği `docs/spec/` dosyalarındadır.
- K1 s.2 §4, taslak A21'e göre önceliklidir: issue dalı `development`'tan açılır, agent aynı issue dalında çalışır ve draft PR'ı `development`'a açar. `main`'e doğrudan push/PR yoktur. Bu repo görevi için PR hedefi `development`'tır.

## Consequences

Tek deployable operasyonu basitleştirir ve proje sınırlarını korur; process içi cache instance ölçeğinde kalır ve kalıcılık sağlamaz. 210 saniyelik süre dış servislerin toplam gecikmesine az pay bırakır. Deterministik puanlama aynı bulgular için tekrarlanabilir sonuç verir; model değerlendirmeleri cache TTL ve instance ömrüyle sınırlı kararlılık sağlar. Ayrıntıların spec dosyalarında tutulması sonraki issue'ların yalnız gereken bağlamı okumasını sağlar. A21'in branch akışı K1'e göre düzeltilmiştir; insan incelemesi ve merge adımı korunur.
