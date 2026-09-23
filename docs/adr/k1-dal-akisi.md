# ADR: K1 dal akışının taslak tasarıma önceliği

## Context

K1 s.2 §4, coding agent atamasında base `development` ve agent draft PR
hedefi `development` der. P01 taslağı ise insanın açtığı feature/fix dalını
agent base'i, `copilot/... → development` PR'ını da yasak sayar. Bu iki
talimat aynı anda uygulanamaz.

## Decision

Kaynak önceliği K1 > K2 > tasarım olduğundan coding agent için base ve draft
PR hedefi `development` seçilir. Branch policy `feature/<issue-no>-<slug>`
ve `fix/<issue-no>-<slug>` kaynaklarına ek olarak yalnız coding agent'ın
`copilot/...` çalışma dallarından `development` hedefine PR kabul eder.
`main` yalnız `development` üzerinden insan onaylı PR ile güncellenir.
Organizasyonun gerekli branch/PR korumaları insan tarafından kurulur.

## Consequences

P01 taslağındaki `copilot/abc → development` ret örneği artık kabul
örneğidir. Agent'ın adlandırılamayan `copilot/...` dalı K1'in issue
numaralı feature/fix dalı beklentisini tek başına karşılamaz; insan açtığı
feature/fix dalı ve PR bağlantısını ayrıca korur. Kurumun kesin akışına
ilişkin bir netleştirme gelirse branch policy ve bu ADR insan onayıyla
yeniden değerlendirilir; bu arada bir check sessizce atlanmaz.
