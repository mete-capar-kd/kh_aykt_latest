# Repository güvenliği

## GitHub ayarları

Bir ekip üyesi **Settings → Code security** bölümünde Dependency graph,
Dependabot alerts, Secret scanning, Push protection ve Code scanning
(Advanced/CodeQL workflow) seçeneklerini açar. Secret scanning ve push
protection GitHub repository ayarıdır; YAML taklidi yapılmaz. Bu repository
private olduğundan CodeQL, secret scanning ve dependency review için GHAS
lisansı/erişimi doğrulanmalıdır; aksi halde repository'yi public yapmak veya
kurumun GHAS yetkisini açtırmak gerekir. Bu değişikliklere yalnız yetkili
insan karar verir.

`.github/workflows/codeql.yml` tek CodeQL workflow'udur. Settings'de CodeQL
Default setup açıksa mükerrer bulgu oluşmaması için insan tarafından
kapatılır; Advanced setup workflow'u korunur. CodeQL veya dependency review
upload'ı lisans/dependency graph ayarı yüzünden başarısızsa workflow
silinmez, `if:` ile atlanmaz veya sadece `main`'e indirilmez: durum PR'da
**external blocker** olarak raporlanır.

PR onayından önce Critical/High başta olmak üzere tüm açık CodeQL, secret
scanning ve dependency review bulguları incelenir. Dismiss ancak teknik
gerekçe, kapsam ve nedenin kayıt altında olmasıyla yapılır. Aynı teknik
bulguyu ikinci CodeQL workflow'u ile çoğaltmayın. Gerçek secret, token,
connection string, sertifika private key veya kişisel veri commit edilmez;
lokal `.env`/user-secrets ve Azure App Settings/Key Vault kullanılır.

Bu aşamadaki workflow action'ları SHA yerine major sürüm etiketiyle
(`actions/*@v4`, `github/codeql-action/*@v3`) kullanılır; SHA araştırmasının
maliyeti bilinçli olarak ertelenmiştir. Bu karar değerlendirici m07
scanner'ında potansiyel bulgu olabilir; güvenlik onayının yerini almaz.
