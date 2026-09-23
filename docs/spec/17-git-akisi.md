# Git akışı
Kaynak: [A21] — bu dosya bağlayıcı spec'tir.

**[A21] Git ve PR akışı (K1 s.2):**
| Dal | Nereden açılır | PR hedefi | Kim merge eder |
|---|---|---|---|
| `main` | — (korumalı, varsayılan, resmî sürüm) | — | yalnız `development` → `main` PR'ı, insan onayıyla |
| `development` | `main`'den bir kez (insan) | `main` | insan |
| `feature/<issue-no>-<kısa-ad>` | **`development`** | **yalnız `development`** | insan |
| `fix/<issue-no>-<kısa-ad>` | **`development`** | **yalnız `development`** | insan |

**Issue başına akış (K1 s.2 dal adıyla birebir):**
1. Issue dalı `origin/development`'tan açılır: feature ise `feature/<issue-no>-<kısa-ad>`, hata/güvenlik düzeltmesiyse `fix/<issue-no>-<kısa-ad>` (ör. issue #3 → `feature/3-api-sozlesmesi`). Dal adı issue numarasını ve kısa adı aynen taşır.
2. Coding agent dal adının tam eşleştiğini doğrular ve doğrudan bu dalda çalışır; kullanıcı adı, araç adı veya `copilot/` gibi başka önek eklenmez.
3. Agent conventional commit'lerle değişiklikleri kaydeder, aynı adlı dalı push eder ve draft PR'ı **`development`**'a açar (`Refs #<issue-no>`). İnsan PR'ı inceler ve merge eder.
4. Kararlı noktada insan `development` → `main` PR'ını açar.

- Dal adı **tam olarak** `feature/<issue-no>-<kısa-ad>` veya `fix/<issue-no>-<kısa-ad>` biçimindedir: küçük harf, kelimeler `-` ile, `<issue-no>` gerçek GitHub issue numarası (ör. `feature/3-api-sozlesmesi`). Kullanıcı adı, araç adı veya başka **önek eklenmez** (`<kullanıcı>-feature/...` yanlıştır; yerel araçlarda "branch prefix" ayarı boş bırakılır). Issue numarasız dal (`p02-...`) yanlıştır.
- Taslak A21'deki agent PR'ını feature dalına açma akışı K1 s.2 §4 ile çeliştiğinden uygulanmaz: bu spec'te coding agent'ın draft PR hedefi `development`'tır.
- **Yasak:** `main`'e doğrudan push; `feature/` veya `fix/` dalından `main`'e PR (GitHub'ın "Fix with Copilot" önerisi dahil); `main`'den dal açıp geliştirme yapmak; `development`'ı `main`'e geçmeden başka dala taşımak.
- `main` → `development` geri senkron PR'ı yalnız `main`'e acil bir düzeltme girmişse açılır; normal akışta gerekmez.
- İlk `development` → `main` PR'ı P01'den hemen sonra açılır (`copilot-setup-steps.yml` yalnız `main`'den okunur).
- Kural `.github/workflows/branch-policy.yml` ile otomatik denetlenir (P01): `main`'e açılan PR'ın kaynağı `development` değilse veya `development`'a açılan PR'ın kaynağı `^(feature|fix)/[0-9]+-[a-z0-9-]+$` değilse check kırmızı olur.
- Commit başlıkları conventional commits kullanır: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `ci:`.
