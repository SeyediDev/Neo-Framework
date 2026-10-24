# ماژول بومی Contract Policy برای .NET

نسخه جاری تحویل سورس `0.4.0` است؛ سورس بومی و نسخه ساخت `0.2.0` حفظ شده‌اند، ولی DLL دوباره ساخته و آزمون شده است. [اتصال پایه PCC](PCC-INTEGRATION.md) و [مسیر امضاشده Gateway](PCC-GATEWAY.md) رفتار جاری را توضیح می‌دهند؛ نتایج در [EVIDENCE.v0.4.0.md](EVIDENCE.v0.4.0.md) و اثرانگشت فایل‌ها در [manifest.v0.4.0.json](manifest.v0.4.0.json) ثبت می‌شوند. API adapterها در [ADAPTERS.md](ADAPTERS.md) آمده‌اند. evidence/manifest نسخه‌های قبلی تاریخی هستند و hashهایشان نماینده سورس جاری نیست.

این پوشه کتابخانه مستقل `Fanasa.ContractPolicy`، اتصال میزبان و برنامه‌های آزمون .NET 10 است. کتابخانه هیچ `PackageReference` ندارد؛ اتصال PCC از وابستگی‌های موجود میزبان استفاده می‌کند. schemaها هنگام startup محلی کامپایل می‌شوند و evaluator/projector خالص‌اند. DI و endpointها در سورس PCC ثبت شده‌اند؛ فعال‌سازی به پیکربندی صریح نیاز دارد و executor پیش‌فرض Gateway بسته است. استقرار عملیاتی و انطباق کامل XACML ادعا نمی‌شود.

هر دو پروفایل fixture مصنوعی‌اند؛ شناسه محصول در fixture شاهد ثبت یا اجرای واقعی آن محصول نیست.

| پروفایل | عملیات و audience | سقف عمر Permit | allocation |
| --- | --- | --- | --- |
| `fanasa.contract-policy/1`، schema `1.0.0` | `report.read`، `fanasa-club-api` | ۶۰ ثانیه | `configRevision` وجود ندارد. |
| `fanasa.contract-policy/2`، schema `2.0.0` | `organization.unit.save`، `fanasa-organization-fabric-api` | ۳۰ ثانیه | `configRevision` مستقل، مثبت و اجباری است. |

`validUntil` زودترین حد قرارداد، membership، grant، evidence و سقف پروفایل است. خرید فقط eligibility ایجاد می‌کند؛ Permit علاوه بر قرارداد فعال به هویت معتبر، tenant و membership فعال، grant صریح، محصول/عملیات، scope و zone معتبر نیاز دارد. PDP هیچ grant، شمارنده، رزرو، usage، budget یا ledger را تغییر نمی‌دهد.

## ساخت و اجرا

از ریشه workspace یعنی `E:/SJVS/Projects/Neo` با SDK .NET 10 اجرا کن. build برنامه آزمون، کتابخانه و schemaها را نیز می‌سازد؛ fixtures برای همین برنامه در پوشه خروجی کپی می‌شوند.

```powershell
dotnet build docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --nologo
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --no-build
```

برنامه آزمون console است؛ شکست با exit code غیرصفر گزارش می‌شود. نتیجه اجرا جای پذیرش یک محصول واقعی و آزمون end-to-end را نمی‌گیرد.

## API و startup

`ContractPolicyModule.FromOutputDirectory()` schemaهای پوشه `Schemas` کنار خروجی برنامه را بار می‌کند. اگر host مسیر دیگری دارد، `new ContractPolicyModule(schemaDirectory)` یک مسیر محلی تحت کنترل سرور می‌گیرد. نبود یا ناسازگاری schema باید startup را متوقف کند؛ schema path از درخواست کاربر گرفته نمی‌شود.

```csharp
using Fanasa.ContractPolicy;

var module = ContractPolicyModule.FromOutputDirectory();

static PolicyDecision EvaluateRequest(
    ContractPolicyModule module,
    ContractAccessProjection acceptedProjection,
    AccessEvaluationContext snapshotFromTrustedPip)
    => module.Evaluator.Evaluate(acceptedProjection, snapshotFromTrustedPip);

static ProjectionResult ApplyVerifiedMessage(
    ContractPolicyModule module,
    ProjectionState? storedState,
    ContractProjectionEvent envelope,
    string producerVerifiedByTransport)
    => module.Projector.Apply(storedState, envelope, producerVerifiedByTransport);
```

پارامتر `producerVerifiedByTransport` فقط خروجی adapter سرور پس از احراز هویت و بررسی مجوز producer است. مقدار از body، header خام یا پیش‌فرض `fanasa-contracts` ساخته نمی‌شود. `Apply` تنها producer تأییدشده با نام `fanasa-contracts` و envelope مطابق آن را می‌پذیرد؛ خود کتابخانه token یا امضای پیام را تأیید نمی‌کند.

projector، snapshot کامل را با `aggregateVersion == contractVersion` اعمال می‌کند؛ فاصله نسخه‌ها مجاز است و زمان `effectiveAt` ترتیب aggregate را تعیین نمی‌کند. schema، هویت aggregate، `allocationId` و scope ثابت می‌مانند. replay یک envelope تکراری را می‌شناسد و تغییر همان event ID یا snapshot متفاوت در نسخه برابر تعارض است. `Ended` به وضعیت غیرپایانی برنمی‌گردد. خروجی state و receiptها کپی مستقل دارند؛ ورودی تغییر نمی‌کند.

در v2، نسخه جدید aggregate می‌تواند همان `configRevision` را فقط با limits یکسان نگه دارد؛ تغییر limits نیازمند revision بالاتر است. revision پایین‌تر رد می‌شود و revision، جزئی از کلید مصرف نیست. کلید rate/quota را `module.Projections.CounterKey(...)` از allocation و scope پایدار می‌سازد؛ این تابع شمارنده ایجاد یا reset نمی‌کند.

## اتصال جاری و سابقه wiring

مسیر host متصل `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web` است. reference زیر اکنون در فایل پروژه وجود دارد و `pcc/*.cs` نیز در همان assembly کامپایل می‌شوند. `FabricServices` ثبت `AddContractPolicyHostWithGatewayContext` را فراخوانی می‌کند؛ اجرای امضاشده از guard داخل تراکنش سازمان استفاده می‌کند. پیکربندی در [راهنمای پایه](PCC-INTEGRATION.md) و [راهنمای Gateway](PCC-GATEWAY.md) ثبت شده است. مثال‌های زیر مرز API کتابخانه را نشان می‌دهند:

```xml
<ProjectReference Include="../../../../docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/src/Fanasa.ContractPolicy/Fanasa.ContractPolicy.csproj" />
```

composition پایه کتابخانه که ثبت میزبان نیز انجام می‌دهد:

```csharp
var contractPolicy = ContractPolicyModule.FromOutputDirectory();
builder.Services.AddSingleton(contractPolicy);
builder.Services.AddSingleton<IContractPolicyDecisionEvaluator>(contractPolicy.Evaluator);
builder.Services.AddSingleton<IContractProjectionProjector>(contractPolicy.Projector);
builder.Services.AddSingleton(contractPolicy.Pep);
```

adapter ورودی مسئول هویت و مجوز producer، mapping تأییدشده business event و مالکیت offer/subscription است. store باید state معتبر و receipt را در یک transaction با کنترل نسخه یا compare-and-swap ذخیره کند؛ receiptهای `duplicate` با event ID تازه و `stale` هم ممکن است به ذخیره نیاز داشته باشند. نتیجه projector به‌تنهایی تضمین هم‌زمانی چند نویسنده یا انتشار و اجرای policy را نمی‌دهد.

PIP مسئول snapshot قابل اعتماد از هویت/client، registry، consumer tenant، membership، grant واقعی، projection پذیرفته‌شده، policy، zone، زمان سرور و تازگی evidence است. داده client درباره role، tenant، contract version یا limits منبع authority نیست. freshness و ابطال suspend/revoke باید در reader و host اثبات شوند؛ TTL تصمیم به‌تنهایی SLA انتشار را ثابت نمی‌کند.

`ContractEventAdapter` envelope نرمال‌شده را پس از کنترل claimهای producer به projector می‌دهد؛ `TrustedAccessSnapshotProvider` reader مورد اعتماد و `TimeProvider` را به context هم‌بسته تبدیل می‌کند. reader واقعی و Bearer اکنون در `pcc/` پیاده‌سازی شده‌اند؛ mapping تجاری مسئول producer قرارداد است. scheme اختصاصی producer از JWT handler موجود استفاده می‌کند و cache تصمیم اضافه نشده است.

PEP فقط Permit معتبر و همه obligationهای شناخته‌شده را می‌پذیرد. `module.Pep.CanExecute(decision, fulfilledIds, now)` بررسی ساختار، زمان و شناسه‌های completion است؛ `fulfilledIds` اثبات اجرای admission یا بررسی tenant نیست. adapter واقعی باید نتیجه معتبر اجرای obligationها را بگیرد، شکست/timeout/obligation ناشناخته را متوقف کند و پس از آن عملیات را اجرا کند. شمارنده TPS/burst/quota، seat admission، Gateway و Accounting در مالکیت ماژول‌های خود می‌مانند.

## مرز JSON و تفاوت‌های عمدی با مرجع JavaScript

برای JSON ورودی از `ContractJson.Parse` و `ContractJson.Options` استفاده کن و قبل از مصرف، schema و semantics مربوط را بررسی کن. parser نام property تکراری، comment، trailing comma و عمق/تعداد node بیش از حد را رد می‌کند. ورودی `JsonNode` ساخته‌شده با parser دیگر نیز پیش از خواندن attributes در entry pointهای projection، evaluator و PEP به snapshot مستقل و strict تبدیل می‌شود؛ property تکراری به خطای validation، Indeterminate یا توقف PEP می‌رسد. schema validator و converter اعداد صحیح، فقط مقادیر finite در بازه امن JavaScript تا `9007199254740991` را می‌پذیرند؛ نمایش‌های عددی `7`، `7.0` و `7e0` معنای integer یکسان دارند. نام property و شناسه‌ها case-sensitive هستند؛ GUID tenant به lowercase تبدیل نمی‌شود. زمان فقط UTC واقعی با سه رقم millisecond و پسوند `Z` است.

در native، پس از اعتبارسنجی و تثبیت هویت، aggregate قدیمی پیش از مقایسه `configRevision` به `stale` تبدیل و receipt آن حفظ می‌شود. مرجع JavaScript ابتدا revision را بررسی می‌کند و ممکن است همان snapshot قدیمی را به دلیل config قدیمی رد کند. این ترتیب native مانع rollback می‌شود و out-of-order عادی را ثبت می‌کند. anchorهای کامل native نیز newline انتهایی شناسه و period را رد می‌کنند؛ این ورودی در regexهای `^...$` مرجع می‌تواند پذیرفته شود. fingerprint، SHA-256 از JSON با keyهای مرتب، آرایه‌های با ترتیب حفظ‌شده و نمایش عددی سازگار با JavaScript است.

instanceهای ساخته‌شده در startup برای فراخوانی موازی با ورودی‌های مستقل مناسب‌اند. تغییر هم‌زمان همان `JsonNode` یا collection توسط caller پشتیبانی نمی‌شود؛ caller باید ورودی را تا پایان فراخوانی ثابت نگه دارد.

مرجع قرارداد و نمونه‌ها: [schemaها](../schemas/)، [fixture v1](../fixtures/projection.v1.json)، [projection مصنوعی v2](../fixtures/integration-projection.v2.json)، [context v2](../fixtures/integration-context.v2.json)، [event v2](../fixtures/integration-projection-event.v2.json)، [مرجع JavaScript](../sample/contract-policy.cjs) و [تطبیق سورس و wiring](../SOURCE-AND-WIRING.md).
