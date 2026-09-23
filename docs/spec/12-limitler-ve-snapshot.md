# Limitler ve snapshot
Kaynak: [A15], [A16] — bu dosya bağlayıcı spec'tir.

**[A15] Limitler (Options'tan gelir, hardcode değil):**
- Snapshot: ≤2.000 dosya, ≤50 MB toplam boyut.
- Snapshot'a alınmayanlar: binary, `.git`, `node_modules`, `.venv`, `bin`, `obj`, `dist`.
- **Azure App Service ön uç HTTP isteklerini ~230 sn'de keser.** Bu yüzden varsayılan süreler: Profiler 20 sn, metrik başına 140 sn, Synthesizer 40 sn, global deadline 210 sn. Hepsi config'ten değiştirilebilir ve `docs/api.md`'de belgelenir. Queue veya async job eklenmez.
- Timeout'a düşen metrik hata fırlatmaz; `Değerlendirilemedi` ve neden döner. **Bir metriğin başarısızlığı diğer dokuzunu düşürmez.**
- Global timeout 504 döner.

**[A16] Snapshot güvenliği:**
- İki ayrı allowlist kullanılır:
  - Girdi `repositoryUrl` host'u (varsayılan `github.com`).
  - İndirme/API host'ları (varsayılan `api.github.com`, `codeload.github.com`).
- Yalnız HTTPS kabul edilir.
- SSRF koruması: özel IP, localhost ve metadata endpoint'leri engellenir. Her redirect hedefi indirme allowlist'ine göre doğrulanır.
- **Snapshot limiti (2.000 dosya veya 50 MB) aşılırsa analiz yapılmaz, 422 döner** ve neden belirtilir; kısmi snapshot ile sessiz analiz yapılmaz.
- Tool seviyesindeki kısaltmalar (500/200/1.500) ise `truncated=true` ile görünür olur ve hata sayılmaz.
- Ref, commit SHA'ya sabitlenir. Arşiv bellek içinde açılır.
- Path traversal/zip-slip, symlink ve zip-bomb sınırları uygulanır.
- İncelenen repo'nun kodu, hook'u veya build/test komutu **çalıştırılmaz**.
- Private repo erişimi için opsiyonel, salt-okunur GitHub token'ı yalnız App Settings/Key Vault'tan gelir; URL, log ve hataya sızmaz.
- Repository içeriği **güvenilmeyen veridir**; içindeki talimatlar ajan davranışını ve tool yetkisini değiştiremez (prompt injection koruması).
