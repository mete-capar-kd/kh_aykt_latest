# K1 §15 release checklist

Her satır main'e teslimden önce kanıtlanmalı veya aşağıdaki açık bekleme
nedeniyle bırakılmalıdır. Bu dosya bir canlı deployment sonucu değildir.

| K1 §15 maddesi | Kanıt / durum |
|---|---|
| En az 10 geçerli GitHub Issue var ve her biri kapsam/kabul kriteri içeriyor. | NOT_EVIDENCED — bu issue kapsamı dışında issue panosu tam sayım/kalite denetimi yapılmadı. |
| Issue, branch, commit ve PR bağlantıları kurulmuş. | BEKLİYOR — release kaydına gerçek issue/commit/PR URL'leri ve development merge commit'i eklenecek. |
| Kod, promptlar, Markdown dokümanları, testler ve config şablonları push edilmiş. | BEKLİYOR — bu issue PR'ı development'a merge edilip main release commit'ine dahil edilmelidir. |
| `.github/workflows` altındaki YAML dosyaları tracked durumda. | KANITLI — workflow YAML'ları Git tarafından izlenir; CI ve live verification sonuçları ayrıca beklenir. |
| Secret, API key, connection string veya gerçek kişisel veri repository'de yok. | BEKLİYOR — yerel audit testleri çalışır; canlı secret scanning/GHAS sonucu ve insan incelemesi gerekir. Test girdileri yalnız GitHub secret'ında tutulur. |
| GHAS kontrolleri çalışıyor ve kritik bulgular ele alınmış. | BEKLİYOR — test ortamında GHAS lisansı yoktur; canlı repo lisansı/ayarları açılmalı, CodeQL ve dependency-review yeşil olmalıdır. |
| CI/CD build-test-image-deploy akışı başarılı. | BEKLİYOR — `ci.yml` ve `cd.yml` tanımlıdır; ilgili GitHub Actions run sonucu bu belgede doldurulmalıdır. |
| ACR image mevcut ve App Service son image ile çalışıyor. | BEKLİYOR — ACR tag/digest ve portal App Service image bilgisi canlı ortamdan alınmalıdır. |
| Application Insights bağlı ve güncel telemetry üretiyor. | BEKLİYOR — App Insights bağlantısı ile request/dependency/token telemetry'si portalda doğrulanmalıdır; içerik loglanmamalıdır. |
| Health ve işlevsel endpoint testleri başarılı. | BEKLİYOR — yerel `HealthEndpointsTests`/`AskEndpointsTests` üretim kanıtı değildir; deploy sonrası `/health` ve yetkili `/api/ask` smoke sonucu eklenmelidir. |
| Model trafiği APIM üzerinden geçiyor; PII ve cache testleri doğrulanmış. | BEKLİYOR — onaylı Gateway workflow'u ve Gateway/Foundry log/telemetry gözlemi gereklidir; uygulama masker'ı Gateway kanıtı sayılmaz. |
| Yalnız izin verilen GitHub coding agent modelleri kullanılmış; kullanım/maliyet kanıtları korunmuş ve ekip üst limiti aşılmamış. | BEKLİYOR — Copilot usage ve 50 USD sınırı **resmî rapordan doldurulacak**; tahmin yazılmaz. |
| Main PR açılmış, kontroller tamamlanmış ve resmi sürüm main'e merge edilmiş. | BEKLİYOR — P12 agent PR'ı yalnız `development`'a açılır; son `development`→`main` PR'ını insan açıp onaylar/merge eder. |

## Release kanıt zinciri

Her yayın için aşağıdaki değerler aynı release kaydında birbirine bağlanır.
İnsan canlı kanıtı eklemeden zincir tamamlanmış sayılmaz.

| Zincir adımı | Değer / durum |
|---|---|
| `main` merge SHA | BEKLİYOR — insan onaylı development→main merge sonrası doldurulacak. |
| CI run URL ve sonuç | BEKLİYOR — ilgili SHA için yeşil build-test/container-smoke run linki eklenecek. |
| ACR image digest | BEKLİYOR — CD job summary'den gerçek digest kopyalanacak. |
| App Service sürümü | BEKLİYOR — `/health` `commitSha` değerinin aynı SHA olduğu doğrulanacak. |
| Application Insights | BEKLİYOR — aynı commit için içeriksiz request/dependency/token telemetry kanıtı eklenecek. |

## Dış doğrulama ve engelleyiciler

- APIM base URL, caller kimlik doğrulama şeması/header'ı, `API_AUDIENCE`,
  verification environment ve Azure OIDC değişkenleri organizasyondan alınır;
  burada değer uydurulmaz.
- `PII_TEST_INPUTS_JSON` ve `APIM_TEST_KEY` GitHub environment secret'ı olarak
  verilir. Sentetik girdiler veya gerçek secret'lar repository'ye eklenmez.
- Cache kanıtı için `Apim:CacheStatusHeader` tanımlı olmalı ve Gateway gözlemi
  miss→hit'i göstermelidir. Header yoksa sonuç BLOCKED'dır.
- AURA hazırlık koşusu resmî puan yerine geçmez. Resmî skor, kategori kırılımı,
  tarih ve commit organizasyonun doğrulanabilir sonucundan doldurulur; gelene
  kadar [AI safety kaydı](ai-safety.md#hazırlık-koşusu-sonucu) bekler.
- CodeQL workflow'u şu anda yoktur; canlı Code scanning/GHAS lisansı ve
  dependency-review sonucu insan tarafından doğrulanmalıdır. Test ortamındaki
  lisans eksikliği canlı güvenlik kontrolünün yeşil olduğu anlamına gelmez.
