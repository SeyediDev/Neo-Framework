# Installation and Distribution Plan

هدف این برنامه، قابل استفاده کردن Neo Agent Orchestration برای حساب‌ها و کامپیوترهای دیگر، بدون clone کردن repository و بدون انتقال secret در package است.

## artifactهای هر release

```text
neo-agent-orchestration-{version}-win-x64.zip
neo-agent-orchestration-{version}-linux-x64.tar.gz
neo-agent-cli-{version}-win-x64.zip
neo-agent-mcp-{version}.zip
checksums.txt
release-manifest.json
UPGRADE.md
```

نسخهٔ اول می‌تواند با ZIP شروع شود؛ container و installer native بعد از اثبات مسیر provisioning اضافه می‌شوند.

## نصب تعاملی

```text
neo-agent init
  → انتخاب provider database
  → دریافت connection/config بدون چاپ secret
  → validate configuration
  → migrate database
  → seed system roles/workflow
  → ایجاد Workspace/Project اولیه
  → چاپ URL و health result
```

دستورهای لازم:

```text
neo-agent doctor
neo-agent migrate
neo-agent seed
neo-agent health
neo-agent export-config
neo-agent version
```

`export-config` فقط template و نام secretها را صادر می‌کند، نه مقدار آن‌ها.

## روش‌های استقرار

### Local/Developer

Web/API/worker به‌صورت processهای local و MCP به‌صورت stdio اجرا می‌شوند. SQLite فقط برای demo/توسعه مجاز است؛ production از SQL Server/PostgreSQL پشتیبانی می‌کند.

### Server

Containerهای جدا برای Web، API، Worker و MCP gateway؛ database و broker external یا compose-managed. Health/readiness باید dependencyها و migration version را گزارش کند.

### Enterprise

OIDC، secret store، managed database، broker/redis، backup، audit retention و signed release الزامی می‌شوند.

## ارتقا

1. `neo-agent doctor` و backup check.
2. بررسی compatibility matrix.
3. اجرای migration در transactionهای قابل برگشت هرجا provider اجازه دهد.
4. smoke test API، Web، worker و MCP.
5. ثبت migration version و release manifest.

Rollback کد باید با forward-fix ترجیح داده شود؛ rollback دیتابیس فقط طبق runbook provider-specific انجام می‌شود.
