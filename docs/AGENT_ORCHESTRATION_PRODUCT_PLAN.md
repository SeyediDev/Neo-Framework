# Neo Agent Orchestration — Product Plan

## هدف

وضعیت این سند: طرح محصول، نه گزارش قابلیت‌های آماده. پیاده‌سازی و محدودیت‌های فعلی در
[README محصول](../products/Neo.AgentOrchestration/README.md) ثبت می‌شود.
طبق تصمیم کاربر در ۲۰۲۶-۰۹-۲۶، نسخهٔ فعلی Hyper تا تکمیل مهاجرت فعال می‌ماند.
حذف کد، داده، تنظیمات یا پنل قدیمی فقط پس از پذیرش نتیجه و تأیید صریح کاربر مجاز است.

ساخت یک محصول مستقل، چندپروژه‌ای و قابل نصب برای مدیریت کار و ارکستراسیون ایجنت‌ها که بر روی Neo Framework ساخته می‌شود و هیچ وابستگی اجرایی به `Hyper.AdminPanel`، `Neo.Bpms` یا دیتابیس Hyper ندارد.

Hyper در این مدل فقط یک مصرف‌کنندهٔ محصول است: یک Workspace/Project ثبت می‌کند و از API، MCP یا SDK برای ایجاد و پیشبرد Work Item استفاده می‌کند. دامین‌های Basalam، Accounting و Integration همچنان متعلق به Hyper باقی می‌مانند و وارد دامین orchestration نمی‌شوند.

## مرزهای معماری

تصمیم تکمیلی ۲۰۲۶-۰۹-۲۷: مدیریت کار Neo مستقل و فعال می‌ماند. GitHub، GitLab و
Azure DevOps ارائه‌دهنده‌های اختیاری‌اند و Neo و سرویس بیرونی می‌توانند هم‌زمان
فعال باشند. انتخاب مدیریت کار، مخزن/PR و Harness سه تصمیم مستقل است. مالکیت
هر فیلد و حل تعارض باید صریح باشد؛ تغییر سرویس بیرونی مجوز دورزدن claim یا gate
نیست. [قرارداد اتصال‌ها](../products/Neo.AgentOrchestration/docs/WORK-PROVIDERS.md)
معیار اجرای NAO-028 تا NAO-031 است. پیاده‌سازی adapterها پس از آماده‌شدن محصول
اصلی انجام می‌شود؛ توقف توسعه Neo یا جایگزینی آن تصویب نشده است.

```text
Neo Framework
  ├─ Domain / Application / Infrastructure / Endpoint
  ├─ persistence, auth, queue, outbox, telemetry, OpenAPI
  └─ Companion/MCP foundation

Neo Agent Orchestration (محصول مستقل داخل Neo-Framework)
  ├─ Organization / Workspace / Project
  ├─ RoleProfile / AgentProfile
  ├─ Workflow / Transition / Approval
  ├─ WorkItem / dependency / evidence / time tracking
  ├─ AgentRun / Handoff / callback
  ├─ Harness adapters
  ├─ API + OpenAPI
  ├─ مستقل Web Admin
  ├─ MCP server + Skill
  └─ installer / provisioning / upgrade

Hyper
  └─ adapter/client و integrationهای دامینی خودش
```

قواعد غیرقابل مذاکره:

1. `Neo.Bpms` فقط برای پنل‌ها یا محصولات فعلی خودش است و dependency محصول جدید نیست.
2. دیتابیس محصول مستقل از Hyper است؛ پروژه‌های همین نصب، جداول چندپروژه‌ای محصول را با scope و مجوز مستقل استفاده می‌کنند.
3. Role مشخص می‌کند «چه کاری مجاز و مورد انتظار است»؛ AgentProfile مشخص می‌کند «چه اجراکننده‌ای با چه provider/model/configuration آن را انجام می‌دهد».
4. وضعیت اجرای واقعی فقط در `AgentRun` است؛ AgentProfile هرگز محل نگهداری state اجرای یک کار نیست.
5. هر تغییر مهم با event/audit و outbox قابل ردیابی است.
6. هیچ token، secret، prompt حساس یا connection string در log، commit یا Work Item ذخیره نمی‌شود.
7. API، MCP و Web سه مصرف‌کنندهٔ مستقل application layer هستند و منطق کسب‌وکار را تکرار نمی‌کنند.

## محل و ساختار محصول

محصول داخل مخزن `Neo-Framework` اما خارج از هستهٔ پنج library اصلی ایجاد می‌شود:

```text
products/Neo.AgentOrchestration/
  src/
    Neo.AgentOrchestration.Domain/
    Neo.AgentOrchestration.Application/
    Neo.AgentOrchestration.Infrastructure/
    Neo.AgentOrchestration.Contracts/
    Neo.AgentOrchestration.Api/
    Neo.AgentOrchestration.Web/
    Neo.AgentOrchestration.Mcp/
    Neo.AgentOrchestration.Provisioning/
    Neo.AgentOrchestration.Harness.Abstractions/
    Neo.AgentOrchestration.Harness.Http/
  tests/
  docs/
  deploy/
```

این solution از پروژه‌های `src/Neo.*` reference می‌گیرد، اما هیچ source code را داخل هستهٔ Neo کپی نمی‌کند. تا وقتی محصول به ثبات نرسیده، قرار دادن آن در Companion به‌عنوان sample ممنوع است؛ پس از تثبیت، sample فقط wrapper مستند خواهد بود.

## مدل دامین نسخهٔ اول

- `Organization`, `Workspace`, `Project`
- `RoleProfile`, `AgentProfile`
- `WorkflowDefinition`, `WorkflowState`, `WorkflowTransition`
- `WorkItem`, `WorkItemDependency`, `WorkItemEvent`
- `WorkItemEvidence`, `WorkItemTimeEntry`, `Approval`
- `AgentRun`, `AgentRunAttempt`, `Handoff`
- `WebhookEndpoint`, `OutboxMessage`, `InboxMessage`

تمام رکوردهای عملیاتی به `WorkspaceId` و در صورت مربوط‌بودن به `ProjectId` محدود می‌شوند. کلیدهای خارجی cross-workspace پذیرفته نمی‌شوند.

## جریان اصلی

```text
Create WorkItem
  → resolve Project/Workflow
  → claim Role/Agent
  → create AgentRun
  → dispatch through IAgentHarness
  → receive authenticated idempotent callback
  → attach commit/test/artifact evidence
  → evaluate transition gates
  → handoff or human approval
  → Done / Blocked / retry / dead-letter
```

دروازهٔ انتقال می‌تواند شامل commit، test evidence، artifact، approval، timeout و retry policy باشد. پاسخ موفق dispatch فقط به معنی accepted شدن اجرا است، نه تکمیل کار.

## دیتابیس و استقرار

- نام پیش‌فرض: `NeoAgentOrchestration`.
- یک database برای هر installation/environment، با schema داخلی مستقل؛ نه یک database برای هر پروژه.
- جداسازی پروژه‌ها با `OrganizationId`/`WorkspaceId`/`ProjectId` و policyهای application/database انجام می‌شود.
- برای installationهای حساس، database جدا و credential جدا قابل فعال‌سازی است؛ این یک deployment option است، نه مدل اجباری.
- migrationها versioned و مستقل از Hyper هستند.
- provisioning باید validate، migrate، seed حداقلی و health check داشته باشد؛ `EnsureCreated` برای production مجاز نیست.

## API و اتصال‌ها

API canonical:

```text
/api/orchestration/v1/workspaces
/api/orchestration/v1/projects
/api/orchestration/v1/roles
/api/orchestration/v1/agents
/api/orchestration/v1/workflows
/api/orchestration/v1/work-items
/api/orchestration/v1/work-items/{id}/claim
/api/orchestration/v1/work-items/{id}/handoff
/api/orchestration/v1/work-items/{id}/runs
/api/orchestration/v1/runs/{id}/dispatch
/api/orchestration/v1/runs/{id}/callback
/api/orchestration/v1/approvals
/api/orchestration/v1/events
```

قرارداد OpenAPI منبع حقیقت API است و با contract-test کنترل می‌شود. API با OAuth/JWT برای کاربران و service identity برای adapterها کار می‌کند؛ callbackها signature، timestamp، replay protection و idempotency key دارند.

Harness abstraction:

```csharp
public interface IAgentHarness
{
    Task<DispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken ct);
    Task<RunStatusResult> GetStatusAsync(string externalRunId, CancellationToken ct);
    Task CancelAsync(string externalRunId, CancellationToken ct);
}
```

نسخهٔ اول adapterهای `Fake` و `Http` را دارد. adapter مخصوص Codex/Agents API فقط وقتی endpoint، احراز هویت و قرارداد بیرونی واقعاً فراهم باشد فعال می‌شود؛ نبود آن به‌صورت صریح در health/status گزارش می‌شود.

## وب مستقل

`Neo.AgentOrchestration.Web` پنل مستقل RTL/LTR است و به API محصول متصل می‌شود. صفحات پایه:

- Workspace/Project selector
- Kanban و فهرست Work Item با فیلتر project/domain/role/status
- جزئیات کار، log، time tracking، evidence و history
- Role/Agent profile
- Workflow designer و transition gates
- Handoff و approval inbox
- Run monitor و retry/cancel
- audit، metrics و settings/harness

پنل Hyper در فاز migration فقط برای backwards compatibility باقی می‌ماند و source of truth جدید نخواهد بود.

## نصب و بستهٔ توزیع

محصول باید سه روش نصب داشته باشد:

1. **Developer bundle**: ZIP یا self-contained archive شامل Web/API/MCP، migration runner، نمونهٔ config، Skill و health checker.
2. **Server deployment**: container image و compose/Helm chart با API، Web، worker، SQL Server و در صورت نیاز RabbitMQ/Redis.
3. **Developer CLI/installer**: ابزار `neo-agent init` برای ساخت config، ایجاد database، اجرای migration/seed و ثبت یک Workspace/Project.

بستهٔ نصب نباید secrets واقعی را حمل کند. کاربر باید secretها را از environment/secret store یا interactive setup وارد کند. Upgrade با migration backup check، version check و rollback guidance انجام می‌شود.

هر release باید شامل version، compatibility matrix، checksum/signature، migration notes و smoke-test result باشد. MCP در حالت local stdio قابل نصب است؛ یک remote MCP اختیاری است و نیازمند auth و hosting جداگانه است.

## معیار خروج محصول مستقل

محصول زمانی از Hyper جداشده تلقی می‌شود که:

- solution مستقل بدون reference به Hyper یا Neo.Bpms build و test شود؛
- database/provisioning مستقل باشد؛
- Web مستقل با API خودش بالا بیاید؛
- FakeHarness مسیر کامل create → dispatch → callback → handoff → done را اجرا کند؛
- OpenAPI، MCP و Skill با implementation هم‌نام و هم‌قرارداد باشند؛
- package نصب روی یک کامپیوتر تمیز، از صفر تا health check، قابل اجرا باشد؛
- Hyper فقط با adapter/client به آن وصل شود و ownership دامین‌های خودش را حفظ کند.
