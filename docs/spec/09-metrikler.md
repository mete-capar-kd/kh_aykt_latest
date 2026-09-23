# Metrikler
Kaynak: [A11] — bu dosya bağlayıcı spec'tir.

**[A11] 10 metrik — K2 §6.1–§6.10.** Metrik adları, "kontrol kapsamı" ve "kabul kriteri" metinleri K2'den **harfiyen** kopyalanır; kısaltılmaz ve yeniden yazılmaz. Adlar:

m01 Kod ve Proje Yapısı Standartları · m02 Kimlik Doğrulama ve Yetkilendirme · m03 Uygulama Güvenliği ve Secret Yönetimi · m04 Veri Yönetimi ve Entegrasyon Standartları · m05 Loglama, İzlenebilirlik ve APM · m06 Test ve Kod Kalitesi Standartları · m07 CI/CD ve Kaynak Kod Yönetimi · m08 Container ve Çalışma Ortamı Standartları · m09 Performans, Dayanıklılık ve Ölçeklenebilirlik · m10 Dokümantasyon ve Mimari Yönetişim

Her metriğin K2 **kontrol kapsamı** (harfiyen; rubric `subChecks` bu kalemlerden üretilir, her kalem en az bir alt kontroldür):
- **m01 (§6.1):** Solution ve proje yapısı, katman ayrımı, dependency yönleri, circular dependency, isimlendirme standartları, ortak bileşen kullanımı, business logic konumlandırması, controller sorumlulukları ve dependency injection kullanımı.
- **m02 (§6.2):** Microsoft Entra ID veya kurumsal kimlik altyapısı, authentication yapılandırması, endpoint bazlı authorization, rol/claim/permission kontrolleri, anonymous endpointler, token validation, 401/403 kullanımı ve service-to-service kimlik doğrulaması.
- **m03 (§6.3):** Hardcoded secret, API key, connection string, Key Vault kullanımı, input validation, SQL Injection, CORS ayarları, güvenlik headerları, hassas veri loglama ve güvensiz şifreleme yöntemleri.
- **m04 (§6.4):** Veritabanı erişim standartları, migration yönetimi, transaction kullanımı, timeout ve retry politikaları, API istemci yönetimi, kişisel/hassas veri kullanımı, harici servis bağımlılıkları ve API versioning.
- **m05 (§6.5):** Structured logging, Application Insights entegrasyonu, correlation ID, distributed tracing, merkezi exception handling, log seviyeleri, hassas veri maskeleme, dependency tracking, health ve telemetry yapılandırması.
- **m06 (§6.6):** Unit ve integration testlerin varlığı, kritik iş kurallarının test edilmesi, test projesi yapısı, mock kullanımı, SonarQube yapılandırması, code coverage, cyclomatic complexity, duplicate code ve testlerin pipeline içinde çalıştırılması.
- **m07 (§6.7):** Pipeline dosyaları, build ve test adımları, code quality gate, security/dependency scanning, artifact yönetimi, ortam bazlı deployment, production onayı, secret kullanımı, branch ve PR politikalarına ilişkin mevcut kanıtlar.
- **m08 (§6.8):** Dockerfile varlığı, onaylı base image, multi-stage build, non-root çalışma, image boyutu, port ve health check, environment variable kullanımı, secret yönetimi, CPU/memory limitleri ve stateless çalışma.
- **m09 (§6.9):** Asenkron programlama, blocking çağrılar, N+1 sorgular, cache kullanımı, timeout, retry, circuit breaker, background job, idempotency, büyük veri sorguları, stateless tasarım ve yatay ölçeklenebilirlik.
- **m10 (§6.10):** README, kurulum açıklamaları, mimari diyagram, API dokümantasyonu, ADR kayıtları, teknoloji envanteri, uygulama sahibi, destek modeli ve operasyonel sorumluluklar.

Her metriğin K2 kabul kriteri aşağıdaki cümlelere bölünür. Her cümle, o metriğin prompt dosyasında zorunlu bir davranış ve P07'da ayrı bir test/doğrulama maddesi olur:
- **m01:** mimari ihlali doğru tespit etmeli · ilgili dosya/proje/metot bilgisini göstermeli · ihlal edilen Holding standardını belirtmeli · uygulanabilir refactoring önerisi sunmalı.
- **m02:** korumasız endpointleri tespit etmeli · endpoint veya metot seviyesinde kanıt göstermeli · authentication ile authorization farkını doğru değerlendirmeli · yetkisiz erişim etkisini açıklamalı.
- **m03:** güvenlik açığını doğru sınıflandırmalı · secret değerlerini raporda açık göstermemeli · kanıtı dosya/satır bilgisiyle sunmalı · güvenli alternatif önermeli · kritik bulguları yüksek öncelikle raporlamalı.
- **m04:** veri tutarlılığı veya entegrasyon dayanıklılığı etkisini açıklamalı · ilgili kod bloğunu göstermeli · genel öneri yerine teknik çözüm sunmalı · retry önerirken idempotency riskini dikkate almalı.
- **m05:** observability eksikliğini doğru tespit etmeli · loglama ile monitoring kavramlarını karıştırmamalı · hassas veri loglarını güvenlik riskiyle ilişkilendirmeli · kurum standardına uygun çözüm önermeli.
- **m06:** yalnızca test dosyası sayısına bakmamalı · assertion kalitesini değerlendirmeli · kritik akışların test edilip edilmediğini değerlendirmeli · quality gate durumunu değerlendirmeli · test piramidine uygun iyileştirme ihtiyacını değerlendirmeli.
- **m07:** pipeline dosyalarını analiz etmeli · eksik aşamaları doğru sıralamayla açıklamalı · kaynak koddan doğrulanamayacak branch policy konularında varsayım yapmamalı · kanıt yoksa `Değerlendirilemedi` statüsünü kullanmalı.
- **m08:** Dockerfile ve deployment manifestlerini birlikte değerlendirmeli · güvenlik ve operasyon risklerini ayrı açıklamalı · uygun base image veya non-root çalışma önermeli · kanıtı olmayan altyapı konularında varsayım yapmamalı.
- **m09:** potansiyel ve kesin performans sorunlarını ayırmalı (`confidence`) · sorunun etkisini açıklamalı · ölçüm gerektiren konularda kesin hüküm vermemeli · profiling veya yük testi ihtiyacını belirtebilmeli.
- **m10:** dokümanın yalnızca varlığını değil yeterliliğini değerlendirmeli · eksikleri göstermeli · koddan çıkarılan mimari görünümü özetlemeli · kullanılabilir içerik şablonu önermeli. Owner/destek bilgisi repo'da yoksa uydurmamalı.

Holding standardı metni ve madde numaraları verilmediği için `standardRef` = `"UseCase §6.x — <alt kontrol>"` olur; madde numarası uydurulmaz. Holding standart kataloğu ileride gelirse rubric'e eklenir.
