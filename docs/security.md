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

P04'te kullanıcı talebiyle `.github/workflows/codeql.yml` kaldırıldı: Code
scanning bu repository'de etkin olmadığı için CodeQL analizi sonuçlarını
yükleyemiyordu. Otomatik CodeQL taraması şu anda **yoktur**; yeniden etkinleştirme
için kurumun lisans/ayarlarını doğrulaması ve insan onayıyla workflow'u geri
getirmesi gerekir. `dependency-review` workflow'u korunur; Dependency graph/GHAS
etkin değilken verdiği hata PR'da **external blocker** olarak raporlanır.

PR onayından önce Critical/High başta olmak üzere tüm açık CodeQL, secret
scanning ve dependency review bulguları incelenir. Dismiss ancak teknik
gerekçe, kapsam ve nedenin kayıt altında olmasıyla yapılır. Aynı teknik
bulguyu ikinci CodeQL workflow'u ile çoğaltmayın. Gerçek secret, token,
connection string, sertifika private key veya kişisel veri commit edilmez;
lokal `.env`/user-secrets ve Azure App Settings/Key Vault kullanılır.

Bu aşamadaki etkin workflow action'ları SHA yerine major sürüm etiketiyle
(`actions/*@v4`) kullanılır; SHA araştırmasının
maliyeti bilinçli olarak ertelenmiştir. Bu karar değerlendirici m07
scanner'ında potansiyel bulgu olabilir; güvenlik onayının yerini almaz.

## Container ve portal secret yönetimi

CD, SHA etiketli image'ı ACR'a göndermeden önce
`aquasecurity/trivy-action@0.28.0` ile işletim sistemi ve uygulama paketlerini
tarar; düzeltmesi olan CRITICAL/HIGH bulgular job'ı durdurur
(`ignore-unfixed: true`). NuGetAudit/NU1901–NU1904 build kapısı ayrıca
korunur. Taranan image Debian tabanlı .NET 10 runtime'dır; Dockerfile'a
credential veya secret build arg verilmez. Taramanın istisna kabulü için
insan onayı gerekir; düzeltmesi bulunmayan CVE kaydı aşağıda tutulmalıdır:

| CVE | Etkilenen image / paket | Neden henüz düzeltilemiyor | Sahip / yeniden bakma tarihi |
|---|---|---|---|
| Henüz doğrulanmış kayıt yok | — | Canlı tarama sonucu bekleniyor | — |

Azure kaynakları portalda ekip tarafından hazırlanır; IaC secret parametresi
veya kaynak oluşturma workflow'u yoktur. APIM key, salt-okunur GitHub token,
canary ve App Insights connection string yalnız App Service Configuration'da
saklanır; portal RBAC erişimi kısıtlanır. CD appsettings komutlarının çıktısını
yazdırmaz. Key Vault bu işte kurulmaz: mevcut uygulamanın secret ihtiyacı
App Service settings üzerinden karşılanır; kurumsal yönetim/rotasyon gereksinimi
doğarsa Key Vault ayrı değerlendirilir. OIDC federated credential üretim
environment'ına bağlıdır; kalıcı Azure client secret tutulmaz.
