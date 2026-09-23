# Maskeleme
Kaynak: [A9] — bu dosya bağlayıcı spec'tir.

**[A9] Maskeleme:** Tek bir `ISecretMasker` vardır. API key, token, connection string, parola, private key, JWT ve e-posta/telefon/TCKN desenlerini `***MASKED***` ile değiştirir. Modele giden her içeriğe, tool çıktısına, scanner bağlamına, rapora, log ve telemetry'ye uygulanır. Rapor secret'ın **varlığını** bildirir, **değerini** göstermez. Bu uygulama maskelemesi APIM PII testinin yerine **geçmez**; ikisi ayrı katmandır.
