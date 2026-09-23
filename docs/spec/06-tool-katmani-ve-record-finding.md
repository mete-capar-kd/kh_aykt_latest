# Tool katmanı ve record_finding
Kaynak: [A7], [A8] — bu dosya bağlayıcı spec'tir.

**[A7] Read-only tool katmanı (snapshot üzerinde; dosya yazmaz, komut çalıştırmaz, ağa çıkmaz):**

| Tool | Davranış |
|---|---|
| `get_repo_manifest()` | Dosya ağacı (≤500 giriş), diller, manifest dosyaları, LOC dağılımı |
| `list_files(glob, limit=200)` | path, boyut, satır sayısı |
| `search_code(pattern, glob?, regex=true, max_results=200)` | `{path, line, text}`; regex zaman aşımı ve ReDoS koruması |
| `read_file(path, start_line?, end_line?)` | Satır numarası önekli metin; aralık verilmezse ≤1.500 satır |
| `run_scanner(metric_id)` | Deterministik aday bulgular; çağrılması zorunlu değildir |
| `record_finding(finding)` | Doğrular ve kaydeder |

- Her yanıtta kısaltma varsa `truncated=true` ve `total` bulunur.
- Yalnız gerçekten dönen satırlar o evaluator'ın **görüldü (provenance)** kaydına girer.

**[A8] `record_finding` doğrulama kuralları (gevşetilemez, bypass edilemez).** Biri başarısızsa kayıt reddedilir ve ajana neden ile düzeltme yolu döner:
1. `file` snapshot'ta var.
2. `1 <= startLine <= endLine <= dosya satır sayısı`.
3. `endLine - startLine <= 120`.
4. Snippet, o satır aralığında whitespace normalize edilmiş ve maskelenmiş olarak geçiyor.
5. Bu satırlar **bu evaluator tarafından** `read_file`/`search_code` ile görülmüş.
6. `severity`, `confidence`, `standardRef` dolu.
7. `recommendation` ≥20 karakter ve jenerik kalıp listesinde değil ("iyileştirilmeli", "gözden geçirilmeli" tek başına reddedilir).

Ek kurallar:
- Aynı bulgunun (teknik fingerprint) 6. başarısız denemesi kalıcı rettir; ID değiştirmek sayacı sıfırlamaz.
- Reddedilen bulgu sayısı `MetricResult.rejectedFindings` alanında tutulur.
- Tool bulgunun doğruluğuna değil **izlenebilirliğine** karar verir.
- `snippetSha256`, normalize edilmiş maskelenmiş snippet'in hash'idir.
