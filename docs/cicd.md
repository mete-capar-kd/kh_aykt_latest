# CI/CD ve dal akışı

## K1 kaynak önceliği

K1 s.2 §4, coding agent atamasında base branch'in `development` seçilmesini ve
agent'ın draft PR'ının `development` dalına açılmasını ister. P01 issue taslağındaki
`feature/...` base ve `copilot/... → development` yasağı bununla çelişir.
K1 önceliklidir; karar ve sonuçlar `docs/adr/k1-dal-akisi.md` içindedir.

İnsan `main` dalından bir defa `development` dalını oluşturur. İşe ait
`feature/<issue-no>-<kısa-ad>` veya `fix/<issue-no>-<kısa-ad>` dalı
`development` üzerinden açılır. K1'in agent akışında base `development` ve draft
PR hedefi `development` olur; agent'ın `copilot/...` çalışma dalına yalnız bu
akış için izin verilir. İnsan kaynaklı feature/fix PR'ları da `development`
dalına gider. `main` yalnız onaylanmış `development → main` PR'ını kabul eder.
Hiçbir agent PR'ı doğrudan `main` dalına açılmaz veya merge edilmez.
Commit başlıklarında `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `ci:` kullanılır.

## Zorunlu kontroller

`main` ve `development` ruleset'leri PR, en az bir insan onayı ve şu check'leri
zorunlu kılmalıdır: `ci / build-test`, `ci / container-smoke`, `dependency-review` ve
`branch-policy`. Force-push yasaklanmalıdır. CI restore, uyarılar-hata build,
GatewayLive hariç test/coverage/TRX ve format kontrolü yapar. P11 image tarama,
ACR push ve App Service deploy aşamalarını ekler.

P04'te kullanıcı talebiyle `codeql` workflow'u kaldırıldı: repository'de
Code scanning etkin olmadığından CodeQL sonuçları ve `codeql` check'i şu anda
mevcut değildir. `codeql` required check'i varsa repository yöneticisi
koruma kuralını güncellemeden sonraki PR'lar bloklanabilir. Güvenlik taramasının
etkin olduğu iddia edilmez. `dependency-review` workflow'u korunur; Dependency
graph/GHAS kapalıysa check kırmızı kalır ve yönetici etkinleştirmelidir.

Copilot PR'ındaki workflow'lar ilk kez çalışmadan önce insan onayı
isteyebilir. Agent yerel build/test/format sonuçlarını PR'da belirtir;
onay bekleyen check'e başarılı demez ve aynı workflow'u tekrar tetiklemez.
İnsan GitHub Actions onayını verir, kırmızı check'leri inceler ve yeşil
sonucu doğrular.

`.github/workflows/copilot-setup-steps.yml` agent ortamı için yalnız varsayılan
dal `main` üzerinden okunur. P01 `development` dalına merge edilince insan
erken bir `development → main` PR'ı açıp kontrollerden sonra merge eder; böylece
P02 ve sonraki agent oturumları .NET 10 kurulumunu kullanır.

Kırmızı GHAS/CI check'i için `main` dalına GitHub'ın “Fix with Copilot”
önerisinden doğrudan PR açılmaz. Düzeltme ilgili PR'a `@copilot` yorumuyla
veya `development` üzerinden açılan `fix/<issue-no>-<kısa-ad>` dalıyla yapılır;
insan onayı ve check'ler olmadan merge edilmez.

P11 kaynakları Azure Portal'da elle hazırlar; CI'da Bicep build yoktur.
`ci.yml` reusable olarak main CD tarafından çağrılır: build/test/format
ardından container health/SHA, non-root, prompts ve Docker HEALTHCHECK
doğrulanır. Main push'unda production onayından sonra OIDC ile mevcut ACR'a
login, image build ve Trivy taraması, SHA tag push/digest kaydı, mevcut App
Service container güncellemesi ve canlı smoke gerçekleşir. Portal kontrol
listesi ve rollback `docs/azure-architecture.md` ve `docs/operations.md`
içindedir. GitHub vars veya Azure RBAC yoksa canlı doğrulama **beklemede**;
başarılı varsayılmaz.

| PR kaynağı | Hedef | Sonuç |
|---|---|---|
| `mete-feature/1-x` | `development` | Ret |
| `p02-x` | `development` | Ret |
| `copilot/abc` | `development` | Kabul: K1 coding agent akışı |
| `feature/5-x` | `main` | Ret |
| `copilot/abc` | `feature/5-x` | Kabul |
| `feature/5-x` | `development` | Kabul |
| `development` | `main` | Kabul |
