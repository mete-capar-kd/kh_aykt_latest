# Config
Kaynak: [A20], [A23] — bu dosya bağlayıcı spec'tir.

**[A20] Organizasyon değerleri gelmeden uygulamanın açılması:** Placeholder değerler (`<ORGANİZASYONDAN-ALINACAK>`) yalnız `Development` ve `Testing` ortamında `ValidateOnStart`'tan geçer. `Production`'da eksik veya placeholder değer açılışı açık bir hata mesajıyla durdurur; mesajda secret değeri bulunmaz.

**[A23] Config anahtarları (tek kaynak; `appsettings.json`, `.env.example` ve App Settings bu listeyi kullanır; ASP.NET Core ortam değişkeni biçimi `Bölüm__Anahtar`):**

| Anahtar | Varsayılan / değer | Secret mi | Sahip issue |
|---|---|---|---|
| `APP_VERSION` | `0.0.0-local` (CD'de sürüm) | Hayır | P03, P11 |
| `GIT_COMMIT_SHA` | `local` (CD'de commit SHA) | Hayır | P03, P11 |
| `Apim__BaseUrl` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P06 |
| `Apim__Auth__Scheme` | `<ORGANİZASYONDAN-ALINACAK>` (ör. SubscriptionKey / ManagedIdentity / OAuth) | Hayır | P06 |
| `Apim__Auth__HeaderName` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P06 |
| `Apim__Auth__Key` | yok | **Evet** | P06 |
| `Apim__Auth__Scope` | `<ORGANİZASYONDAN-ALINACAK>` (yalnız OAuth/MI ise) | Hayır | P06 |
| `Apim__Deployments__Cheap` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P06 |
| `Apim__Deployments__Strong` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P06 |
| `Apim__AttemptTimeoutSeconds` | `60` | Hayır | P06 |
| `Apim__MaxAttempts` | `3` | Hayır | P06 |
| `EntraId__Instance` | `https://login.microsoftonline.com/` | Hayır | P10 |
| `EntraId__TenantId` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P10 |
| `EntraId__Audience` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P10 |
| `EntraId__RequiredRoles__0` | boş (baseline: authenticated) | Hayır | P10 |
| `EntraId__RequiredScopes__0` | boş | Hayır | P10 |
| `Repository__DefaultUrl` | `<ORGANİZASYONDAN-ALINACAK>` | Hayır | P03, P04 |
| `Repository__DefaultRef` | `main` | Hayır | P03 |
| `Repository__AllowedInputHosts__0` | `github.com` | Hayır | P04 |
| `Repository__AllowedDownloadHosts__0` / `__1` | `api.github.com` / `codeload.github.com` | Hayır | P04 |
| `Repository__GitHubToken` | yok (opsiyonel, salt-okunur) | **Evet** | P04 |
| `Assessment__MaxFiles` | `2000` | Hayır | P03, P04 |
| `Assessment__MaxTotalBytes` | `52428800` | Hayır | P03, P04 |
| `Assessment__ProfilerTimeoutSeconds` | `20` | Hayır | P07 |
| `Assessment__MetricTimeoutSeconds` | `140` | Hayır | P07 |
| `Assessment__SynthesizerTimeoutSeconds` | `40` | Hayır | P08 |
| `Assessment__GlobalTimeoutSeconds` | `210` | Hayır | P08 |
| `Assessment__MaxConcurrentFullAssessments` | `2` | Hayır | P08 |
| `Cache__MetricResultTtlMinutes` | `120` | Hayır | P07 |
| `Cache__MetricResultMaxEntries` | `1000` | Hayır | P07 |
| `Cache__SnapshotMaxBytes` | `536870912` | Hayır | P07 |
| `RateLimit__PermitsPerMinute` | `300` | Hayır | P03 |
| `RateLimit__QueueLimit` | `100` | Hayır | P03 |
| `RateLimit__ExemptClientIds__0` | boş | Hayır | P03 |
| `Safety__CanaryToken` | yok (yoksa açılışta rastgele) | **Evet** | P09 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | yok | **Evet** | P06 |
| `Telemetry__Team` / `Telemetry__Application` / `Telemetry__Environment` | `<ORGANİZASYONDAN-ALINACAK>` / `hackathon-assessment-api` / `local` | Hayır | P06 |

- Secret olan anahtarların değeri **hiçbir dosyaya yazılmaz**. `appsettings.json`'da secret anahtarlar yer almaz; `.env.example`'da `=` sonrası boş bırakılır.
- `appsettings.Development.json` yalnız secret olmayan lokal değerleri içerir ve commit edilir. Lokal secret'lar `dotnet user-secrets` veya git'e girmeyen `appsettings.Local.json` ile verilir.
