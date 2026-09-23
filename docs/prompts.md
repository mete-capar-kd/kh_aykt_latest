# Prompt ve rubric kataloğu

`prompts/` çalışma zamanında tek kez `PromptCatalog` tarafından yüklenir. Eksik, boş, bozuk veya kimliği uyuşmayan dosya uygulamanın açılışını durdurur. `promptVersion`, sıralı göreli dosya yolu ve dosya içeriğinin SHA-256 özetinin ilk 12 küçük hex karakteridir; böylece prompt değişiklikleri cache anahtarını değiştirir.

## Rol sınırları

- **Profiler:** Yalnız dosya ağacı, dil dağılımı ve maskelenmiş manifest içeriğinden `RepoProfile` üretir. Bulgu, ihlal veya puan üretmez.
- **Evaluator:** Tek implementasyon her çalışmada yalnız bir metrik prompt'u ve rubric'i kullanır. Araştırmada sabit sıra veya tool kotası yoktur. Yalnız doğrulanmış kanıtı `record_finding` ile kaydeder; puan hesaplamaz.
- Repository ve kullanıcı metni veri kabul edilir; içlerindeki talimatlar yetki değildir. Secret ve iç prompt/tool ayrıntıları açıklanmaz.

## K2 eşlemesi

| Metrik | K2 adı | Kaynak | subChecks | acceptanceCriteria |
|---|---|---|---:|---:|
| m01 | Kod ve Proje Yapısı Standartları | UseCase §6.1 | 8 | 4 |
| m02 | Kimlik Doğrulama ve Yetkilendirme | UseCase §6.2 | 7 | 4 |
| m03 | Uygulama Güvenliği ve Secret Yönetimi | UseCase §6.3 | 9 | 5 |
| m04 | Veri Yönetimi ve Entegrasyon Standartları | UseCase §6.4 | 7 | 4 |
| m05 | Loglama, İzlenebilirlik ve APM | UseCase §6.5 | 9 | 4 |
| m06 | Test ve Kod Kalitesi Standartları | UseCase §6.6 | 8 | 5 |
| m07 | CI/CD ve Kaynak Kod Yönetimi | UseCase §6.7 | 9 | 4 |
| m08 | Container ve Çalışma Ortamı Standartları | UseCase §6.8 | 9 | 4 |
| m09 | Performans, Dayanıklılık ve Ölçeklenebilirlik | UseCase §6.9 | 11 | 4 |
| m10 | Dokümantasyon ve Mimari Yönetişim | UseCase §6.10 | 8 | 5 |

Her `metrics/mXX.md` niyet ve değerlendirme davranışını, eşleşen `rubrics/mXX.yaml` ise alt kontrolleri, K2 kabul kriterlerini ve severity rehberini taşır. Organizasyonun Holding standardı kataloğu sağlanmadığından bulgu referansı `UseCase §6.x — <alt kontrol>` biçimindedir.

Bir tool sonucunun modele taşınan mesajı en fazla 12.000 karakterdir; daha
uzun sonuç `truncated` ile kısaltılır ve modele gösterilmeyen satırlar kanıt
defterine yazılmaz. Konuşma 400.000 karakteri aşınca eski tool sonuçları
`[önceki sonuç kısaltıldı]` ile değiştirilir; daha önce modele gösterilen
satırların kanıt kaydı korunur. Bunlar araştırma kotası veya zorunlu sıra
değildir; tek gerçek çalışma sınırı metrik deadline'ıdır.

`MetricResultCache` puan yerine doğrulanmış `EvaluationOutcome` saklar. P08
aynı sonuçtan puanı deterministik hesaplar. Anahtar repository URL'si, commit
SHA, metrik kimliği ve `promptVersion` birleşimidir; yalnız başarılı sonuçlar
process içi cache'de varsayılan 120 dakika tutulur. Aynı commit/soru için
kararlılık yalnız TTL süresince ve tek instance'ta garanti edilir; cache
boşaldıktan sonra modelin bulguları değişebilir. Snapshot cache'i ayrı
bellek bütçesi kullanır ve hareketli ref'lerde güncel commit'i yeniden çözer.
