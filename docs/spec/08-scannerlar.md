# Scannerlar
Kaynak: [A10] — bu dosya bağlayıcı spec'tir.

**[A10] Scanner paketleri (saf, LLM'siz; `CandidateFinding` üretir: kural ID, konum, ±15 satır maskelenmiş bağlam, dosya rolü controller/test/config/migration/örnek):**
- **Generic:** secret regex, `.env` varlığı, Dockerfile (root, `latest`, secret ARG/ENV, HEALTHCHECK yok), `.github/workflows` (geniş `permissions`, `pull_request_target`, pinlenmemiş action), README/docs varlık ve yeterlilik sinyalleri, aşırı geniş `.gitignore`.
- **.NET:** `[AllowAnonymous]`, `.Result`/`.Wait()`, boş `catch {}`, `appsettings*.json` içinde connection string, `AllowAnyOrigin` CORS, DI kayıtları.
- **Python:** `eval`/`exec`, `verify=False`, f-string SQL, bare `except`, `DEBUG = True`.
- **Node:** doğrudan `process.env` kullanımı, string ile `child_process` komutu, açık CORS.

Profile göre generic ve eşleşen paketler çalışır. Scanner yokluğu metriği otomatik olarak `Değerlendirilemedi` yapmaz. Scanner çıktısı bağlayıcı değildir.
