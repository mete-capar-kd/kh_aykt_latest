# Puanlama ve durum çözümleme

Metrik puanları model çıktısından bağımsız, `decimal` aritmetiğiyle hesaplanır:

```text
10 − (3 × CRITICAL) − (2 × HIGH) − (1 × MEDIUM) − (0,5 × LOW)
```

`INFO` puanı etkilemez. `potansiyel` bulgular ilgili cezanın yarısını uygular. Aynı
teknik bulgu; kanıt yolu, başlangıç/bitiş satırı ve normalize edilmiş başlığıyla
tekilleştirilir ve yalnız bir kez düşülür. Sonuç sıfırın altına inmez ve bir ondalığa
`MidpointRounding.AwayFromZero` ile yuvarlanır (`9,25 → 9,3`).

## Kapsam ve durum

- `none`: puan `null`, durum `Değerlendirilemedi` ve neden zorunludur.
- `partial`: puan hesaplanır, ancak en iyi durum `Kısmen Uyumlu`dur.
- `complete`: puan en az 9 ve açık `HIGH`/`CRITICAL` yoksa `Uyumlu`dur.
- Puan 5'ten küçükse `Uyumsuz`; diğer değerlendirilebilir sonuçlar
  `Kısmen Uyumlu`dur.
- Potansiyel dahil her `HIGH`/`CRITICAL`, puan 9 veya üzerindeyken dahi
  `Uyumlu` sonucunu engeller.

Genel puan yalnız değerlendirilebilir metriklerin aritmetik ortalamasıdır ve aynı
yuvarlama kuralını kullanır. Değerlendirilebilir metrik yoksa genel puan `null`dır.

## Markdown raporu

Rapor, `AssessmentReport` ile aynı metrik kayıtlarından ve `m01`–`m10` sırasıyla
üretilir. Her satır durum, puan, gerekçe, risk, en çok üç kanıt bağlantısı ve en
yüksek önem dereceli bulgunun önerisini içerir. Kanıt bağlantıları
`blob/<commitSha>/<path>#Lx-Ly` biçiminde commit'e sabitlenir. Hücrelerde `|`
kaçırılır, satır sonları `<br>` olur, hücreler 300 karakterle sınırlandırılır ve
`null` puan `—` olarak gösterilir.

Rapor ayrıca `Değerlendirilemeyen metrikler` ve `Yönetici özeti` bölümlerini içerir.
Oluşturulan Markdown'ın tamamı döndürülmeden önce `ISecretMasker` ile maskelenir.
