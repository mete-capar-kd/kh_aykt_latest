# LLM rolleri
Kaynak: [A6] — bu dosya bağlayıcı spec'tir.

**[A6] Dört LLM rolü (beşinci rol eklenmez; orkestratör LLM çağırmaz):**
0. **Ask Router** (`Agents/AskRouterAgent`; Tasarım kararı — AURA gereksinimi):
   - Ucuz deployment; tool yok; repo içeriği görmez; yalnız kullanıcının sorusunu **veri** olarak alır.
   - Çıktısı JSON schema ile doğrulanır: `{ intent: assessment | repo_question | out_of_scope | unsafe, metrics: [m01..m10], language: tr | en | other, questionType: yes_no | open }`.
   - Router kararı yetki vermez; yalnız hangi metriklerin gerektiğini ve sorunun kapsam içinde olup olmadığını belirler.
   - **Metrik önceliği:** istekte `metrics` verilmişse o kullanılır ve Router'ın metrik önerisi yok sayılır (Router yine `intent` için çalışır). Verilmemişse Router'ın `metrics` listesi kullanılır; liste boşsa 10 metrik. Çalıştırılmayan metrikler `Değerlendirilemedi` + `notAssessableReason` ile döner: istekte filtrelendiyse "İstekte seçilmedi", Router seçmediyse "Bu soru için değerlendirilmedi".
   - **Router hatası veya geçersiz çıktı (fail-closed):**
     - Input Guard `suspectedInjection = true` işaretlediyse güvenli ret döner.
     - Aksi halde `prompts/guard/metric-keywords.yaml` içindeki deterministik tr/en anahtar kelime → metrik eşlemesi kullanılır (ör. "sso, kimlik, yetki, auth, token" → m02; "secret, şifre, cors, injection" → m03; "docker, container, image" → m08; "test, coverage" → m06; "pipeline, ci, cd, deploy" → m07; "log, telemetry, izlenebilirlik" → m05; "readme, doküman, adr" → m10; "performans, cache, retry" → m09; "veritabanı, migration, transaction, entegrasyon" → m04; "katman, mimari, yapı, bağımlılık" → m01). Eşleme kuralı: 4 karakterden kısa anahtarlar ("ci", "cd", "adr", "sso") yalnız tam kelime olarak eşleşir; diğerleri Türkçe ekler için kelime başı (prefix) eşleşmesiyle ("yetki" → "yetkilendirme") eşleşir. Karşılaştırma `tr-TR` kültüründe küçük harfe çevrilmiş metin üzerinde yapılır.
     - Eşleşme yoksa `answerType = "refusal"` ve kapsamı açıklayan şablon döner. Belirsiz girdi tam 210 sn'lik değerlendirmeyi **tetiklemez**.
1. **Repo Profiler:** dosya ağacı ve manifest dosyalarını görür. Stack, katman haritası, giriş noktaları ve metrik uygulanabilirlik önerisini döner. Puan veya ihlal üretmez.
2. **Metric Evaluator:** tek implementasyon, 10 farklı prompt+rubric ile instance'lanır.
   - Tool-calling döngüsü burada çalışır. Her instance izoledir; diğerlerinin context'ini ve bulgularını görmez.
   - Araştırma serbesttir: tool çağrı kotası ve zorunlu arama sırası yoktur, tek sınır timeout'tur.
3. **Report Synthesizer:** yalnız doğrulanmış bulgulardan kullanıcının sorusuna cevap, yönetici özeti ve risk önceliği yazar.
   - Puan hesaplamaz; yeni bulgu veya satır referansı üretmez.
   - Özetteki her iddia bir bulguya ya da kapsam kanıtına bağlıdır.
