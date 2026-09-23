# Test ve kalite
Kaynak: [A5] — bu dosya bağlayıcı spec'tir.

**[A5] Test ve kalite:**
- Test araçları: xUnit v2 + `xunit.runner.visualstudio` (VSTest runner; `dotnet test --filter "Category!=GatewayLive"` bu runner'la çalışır), NSubstitute, NetArchTest.Rules; HTTP sahtelemesi için özel `HttpMessageHandler`; coverage için coverlet.
- Gerçek ağ veya gerçek model çağrısı unit/integration testlere **girmez** (yalnız `GatewayLive` kategorisi hariç; o testler CI'da çalışmaz).
- Kalite kapısı: `dotnet build -warnaserror`, `dotnet test --filter "Category!=GatewayLive"`, `dotnet format --verify-no-changes --exclude tests/Hackathon.Assessment.Tests/Fixtures`.
- Kodlama kuralları:
  - Async I/O ve her katmanda `CancellationToken`.
  - Options pattern + `ValidateOnStart`.
  - Global mutable state yok (tek istisna: DI'da singleton kaydedilen, thread-safe metrik sonuç cache'i).
  - Merkezi exception handling ve ProblemDetails; stack trace/secret dışarı sızmaz.
  - `.Result`/`.Wait()` yok, boş `catch` yok.
