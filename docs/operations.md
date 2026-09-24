# Portal kurulum ve production operasyonları

Kaynaklar portalda elle oluşturulur; workflow kaynak oluşturmaz. Organizasyon
önkoşulları: resource group/RBAC, bölge ve SKU, ACR ve Web App adları, model
deployment/kota, ortak APIM erişimi, Entra API audience, App Insights
connection string ve OIDC yetkileridir. Gerçek secret, token veya connection
string dosyalara yazılmaz. Bkz. [Azure mimarisi](azure-architecture.md).

## App Service → Environment variables / App Settings

`docs/spec/16-config.md` tek config kaynağıdır. ASP.NET Core bölüm ayırıcısı
`__` olarak girilir. `Apim__Auth__Key`, `Repository__GitHubToken`,
`Safety__CanaryToken`, `APPLICATIONINSIGHTS_CONNECTION_STRING` yalnız
portalın güvenli App Settings alanından sağlanır; deploy workflow'u bunları
okumaz ve yalnız `APP_VERSION` ile `GIT_COMMIT_SHA` değerlerini günceller.
APIM şeması ManagedIdentity/OAuth ise ilgili token ve scope yöntemi
organizasyonca ayrıca doğrulanmalıdır (mevcut uygulama desteklemediği şemada
bilinçli olarak hata verir).

| App Setting | Secret? | Kaynak / değer |
|---|---|---|
| `APP_VERSION`, `GIT_COMMIT_SHA` | Hayır | CD run number ve merge commit SHA |
| `WEBSITES_PORT` | Hayır | `8080` (portal) |
| `Apim__BaseUrl`, `Apim__RouteStyle`, `Apim__ApiVersion` | Hayır | Ortak APIM ekibi; route stiline göre sürüm |
| `Apim__CacheStatusHeader` | Hayır | Yalnız APIM doğrulanmış header adını bildirirse; aksi halde boş |
| `Apim__Auth__Scheme`, `Apim__Auth__HeaderName`, `Apim__Auth__Scope` | Hayır | APIM ekibinin doğruladığı yöntem/header/scope |
| `Apim__Auth__Key` | **Evet** | APIM ekibinin sağladığı key; yalnız gereken şemada |
| `Apim__Deployments__Cheap`, `Apim__Deployments__Strong` | Hayır | APIM üzerinden model deployment adları |
| `Apim__AttemptTimeoutSeconds`, `Apim__MaxAttempts` | Hayır | Config varsayılanları: `60`, `3` |
| `EntraId__Instance`, `EntraId__TenantId`, `EntraId__Audience` | Hayır | `https://login.microsoftonline.com/`, Entra tenant ve API audience |
| `EntraId__RequiredRoles__0`, `EntraId__RequiredScopes__0` | Hayır | Entra API politikası; varsayılan boş |
| `Repository__DefaultUrl`, `Repository__DefaultRef` | Hayır | Organizasyonun değerlendirme reposu, varsayılan ref `main` |
| `Repository__AllowedInputHosts__0` | Hayır | Varsayılan `github.com` |
| `Repository__AllowedDownloadHosts__0`, `Repository__AllowedDownloadHosts__1` | Hayır | Varsayılan `api.github.com`, `codeload.github.com` |
| `Repository__GitHubToken` | **Evet** | Opsiyonel salt-okunur private-repo token |
| `Assessment__MaxFiles`, `Assessment__MaxTotalBytes` | Hayır | Varsayılan `2000`, `52428800` |
| `Assessment__RouterTimeoutSeconds`, `Assessment__ProfilerTimeoutSeconds`, `Assessment__MetricTimeoutSeconds`, `Assessment__SynthesizerTimeoutSeconds`, `Assessment__GlobalTimeoutSeconds` | Hayır | Varsayılan `4`, `20`, `140`, `40`, `210` |
| `Assessment__MaxConcurrentFullAssessments` | Hayır | Varsayılan `2` |
| `Cache__MetricResultTtlMinutes`, `Cache__MetricResultMaxEntries`, `Cache__SnapshotMaxBytes` | Hayır | Varsayılan `120`, `1000`, `536870912` |
| `Cache__WarmupOnStartup` | Hayır | Production `true`; test ortamında `false` |
| `RateLimit__PermitsPerMinute`, `RateLimit__QueueLimit`, `RateLimit__ExemptClientIds__0` | Hayır | Varsayılan `300`, `100`, boş; exemption yalnız onaylı client ID |
| `Safety__CanaryToken` | **Evet** | Portalda deploy'a özel rastgele değer; yoksa uygulama açılışta üretir |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | **Evet** | Workspace-based Application Insights connection string |
| `Telemetry__Team`, `Telemetry__Application`, `Telemetry__Environment`, `Telemetry__SamplingRatio` | Hayır | Organizasyon team; `hackathon-assessment-api`, `production`, `1.0` |

Portaldaki image ayarları: ACR login server,
`hackathon-assessment-api:<commit SHA>`, system-assigned identity ile
AcrPull (`acrUseManagedIdentityCreds`), Always On, `/health`, HTTPS only ve
tek instance. `APP_VERSION` ve `GIT_COMMIT_SHA` container ENV olarak da
build edilir, fakat portal App Settings CD sırasında güncellenir.

## GitHub production environment ve OIDC

`Settings → Environments → production` için insan onayı ve branch restriction
olarak `main` tanımlayın. Bu environment'a aşağıdaki **vars** girilir; repo
secret olarak uzun ömürlü Azure client secret girilmez:

| Variable | Kaynak |
|---|---|
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | OIDC uygulama kaydı ve abonelik |
| `AZURE_RESOURCE_GROUP` | Portalda hazırlanmış resource group |
| `ACR_NAME` | Portalda hazırlanmış ACR kısa adı (`.azurecr.io` olmadan) |
| `WEBAPP_NAME` | Portalda hazırlanmış Web App kısa adı |
| `API_AUDIENCE` | Entra API resource/audience; yoksa `/api/ask` smoke uyarı ile atlanır |
| `APP_VERSION_PREFIX` | Onaylanan sürüm öneki, ör. ekip sürüm etiketi |

Entra uygulama kaydında GitHub Actions federated credential oluşturun:
issuer `https://token.actions.githubusercontent.com`, subject
`repo:<org>/<repo>:environment:production`, audience
`api://AzureADTokenExchange`. Bu kimliğe mevcut resource group/App Service
üzerinde container+app settings güncelleme ve ACR üzerinde push yetkisi
tanımlayın. App Service'in **ayrı** system-assigned identity'sine ACR
üzerinde AcrPull atayın. Azure login OIDC'dir; production dışı job'lara
`id-token: write` verilmez.

## Deploy ve rollback

`development → main` insan onaylı merge'i CD'yi tetikler; CI ve Trivy geçerse
image SHA ile etiketlenir, ACR'a push edilir. Job summary'de `Image` ve
`Digest` kaydedilir. ACR'da tag → digest'i ve App Service container image'ının
`<acr>.azurecr.io/hackathon-assessment-api:<SHA>` olduğunu doğrulayın.
`https://<webapp>.azurewebsites.net/health` yanıtı `commitSha == SHA`
olmalıdır. `/api/ask` başarılıysa yalnız HTTP status ve `answerType` loglanır;
App Insights'ta request/dependency/token telemetrisi kontrol edilir,
soru/prompt/cevap metni bulunmamalıdır.

Rollback: Önceki başarılı run'ın SHA'sını ve ACR'da image tag/digest'ini
doğrulayın. Yetkili operatör mevcut resource group ve Web App için
`az webapp config container set -g <resource-group> -n <webapp> --container-image-name <acr>.azurecr.io/hackathon-assessment-api:<previous-sha>`
komutunu uygular; `APP_VERSION` ve `GIT_COMMIT_SHA` App Settings'ini önceki
image ile uyumlu hale getirir. `/health` SHA'sını yeniden doğrular. İşlem
önceki başarılı container'ı geri getirir, kaynakları yeniden oluşturmaz.
`/api/ask` smoke, organizasyon API audience/kimliği yoksa canlı kanıt sayılmaz.

## Onaylı Gateway doğrulaması

[`gateway-verification.yml`](../.github/workflows/gateway-verification.yml)
yalnız `workflow_dispatch` ile ve `verification`
environment onayı altında çalışır. Workflow, OIDC ile Azure'a giriş yapıp
`API_AUDIENCE` için kısa ömürlü `API_TOKEN` alır; token log'a veya artifact'e
yazılmaz. `API_BASE_URL` (HTTPS), `APIM_BASE_URL`, `APIM_ROUTE_STYLE`,
`APIM_API_VERSION`, `APIM_AUTH_SCHEME`, `APIM_AUTH_HEADER_NAME`,
`APIM_DEPLOYMENT_CHEAP` ve `APIM_CACHE_STATUS_HEADER` environment variable
olarak sağlanır. Canlı direct-Gateway credential'ı `APIM_TEST_KEY`, yalnız
sentetik PII girdileri `PII_TEST_INPUTS_JSON` environment secret'ıdır.

`PII_TEST_INPUTS_JSON` JSON array'inde her nesne `kind` (`email`, `phone`,
`tckn` veya `secret-like`) ve `value` taşır; her tür için organizasyonun
ürettiği sentetik bir değer bulunmalıdır. Bu değerler repoya veya rapora
kopyalanmaz. Testler uygulamanın `ISecretMasker` katmanını atlayıp APIM'e
doğrudan gider. PII sonucu ham girdinin APIM cevabında bulunmadığını kontrol
eder; secret-benzeri girdi 4xx ile engellenmeli veya `***MASKED***` ile
maskelenmelidir. Gateway log/telemetry gözlemi ayrıca organizasyon tarafından
manuel olarak kaydedilir.

Semantic cache koşusu için `APIM_CACHE_STATUS_HEADER` doğrulanmış APIM header
adını taşımalıdır; değer ilk çağrıda `miss`, anlamca benzer ikinci çağrıda
`hit` olmalıdır. Header yoksa test `BLOCKED` olur; `cachedTokens` tek başına
cache kanıtı sayılmaz. Usage ve latency değerleri
`TestResults/gateway-semantic-cache.json` dosyasına içerik taşımadan raporlanır.
`aura-readiness`, `gateway-pii` ve `gateway-semantic-cache` artifact'leri
soru/cevap veya secret içermez. Workflow veya canlı kanıtlar henüz
çalıştırılmadıysa durum `BEKLİYOR`; başarılı varsayılmaz.
