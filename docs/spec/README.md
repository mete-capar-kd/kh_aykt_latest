# Mimari spec dosyaları

Bu dizindeki bağlayıcı metinler, P02 issue'sunun ilk yorumundaki [A1]–[A23] bölümlerinden üretilmiştir. Issue'ya özel istisna olarak [A21], K1 s.2 §4'ün `development` base ve PR hedefi şartıyla düzeltilmiştir; gerekçe `docs/adr/0001-mimari-kararlar.md` içindedir.

| Dosya | Konu | Okuyan issue |
|---|---|---|
| [00-kesin-yasaklar.md](00-kesin-yasaklar.md) | Kesin yasaklar | P02–P12 |
| [01-urun-ve-kapsam.md](01-urun-ve-kapsam.md) | Ürün ve kapsam | P02, P03, P12 |
| [02-runtime-zinciri.md](02-runtime-zinciri.md) | Runtime zinciri | P02, P03, P08, P10, P12 |
| [03-solution-yapisi.md](03-solution-yapisi.md) | Solution yapısı | P02, P03, P12 |
| [04-test-ve-kalite.md](04-test-ve-kalite.md) | Test ve kalite | P02, P03–P12 |
| [05-llm-rolleri.md](05-llm-rolleri.md) | LLM rolleri | P02, P06–P08, P12 |
| [06-tool-katmani-ve-record-finding.md](06-tool-katmani-ve-record-finding.md) | Tool katmanı ve bulgu doğrulama | P02, P04, P05, P07, P08, P12 |
| [07-maskeleme.md](07-maskeleme.md) | Maskeleme | P02, P04, P06, P09, P12 |
| [08-scannerlar.md](08-scannerlar.md) | Scannerlar | P02, P05, P07, P12 |
| [09-metrikler.md](09-metrikler.md) | K2 metrikleri | P02, P05, P07, P08, P12 |
| [10-puanlama.md](10-puanlama.md) | Puanlama ve kapsam | P02, P07, P08, P12 |
| [11-api-sozlesmesi.md](11-api-sozlesmesi.md) | API sözleşmesi | P02, P03, P08, P10, P12 |
| [12-limitler-ve-snapshot.md](12-limitler-ve-snapshot.md) | Limitler ve snapshot güvenliği | P02, P03, P04, P08, P12 |
| [13-apim-client.md](13-apim-client.md) | APIM AI Gateway client | P02, P06, P12 |
| [14-telemetry.md](14-telemetry.md) | Telemetry | P02, P06, P11, P12 |
| [15-aura-guvenlik.md](15-aura-guvenlik.md) | AURA güvenlik ve güvenilirlik | P02, P09, P10, P12 |
| [16-config.md](16-config.md) | Config anahtarları | P02, P03–P10, P12 |
| [17-git-akisi.md](17-git-akisi.md) | Git ve PR akışı | P02–P12 |

P03–P12 için issue gövdesindeki **Okunacak spec** listesi belirleyicidir; bu özet tablo yeni bir okuma yükümlülüğü oluşturmaz. Bir çelişkide `docs/spec/00-kesin-yasaklar.md` ve ilgili issue'nun kaynak önceliği kuralları uygulanır.
