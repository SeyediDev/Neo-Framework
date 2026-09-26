# Neo Agent Orchestration — Backlog

این بک‌لاگ برای انتقال مستقل زیرساخت Hyper به Neo-Framework است. ترتیب، وابستگی و معیار پذیرش بخشی از قرارداد کار است.

## P0 — مسیر اصلی قابل اجرا

| Key | عنوان | وابستگی | خروجی قابل قبول |
|---|---|---|---|
| NAO-001 | ایجاد solution مستقل و project references به Neo | — | solution مستقل build می‌شود و reference به Hyper/Neo.Bpms ندارد |
| NAO-002 | مدل دامین Organization/Workspace/Project | NAO-001 | invariants، scope keys و تست cross-workspace |
| NAO-003 | RoleProfile و AgentProfile | NAO-002 | role و agent جدا، profile قابل فعال/غیرفعال و تست انتخاب agent |
| NAO-004 | WorkItem، child item، dependency، log، evidence و time entry | NAO-002 | create/claim/status/time/history با تست application |
| NAO-005 | WorkflowDefinition/Transition و gateها | NAO-003, NAO-004 | transition با commit/test/approval/artifact شرطی |
| NAO-006 | persistence و migration دیتابیس مستقل | NAO-002..005 | migration، unique/index/FK، optimistic concurrency و provisioner |
| NAO-007 | outbox/inbox، idempotency، retry و lease | NAO-006 | duplicate callback اثر تکراری ندارد؛ failure به retry/DLQ می‌رود |
| NAO-008 | API v1 و OpenAPI contract | NAO-004..007 | APIهای canonical با auth/scope و contract test |
| NAO-009 | FakeHarness و orchestration worker | NAO-005, NAO-007 | create → dispatch → callback → handoff → done سبز است |
| NAO-010 | Web مستقل: board، جزئیات، roles، agents، workflows، runs | NAO-008 | پنل بدون Hyper/Bpms بالا می‌آید و عملیات P0 را انجام می‌دهد |

## P1 — اتصال و مهاجرت

| Key | عنوان | وابستگی | خروجی قابل قبول |
|---|---|---|---|
| NAO-011 | HTTP Harness adapter با secret reference | NAO-009 | endpoint بیرونی configurable، callback امن، secret در log نیست |
| NAO-012 | MCP server عملیاتی | NAO-008 | tools: get/create/claim/log/start/handoff/evidence با policy |
| NAO-013 | Skill مستقل `neo-agent-orchestration` | NAO-012 | workflow ایجنت و نام/schema ابزارها با MCP یکسان است |
| NAO-014 | Hyper adapter/client | NAO-008 | Hyper بدون اتصال مستقیم به جداول محصول کار می‌کند |
| NAO-015 | مهاجرت رکوردهای WorkManagement | NAO-014 | dry-run، mapping و reconciliation؛ migration قابل تکرار |
| NAO-016 | compatibility read path در Hyper | NAO-015 | پنل قدیمی موقتاً خراب نمی‌شود و source of truth مشخص است |

## P1 — بسته و نصب

| Key | عنوان | وابستگی | خروجی قابل قبول |
|---|---|---|---|
| NAO-017 | CLI `neo-agent init` و provisioner | NAO-006, NAO-008 | init، migrate، seed، health با exit code مشخص |
| NAO-018 | Developer bundle و MCP local package | NAO-010, NAO-012, NAO-013 | نصب روی ماشین تمیز بدون repo checkout |
| NAO-019 | container/compose و environment template | NAO-010, NAO-017 | API/Web/worker و database قابل راه‌اندازی |
| NAO-020 | release manifest/checksum/signature/upgrade notes | NAO-018, NAO-019 | artifact قابل اعتبارسنجی و migration versioned |

## P2 — سخت‌سازی پس از مسیر اصلی

| Key | عنوان | معیار خروج |
|---|---|---|
| NAO-021 | authorization policy کامل و audit tamper-aware | policy matrix و audit verification |
| NAO-022 | metrics/tracing برای item/run/handoff | dashboard و trace correlation |
| NAO-023 | workflow designer پیشرفته و versioning | draft/publish/rollback workflow |
| NAO-024 | approval، artifact و repository providerهای بیشتر | provider contract + integration tests |
| NAO-025 | Agents API/Codex gateway واقعی | endpoint/credential واقعی و E2E با عدم افشای secret |
| NAO-026 | backup/restore، retention و disaster recovery | runbook و restore drill |
| NAO-027 | معماری، امنیت، load و contract test suite | گزارش reproducible در CI |

## قواعد مدیریت کار

- هر task یک owner role، owner agent، project، domain، branch و status دارد.
- task فعال متعلق به هر role باید قبل از واگذاری task جدید claim/complete/block شود.
- هر تسک توسعه‌ای در همین محصول باید commit، test evidence و log داشته باشد.
- کارهای P2 تا پایان P0/P1 متوقف نمی‌کنند مگر blocker امنیتی یا داده‌ای باشند.
- هر stage با یک commit کوچک و قابل برگشت تحویل می‌شود.

## ترتیب اجرا

```text
NAO-001 → NAO-002 → NAO-003 → NAO-004 → NAO-005 → NAO-006 → NAO-007
                                                   ↓
                                  NAO-008 → NAO-009 → NAO-010
                                                     ↓
                          NAO-011/012/013 → NAO-014/015/016
                                                     ↓
                                      NAO-017/018/019/020
                                                     ↓
                                             NAO-021..027
```

## ریسک‌های ثبت‌شده

- API بیرونی Codex/Agents هنوز نباید فرض‌شده یا hard-code شود؛ تا زمان وجود قرارداد معتبر فقط Fake/Http عمومی.
- انتقال schema Hyper بدون mapping و reconciliation باعث از دست رفتن context می‌شود؛ migration ابتدا dry-run است.
- shared database بین پروژه‌ها مرز ownership را خراب می‌کند؛ مدل پیش‌فرض database مستقل با scope چندپروژه‌ای است.
- UI مستقل نباید business rule را تکرار کند؛ همهٔ transitionها از Application/API عبور می‌کنند.
