# Hackathon Assessment API

Bir GitHub repository'sindeki kodu, promptları, dokümanları, workflow ve config dosyalarını K2'nin 10 mimari metriğine göre değerlendirip her bulguya doğrulanmış dosya/satır kanıtı bağlayan .NET 10 Web API.

## Mimari

İstekler Entra ID JWT doğrulaması, Input Guard ve Ask Router'dan geçer. İzinli akış snapshot/profiler, izole Metric Evaluator'lar, deterministik puanlama ve Report Synthesizer kullanır; Output Guard yanıtı maskeleyip kanıtı doğrular. Her model çağrısı `IApimAiGatewayClient` üzerinden organizasyonun APIM AI Gateway'ine gider.

- [Mimari diyagramı ve bileşen sahipliği](docs/architecture.md)
- [Bağlayıcı mimari spec'leri](docs/spec/README.md)
- [ADR kayıtları](docs/adr/README.md)
- [Config anahtarları](docs/spec/16-config.md)
- [API sözleşmesi ve örnek yanıt](docs/api.md)
- [K1/K2/K3 kanıt matrisi](docs/evidence-matrix.md)
- [K1 release checklist](docs/release-checklist.md)

## Teknoloji envanteri

Paket adları ve sürümleri `src/Hackathon.Assessment.Api.csproj`,
`tests/Hackathon.Assessment.Tests.csproj` ve `Directory.Packages.props` ile
eşleşir.

| Uygulama/test paketi | Sürüm | Kullanım |
|---|---:|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.12 | Entra ID JWT bearer doğrulaması |
| `Azure.Monitor.OpenTelemetry.AspNetCore` | 1.6.0 | OpenTelemetry / Application Insights export |
| `YamlDotNet` | 18.1.0 | Prompt rubric ve guard YAML okuma |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.0 | API integration test host'u |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | Test çalıştırıcısı |
| `xunit` | 2.9.3 | Test framework'ü |
| `coverlet.collector` | 6.0.4 | Test coverage toplama |
| `NetArchTest.Rules` | 1.3.2 | Katman bağımlılık testleri |
| `NSubstitute` | 5.3.0 | Test doubles |
| `xunit.runner.visualstudio` | 3.1.1 | Visual Studio / `dotnet test` adapter'ı |

Uygulama altyapısı organizasyonun hazırladığı Azure hizmetlerini kullanır;
bu tablo NuGet paket listesi değildir.

| Altyapı | Kullanım |
|---|---|
| .NET 10 / ASP.NET Core | Tek Web API deployable |
| Azure App Service / ACR | Container barındırma ve imaj kaynağı |
| Azure API Management / Azure AI Foundry | Tek model erişim yolu ve APIM arkasındaki deployment'lar |
| Application Insights | İçeriksiz OpenTelemetry telemetry'si |
| GitHub Actions | Build, test, image ve onaylı deployment workflow'ları |

## Yerel çalıştırma

1. .NET 10 SDK kurun ve bağımlılıkları geri yükleyin:

  ```sh
  dotnet restore Hackathon.Assessment.slnx
  dotnet user-secrets init --project src/Hackathon.Assessment.Api
  ```

2. Zorunlu organizasyon değerlerini [config kataloğundan](docs/spec/16-config.md)
  edinin; APIM, Entra ve Azure değerlerini tahmin etmeyin. Yerel secret'ları
  User Secrets veya git'e girmeyen `appsettings.Local.json` ile sağlayın.
3. API'yi başlatın:

  ```sh
  dotnet run --project src/Hackathon.Assessment.Api
  ```

`.env.example` yalnız anahtar şablonudur; ASP.NET Core `.env` dosyasını
otomatik yüklemez. Gerçek secret, token veya kişisel veri repository'ye
eklenmemelidir.

## Test ve kalite kontrolleri

```sh
dotnet build -warnaserror
dotnet test --filter "Category!=GatewayLive"
dotnet format --verify-no-changes --exclude tests/Hackathon.Assessment.Tests/Fixtures
```

Gateway/AURA canlı kontrolleri varsayılan CI testinden hariçtir. Yalnız onaylı
`verification` ortamında [gateway-verification workflow'u](.github/workflows/gateway-verification.yml)
`workflow_dispatch` ile çalıştırılır; testler Gateway kanıtı yerine uygulama
maskelemesini kullanmaz.

## Deployment, SSO ve AI güvenliği

- Portalda hazırlanan Azure kaynakları, CD ve rollback: [Azure mimarisi](docs/azure-architecture.md), [operasyonlar](docs/operations.md) ve [CI/CD](docs/cicd.md).
- Entra ID JWT, yetkilendirme ve canlı SSO kanıtı: [SSO](docs/sso.md).
- Guard'lar, veri güven sınırı ve AURA kanıt durumu: [AI safety](docs/ai-safety.md).

## Sahiplik, destek ve operasyon

- Uygulama sahibi: `NOT_EVIDENCED — doldurulacak`
- Destek modeli / iletişim kanalı: `NOT_EVIDENCED — doldurulacak`
- Operasyonel sorumluluk ve nöbet: `NOT_EVIDENCED — doldurulacak`
- Azure subscription, tenant, APIM endpoint ve deployment adları: `<ORGANİZASYONDAN-ALINACAK>`

Bu alanlar doğrulanmış repository kanıtı veya organizasyon bilgisi olmadan doldurulmaz.

## Sınırlar

- Public business route yalnız `POST /api/ask`; anonim altyapı route'u yalnız
  `GET /health`'tir.
- Repository araçları read-only'dir. Model trafiği Foundry'ye doğrudan değil,
  yalnız APIM AI Gateway üzerinden gider.
- Veritabanı, EF Core ve kalıcı uygulama verisi yoktur; izin verilen tek cache
  process içi snapshot/metrik cache'idir.
- Azure/Entra/APIM değerleri, canlı AURA sonucu, Gateway PII/cache gözlemi ve
  resmî Copilot kullanım raporu organizasyondan alınmadan kanıtlanmış sayılmaz.
