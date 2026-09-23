# API sözleşmesi
Kaynak: [A14] — bu dosya bağlayıcı spec'tir.

**[A14] API sözleşmesi:**

```
POST /api/ask      (Authorization: Bearer <Entra ID token>)
Request:
{ "question": "Bu repository SSO gereksinimlerini karşılıyor mu?",   // zorunlu, trim, 3–2000 karakter
  "repositoryUrl": "https://github.com/org/repo",  // opsiyonel; yoksa config'teki varsayılan repo; yalnız izinli host
  "ref": "main",                                  // opsiyonel; commit SHA'ya sabitlenir
  "metrics": ["m02","m03"] }                       // opsiyonel; yoksa 10 metrik; geçersiz/boş/tekrarlı → 400
200:
{ "answer": "...",
  "answerType": "assessment | repo_answer | insufficient_evidence | refusal",
  "promptVersion": "...",
  // answerType "refusal" (Router out_of_scope/unsafe, content filter, sızıntı engeli) yanıtlarında
  // "evidence": [] ve "assessment": null olur. Router intent "repo_question" → answerType "repo_answer".
  "evidence": [ { "file": "src/.../Program.cs", "startLine": 42, "endLine": 58,
                  "reason": "...", "url": "https://github.com/org/repo/blob/<commitSha>/src/...#L42-L58" } ],
  "correlationId": "...",
  "assessment": {
    "jobId": "...", "repositoryUrl": "...", "ref": "...", "commitSha": "...",
    "metrics": [ /* HER ZAMAN m01..m10 sırasıyla 10 kayıt */
      { "metricId": "m02", "metricName": "...", "status": "Kısmen Uyumlu", "score": 7.0,
        "rationale": "...", "risk": "...", "coverage": "complete|partial|none",
        "subChecks": [ { "id": "m02-anonymous-endpoints", "status": "İhlal",
                         "reason": "...", "evidenceRefs": ["m02-001"] } ],
        "findings": [ { "id": "m02-001", "title": "...", "severity": "high", "confidence": "kesin",
                        "standardRef": "UseCase §6.2 — anonymous endpointler", "rationale": "...", "impact": "...",
                        "evidence": [ { "file": "...", "startLine": 1, "endLine": 9,
                                        "snippet": "***MASKED*** ...", "snippetSha256": "...", "url": "..." } ],
                        "recommendation": "..." } ],
        "notAssessableReason": null, "filesExamined": 12, "toolCalls": 31, "rejectedFindings": 2 } ],
    "overallScore": 6.4,
    "reportMarkdown": "| Metrik | Durum | Puan | Gerekçe | Risk | Dosya/Satır | Öneri | ...",
    "generatedAt": "...",
    "modelInfo": { "router": "...", "profiler": "...", "evaluator": "...", "synthesizer": "...", "promptVersion": "..." } } }
Hatalar (ProblemDetails + correlationId):
  400 geçersiz istek · 401 kimlik yok/geçersiz · 403 yetki yok
  422 repo erişilemedi veya snapshot limiti (2.000 dosya / 50 MB) aşıldı
  429 rate limit (Retry-After) · 500 beklenmeyen hata (iç detay yok) · 502 APIM/model hatası (içerik filtresi hariç) · 504 global timeout

GET /health (anonim) → 200 { "status": "Healthy", "version": "...", "commitSha": "...", "uptimeSeconds": 0 }
  Secret, connection string veya iç detay dönmez. AI/APIM çağrısı yapmaz.
```
