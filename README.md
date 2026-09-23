# Hackathon Assessment API

Bir GitHub repository'sindeki kodu, promptları, dokümanları, workflow ve config dosyalarını mimari metriklere göre değerlendirip bulgular için dosya/satır kanıtı üreten .NET 10 Web API.

## Mimari

İstekler Entra ID doğrulamasından, Input Guard ve Ask Router'dan geçer; izinli assessment akışı snapshot, profiler, izole metric evaluator'lar, deterministik puanlama ve synthesizer kullanır. Her model çağrısı organizasyon APIM AI Gateway üzerinden yapılır. Public iş endpoint'i `POST /api/ask`, altyapı endpoint'i `GET /health`'tir.

- [Mimari diyagramı ve bileşen sahipliği](docs/architecture.md)
- [Bağlayıcı mimari spec'leri](docs/spec/README.md)
- [ADR kayıtları](docs/adr/README.md)
- [Config anahtarları](docs/spec/16-config.md)

## Teknoloji envanteri

| Teknoloji | Kullanım |
|---|---|
| .NET 10 / ASP.NET Core | Tek Web API deployable |
| Azure App Service | Uygulama barındırma |
| Azure Container Registry (ACR) | Container imajı |
| Azure API Management (APIM) | Tek AI Gateway ve model erişim yolu |
| Azure AI Foundry | APIM arkasındaki model deployment'ları |
| Application Insights | OpenTelemetry tabanlı uygulama telemetry'si |
| GitHub Actions | Build, test, kalite ve güvenlik otomasyonu |

## Yerel çalıştırma

1. .NET 10 SDK kurun.
2. `.env.example` içindeki secret olmayan örnek anahtarları yerel App Settings veya User Secrets ile sağlayın; gerçek secret'ları repoya commit etmeyin.
3. `docs/spec/16-config.md`'deki zorunlu organizasyon değerlerini edinin; bunları tahmin etmeyin.
4. `dotnet run --project src/Hackathon.Assessment.Api`

Yerel ayar örneği `.env.example` içindedir; ASP.NET Core `.env` dosyasını otomatik yüklemez. Secret'ları `.NET user-secrets` veya git'e girmeyen `appsettings.Local.json` ile sağlayın.

## Sahiplik, destek ve operasyon

- Uygulama sahibi: `NOT_EVIDENCED — doldurulacak`
- Destek modeli / iletişim kanalı: `NOT_EVIDENCED — doldurulacak`
- Operasyonel sorumluluk ve nöbet: `NOT_EVIDENCED — doldurulacak`
- Azure subscription, tenant, APIM endpoint ve deployment adları: `<ORGANİZASYONDAN-ALINACAK>`

Bu alanlar doğrulanmış repository kanıtı veya organizasyon bilgisi olmadan doldurulmaz.
