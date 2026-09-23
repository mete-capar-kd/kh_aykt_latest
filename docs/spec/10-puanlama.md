# Puanlama
Kaynak: [A12], [A13] — bu dosya bağlayıcı spec'tir.

**[A12] Puanlama (deterministik saf fonksiyon; LLM'e bırakılmaz):**

```
score = 10 − 3×CRITICAL − 2×HIGH − 1×MEDIUM − 0,5×LOW   (INFO = 0)
confidence = potansiyel olan bulgular yarım ağırlıkla düşülür
score = max(0, round(score, 1))   // decimal aritmetiği, MidpointRounding.AwayFromZero (9,25 → 9,3)
```

**[A13] Kapsam ve "sıfır bulgu" kuralı:**
- Her rubric alt kontrolü için `SubCheckResult { id, status: Karşılandı | İhlal | Kanıt yok | Uygulanamaz, evidenceRefs }` kaydedilir.
- Tanımlar:
  - "Uygulanabilir" alt kontrol: durumu `Uygulanamaz` olmayan alt kontrol.
  - "Kanıtlı" alt kontrol: durumu `Karşılandı` veya `İhlal` olan ve en az bir doğrulanmış `evidenceRef` taşıyan alt kontrol. `İhlal` de kanıtlı sayılır. Kanıt referansı taşımayan `Karşılandı`/`İhlal` kaydedilmez; `Kanıt yok` olarak kaydedilir.
  - `Uygulanamaz` durumu gerekçe ister. Koddan doğrulanamayan konular (ör. m07 branch policy) `Uygulanamaz` + "koddan doğrulanamaz" olarak işaretlenir ve kapsamı düşürmez.
- Kapsam şöyle hesaplanır:
  - `none`: uygulanabilir alt kontrol yok **veya** hiçbiri kanıtlı değil.
  - `partial`: en az bir uygulanabilir alt kontrol kanıtlı, en az biri "Kanıt yok".
  - `complete`: **en az bir** uygulanabilir alt kontrol var ve uygulanabilir alt kontrollerin hepsi kanıtlı.
- `coverage = none` → `Değerlendirilemedi`. Hepsi `Uygulanamaz` olan metrik 10/Uyumlu **olamaz**.
- `coverage = partial` → metrik en iyi ihtimalle `Kısmen Uyumlu` olur; puan yine bulgulardan hesaplanır.
- `Uyumlu` için `coverage = complete` gerekir.

| Koşul | Durum |
|---|---|
| coverage = none, bütün bulgular doğrulanamadı, timeout veya istekte seçilmedi | `Değerlendirilemedi` (score = null, `notAssessableReason` dolu) |
| score ≥ 9, açık CRITICAL/HIGH yok **ve coverage = complete** | `Uyumlu` |
| score ≥ 9, açık CRITICAL/HIGH yok, coverage = partial | `Kısmen Uyumlu` |
| 5 ≤ score < 9, ya da score ≥ 9 olup HIGH/CRITICAL var | `Kısmen Uyumlu` |
| score < 5 | `Uyumsuz` |

- `overallScore`, değerlendirilebilen metriklerin ortalamasıdır; hiçbiri değerlendirilemezse null olur.
- Kanıtsız boş bulgu listesi otomatik 10 **almaz**; yukarıdaki kapsam kuralı uygulanır.
- Aynı teknik bulgu aynı metrikte iki kez düşülmez.
- Aynı snapshot ve aynı (mock) bulgular iki koşuda aynı puanı verir; bu determinizm testi zorunludur.
