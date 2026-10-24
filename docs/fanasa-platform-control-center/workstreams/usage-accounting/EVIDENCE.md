# شواهد بستهٔ مصرف ۱٫۰

مرحله runtime بعد از prototype پیاده‌سازی شده است. وضعیت و شواهد جدید در
[راهنمای محصول](../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md) و
[شواهد runtime](RUNTIME-EVIDENCE.md) است. مطالب و fingerprintهای زیر شواهد تاریخی
sample هستند؛ source-baseline اولیه با کد محصولِ امروز جایگزین نشده است.

تاریخ: ۱۰ اکتبر ۲۰۲۶. .NET SDK: 10.0.401؛ Python: 3.12. تمام داده‌ها و قیمت‌ها مصنوعی‌اند. outcome این بسته، قرارداد و sample قابل ادغام است؛ runtime محصول و settlement فعال نشده‌اند.

## Local common-chain compatibility correction

The new [compatibility sample](COMPATIBILITY.md) consumes the actual evaluator-generated policy Decision `2.0.0` and actual Gateway PEP/Lua-generated confirmed admission. The historical independent results below remain separate evidence.

| Affected check | Actual result |
| --- | --- |
| Cross-package C# compatibility | 53 checks PASS, exit 0; [exact check log](fixtures/integration-checks.txt) |
| Synthetic committed backend and ledger | one backend execution, one 12 IRR consumer debit, one usage record/outbox; duplicate event and new delivery/attempt produce no extra debit; [ledger](fixtures/integration-ledger.v1.json) |
| Commit evidence | [flushed backend completion](fixtures/integration-backend-committed.v1.json) binds the immutable business key/result/snapshot; Permit/admission alone has no debit |
| Denial and malformed evidence | Deny, wrong owner/consumer/product/environment/contract/allocation/period/principal, absent/unknown/failed admission, missing/duplicate/unknown obligation, expiry and unresolved references leave debit/receipt/usage/outbox unchanged |
| Versions and period | explicit contract range `1..2147483647` including overflow/fraction rejection; single `policyId@policyVersion`, independent allocation revision `1`, exact October UTC calendar bounds; token/policy/30-second deadline binding |
| Generated event schema | unchanged UsageEvent `1.0` accepts the [actual common event](fixtures/integration-usage-event.v1.json), exit 0 |
| Existing sample regression | 62 checks PASS, exit 0; [new regression log](fixtures/integration-regression-checks.txt) |
| Existing structural suite | 3 historical instances PASS; 26 tests PASS in 0.148s, exit 0 |

The generated [input/source/output SHA-256 manifest](fixtures/integration-evidence.v1.json) pins the exact predecessor files and the generated common event, ledger, backend evidence and check log. Copies under `fixtures/` retain the exact output bytes. The common tuple is owner `11111111-1111-4111-8111-111111111111`, consumer `22222222-2222-4222-8222-222222222222`, product `organization-fabric`, contract `fixture-contract-b@7`, allocation `fixture-shared-organization` / `2026-10`. Policy is `fixture-organization-unit-save@3`. Decision is referenced through `g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`; confirmed admission is `admission:g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`. Actor is the Gateway subject UUID, and business operation/attempt UUIDs remain separate.

Final hash verification: all 16 manifest entries matched current bytes using `Get-FileHash -Algorithm SHA256`; all five published final evidence files matched their generated originals byte-for-byte. The final Gateway witness hash is `ff9131c1bae7fbe211673e1151bfca73ab57194f61b2c6b4054001f22608ae15`; accounting manifest hash is `49c757132ad0b10e5e17f690bc3363d880cd38b5063567b0165d7e55b39b9a16`. UsageEvent hash is `502757d9cd46998f5947a6f18086b2c862655b744d41788e1bdd8e8a6f025b0b`; ledger hash is `841359ba1c7e26670c4d32431467230056af707ba660e7ecf58c34799817fc2c`. The full Decision/source/output hashes remain in the manifest.

Commands actually executed from `E:/SJVS/Projects/Neo`:

```powershell
dotnet run --project docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/UsageAccounting.Sample.csproj --property:UseArtifactsOutput=true --property:ArtifactsPath=E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/results/compatibility-build-20261010 --property:NuGetAudit=false -- E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/usage-accounting/fixtures/usage-event-v1.json E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/results/compatibility-regression-20261010
dotnet run --project docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/UsageAccounting.Sample.csproj --property:UseArtifactsOutput=true --property:ArtifactsPath=E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/results/compatibility-build-20261010 --property:NuGetAudit=false -- --integration E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/integration/fixture-binding.v1.json E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/contract-policy/fixtures/integration-decision.v2.json E:/SJVS/Projects/NeoCluster/fanasa-platform-workstreams/api-gateway/fixtures/integration-admission.v1.json E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/results/integration-final-20261010
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py
python -B -m unittest discover -s docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check -p 'test_*.py' -v
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py --event docs/fanasa-platform-control-center/workstreams/usage-accounting/fixtures/integration-usage-event.v1.json
```

Initial sandboxed `run.ps1` failed with MSB3491 access denied to `sample/.artifacts/...CoreCompileInputs.cache`; a fresh confined build path also hit `NuGet.targets` access denied. The same local build with approved filesystem permission succeeded. The first sandboxed evidence-copy command was denied, so the immediately following generated-event schema call reported a missing file; the approved exact-copy retry and schema check succeeded. The first 53-check common run used `sample/results/integration-accepted-20261010`; final evidence was regenerated in `integration-final-20261010` against the Gateway's stable artifact bytes. These were local output permissions, with no runtime/production mutation or approval rejection. Build/DB outputs stayed within this workstream.

Changed package files: `sample/Program.cs`, `sample/ProposedUsageIntake.cs`, new `sample/IntegrationCompatibility.cs` and `sample/run-integration.ps1`, `schema-check/validate.py`, `schema-check/README.md`, `README.md`, `CONTRACT.md`, this evidence file, new `COMPATIBILITY.md` and six `fixtures/integration-*` files. The original schema, standalone/rate/reconciliation fixtures, key construction and deduplication rules are unchanged. All modified files are inside usage-accounting.

This proves a local generated predecessor chain and accounting compatibility. Authentication/provenance of live producer/PDP/Gateway resolvers, real historical rates/contracts/products, native APISIX/Redis/TLS, backend data-tenant authorization and product runtime wiring remain acceptance gates. The proposed ledger append is still a sample seam using existing Fabric/Accounting models; it is not an installed production debit API, new accounting framework or storage engine. Revenue share, payout and settlement remain undefined.

## اجرای موفق

| بررسی | نتیجه |
| --- | --- |
| تعریف schema و fixtureها | PASS؛ سه UsageEvent instance: یک standalone و دو عضو reconciliation.events |
| آزمون validator ساختاری | ۲۶ test، exit code 0؛ اجرای نهایی root: 0.174s |
| C# sample پس از اصلاح‌های review | ۶۲ check، exit code 0؛ ثبت اجرای نهایی در sample/VERIFICATION.txt |
| ledger مستقل | دو usage entry؛ ۱۲ + ۱۸ = ۳۰ IRR؛ outstanding=۳۰، wallet=۰، availableCredit=۹۹۷۰ |
| نرخ و طرفین | consumer بدهکار، owner بدون بدهی؛ tariff جاری ۱۸ تغییر نکرده و event v7 با نرخ ۱۲ ثبت شده است |
| backup/restore واقعی | FabricRecovery از هر دو DB؛ snapshot با بدهی ۱۸، restore همان ۱۸، replay اصل event گم‌شده و نتیجهٔ نهایی ۳۰ |
| outbox/sink | دو MeasuredUsageCharged؛ سه attempt در آزمون ack-crash، دو receipt مقصد؛ replay پس از restore نیز دو receipt یکتا نگه می‌دارد |

[log قابل ادغام ۶۲ check](fixtures/verified-checks.txt) و [ledger خروجی واقعی sample](fixtures/verified-ledger.json) از run نهایی کپی شده‌اند. [expected fixture](fixtures/reconciliation-v1.json) مرجع ورودی مستقل است؛ نتیجه با آن دقیقاً تطبیق کرده است. output اصلی در `sample/results/verified-final/` است و DBهای موقت/build outputs ignored هستند. [sample/VERIFICATION.txt](sample/VERIFICATION.txt) خلاصهٔ اجرا را نگه می‌دارد.

## فرمان‌های بازتولید

از `E:/SJVS/Projects/Neo`:

```powershell
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py
python -B -m unittest discover -s docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check -p 'test_*.py' -v
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/run.ps1
```

wrapper یک output directory تازه و تصادفی می‌سازد؛ output موجود را overwrite نمی‌کند. build همهٔ ProjectReferenceها به `sample/.artifacts/` هدایت می‌شود. packageهای موجود محصول استفاده شده‌اند؛ dependency جدیدی برای این sample اضافه نشده است. NuGetAudit فقط در build محلی fixture غیرفعال است، چون audit شبکه‌ای در محیط در دسترس نبود. این اجرا audit امنیت package یا انتشار artifact نیست.

## رفتارهای آزموده‌شده

- duplicate payload همسان بی‌اثر؛ JSON property order بی‌اثر؛ payload متفاوت با eventId ثابت conflict.
- eventId تازه با operation همسان فقط receipt؛ actor، payload، acceptedAt، rate یا contract متفاوت برای همان operation conflict.
- exception قبل از commit تمام receipt/event/ledger/outbox را rollback می‌کند؛ exception بعد از commit و reopen همان receipt را بدون debit تازه برمی‌گرداند.
- unknown پس از commit backend، restart بدون blind replay، نبود status غیرمرجع بدون اجرای تازه و inquiry با همان business key.
- unknown completed با credit ناکافی، pending durable خارج از outer transaction، سپس credit resolution و ثبت یک charge بدون backend replay.
- pending همان event و همان operation پس از funding، payload قابل تعویض نیست؛ چند alias pending با یک charge همگی resolved می‌شوند.
- race بین شکست capacity و retention pending، receipt موفق هم‌زمان را overwrite نمی‌کند.
- event دیررس با rate قبلی، rejection مسیر ناامن legacy، و regression واقعی AccountingStore که به‌جای command.UnitPrice=۱۲ از tariff=۱۸ استفاده می‌کند.
- دورهٔ صادرشده به accounting-pending می‌رود؛ invoice موجود تغییر نمی‌کند.
- duplicate JSON names، UTC نامعتبر و `acceptedAt <= occurredAt <= producedAt` پیش از mutation کنترل می‌شوند.
- actual FabricRecovery verify/restore و authoritative replay؛ consumer sink که از backup جلوتر است duplicate را جذب می‌کند.

زمان LedgerEntry.At در sample از clock ثابت ثبت حساب `2026-10-10T08:00:00Z` می‌آید؛ occurredAt مستقل در immutable usage و outbox کامل حفظ می‌شود. charge IDs و logical keys پایدارند؛ source replay از eventهای اصلی با payload ثابت انجام شده است.

## حدود این evidence

validator Python یک **limited offline keyword profile** است و general JSON Schema conformance را اثبات نمی‌کند. format assertions و تمام keywordهای استفاده‌شده را بررسی می‌کند و keyword ناشناخته را رد می‌کند. در intake واقعی، validator کامل مورد پذیرش Draft 2020-12 و trusted domain validation لازم‌اند.

C# از FabricDatabase، AccountingStore.Read و BillingAccount/LedgerEntry واقعی reuse می‌کند، اما append snapshot هنوز **پیشنهادی** است. producer resolver، backend status و sink مصنوعی‌اند؛ real contract/projection/Gateway/PEP/admission وصل نشده‌اند. producer precommit integration، abrupt process kill، power/volume loss، چند worker/replica و workload ده‌هزار event T-04 در این اجرای جدید آزموده نشده‌اند. relay paging در کد وجود دارد، اما workload بیش از یک batch و ظرفیت worker جدا سنجیده نشده است.

backend شبیه‌سازی‌شده یک business receipt فایل محلی می‌سازد و response را از دست می‌دهد؛ endpoint واقعی OrganizationApi به این adapter وصل نشده است. source tests موجود در WIRING/RECOVERY مرجع طراحی‌اند؛ suite محصول در این کار دوباره اجرا نشده است. هیچ نتیجهٔ fixture، پذیرش عملیاتی M1/M2، RPO/RTO واقعی، SLA، قرارداد امضاشده، revenue share یا payout مصوب نیست.

[اثر انگشت منابع بررسی‌شده](fixtures/source-baseline.json) SHA-256 شش سورس اصلی را ثبت می‌کند؛ این manifest کامل artifact/deployment نیست. دو سند مرجع و فایل‌های مشترک Program.cs، persistence، registry و migrations در این workstream تغییر نکرده‌اند؛ تغییرات موجود دیگر چت‌ها حفظ شده‌اند. Skill/MCP runtime موجود قراردادی عوض نکرده و companion آن‌ها تغییر نکرده است. scoped package و پیشنهاد ادغام در [WIRING](WIRING.md) قرار دارند.
