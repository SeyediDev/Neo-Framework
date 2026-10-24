# اتصال پایه Contract Policy به PCC — نسخه 0.3.0

این راهنما اتصال پایه `0.3.0` را ثبت می‌کند؛ ادامه جاری `0.4.0` در [راهنمای Gateway](PCC-GATEWAY.md) آمده است. نسخه ساخت کتابخانه بومی در `Directory.Build.props` همچنان `0.2.0` است. کتابخانه، schemaها و fixtureها تغییر نکرده‌اند؛ NuGet package منتشر نشده است. [شاهد پایه](EVIDENCE.v0.3.0.md) و [manifest تاریخی](manifest.v0.3.0.json) این تفکیک را ثبت می‌کنند.

در اتصال پایه، سورس در میزبان موجود `Fanasa.AccessManagement.Web` کامپایل و ثبت شد. `FabricServices.AddIdentityOrganizationFabric` تابع `ContractPolicyHostServices.AddContractPolicyHost` را فراخوانی می‌کرد؛ ادامه 0.4.0 از `AddContractPolicyHostWithGatewayContext` در [راهنمای Gateway](PCC-GATEWAY.md) استفاده می‌کند. فایل پروژه، کتابخانه native و `pcc/*.cs` را reference/link می‌کند. این اتصال پایه `Program.cs`، schema تجاری قرارداد، registry، migrations، ledger و Gateway را تغییر نداد. پیکربندی پیش‌فرض، producer و ارزیابی محصول را فعال نمی‌کند و هیچ داده عملیاتی seed نمی‌شود.

فایل‌های اتصال: [store](pcc/ContractPolicyStore.cs)، [security](pcc/ContractPolicySecurity.cs)، [reader/decision/admission](pcc/ContractPolicyHost.cs) و [HTTP APIs](pcc/ContractPolicyApi.cs). فقط پروفایل `fanasa.contract-policy/2` برای محصول ثبت‌شده `organization-fabric` به این میزبان متصل است؛ v1 همچنان regression کتابخانه مستقل است.

## مسیر درخواست

| مسیر | احراز هویت / ورودی | رفتار |
| --- | --- | --- |
| `POST /api/contract-policy/projection-events` | scheme اختصاصی `ContractProducerBearer`؛ envelope کامل و نرمال‌شده | JWT امضاشده با issuer/audience/lifetime معتبر، client و scope ثبت‌شده جاری؛ سپس ذخیره اتمی projection/receipt/allocation/outbox. |
| `POST /api/contract-policy/tenants/{tenantId}/organization.unit.save/evaluate` | scheme موجود `OrganizationBearer`؛ body ندارد | tenant از route، subject از JWT، environment/zone از سرور؛ Decision بدون اجرای obligation یا تغییر business data. `Cache-Control: no-store`. |
| `GET /api/contract-policy/administration/tenants/{tenantId}/policy` | cookie و `PlatformAdmin` | سیاست منتشرشده مستقل. |
| `POST .../policy` و `POST .../policy/retire` | cookie، `PlatformAdmin` و antiforgery | انتشار نسخه جدید یا بازنشستگی با `expectedRevision` و actor/reason ثبت‌شده. |
| `GET .../role-assignments/{subject}` و `POST .../role-assignments` | cookie و `PlatformAdmin`؛ POST همچنین antiforgery | انتساب صریح و زمان‌دار به `AccessRole` واقعی محصول؛ grant مستقیم ایجاد نمی‌کند. |

JWTها توسط ASP.NET JwtBearer اعتبارسنجی می‌شوند، نه با decode محلی یا هویت ساختگی. claim mapping خام است؛ skew producer صفر است. زمان عددی canonical و property تکراری JWT رد می‌شود. هویت تکمیلی و احرازنشده Fabric هیچ claim امنیتی تأمین نمی‌کند. producer قبل از خواندن body، داخل transaction و پیش از commit دوباره بررسی می‌شود؛ انقضا یا ابطال دیرهنگام تمام writes را rollback می‌کند. UTF-8 نامعتبر و بیش از 65,536 کاراکتر پذیرفته نمی‌شود.

بدون Bearer معتبر پاسخ `401` است؛ producer احراز‌شده ولی فاقد binding جاری `403` می‌گیرد. JSON نامعتبر `400`، اندازه بیش از حد `413` و تعارض یا binding نامعتبر `409` است. در پایه 0.3.0، `applied`، `duplicate` و `stale` با `200` گزارش می‌شدند؛ در ادامه 0.4.0، نبود رسید معتبر observed پاسخ `202` می‌دهد و فقط acknowledgement پایدار با `phase=acknowledged` پاسخ `200` دارد؛ [قرارداد جاری](PCC-GATEWAY.md). کمبود یا خطای منبع معتبر PDP به `Indeterminate` با obligations خالی می‌رسد؛ شکست شروط دسترسی شناخته‌شده Permit نمی‌سازد. body غیرخالی endpoint ارزیابی `400` است.

## پیکربندی صریح، بدون provisioning

این کلیدها باید از پیکربندی معتبر استقرار تأمین شوند؛ مثال‌ها نشانی یا registration واقعی ایجاد نمی‌کنند:

```text
Authentication:Authority                  = issuer مرکزی HTTPS با مسیر دقیق /realms/fanasa
ContractPolicy:Producer:Enabled            = true
ContractPolicy:Producer:Authority          = دقیقاً Authentication:Authority
ContractPolicy:Producer:Audience           = fanasa-access-management-web
ContractPolicy:Producer:ProductKey         = کلید ثبت‌شده محصول مالک client سرویس قرارداد
ContractPolicy:Producer:ClientId           = client سرویس قرارداد ثبت‌شده و فعال
ContractPolicy:Producer:Scope              = scope اختصاصی مجاز ثبت‌شده برای همان client
Organization:Bearer:Enabled                = true
Organization:Bearer:Authority              = issuer مرکزی
Organization:Bearer:Audience               = fanasa-organization-fabric-api
Organization:Bearer:ClientId               = client ثبت‌شده و فعال محصول سازمان
Organization:Bearer:Scope                  = organization.unit.save
ContractPolicy:Organization:Enabled        = true
ContractPolicy:Organization:Environment    = شناسه محیط تحت کنترل سرور
ContractPolicy:Organization:Zone           = شناسه zone تحت کنترل سرور
ContractPolicy:Organization:MaximumSnapshotAgeSeconds = 5
```

حد freshness قابل پیکربندی `1..30` ثانیه است؛ سقف native v2 همچنان ۳۰ ثانیه است. `ProductKey` producer محصول مالک client فرستنده است؛ audience API خودش لازم نیست audience گیرنده PCC باشد. هیچ `RegisterClient`، grant، نقش، قرارداد، نرخ یا subscription برای فعال‌سازی خودکار فراخوانی نمی‌شود. پیکربندی SSL/CA شبکه باید با زیرساخت معتبر استقرار سازگار باشد؛ آزمون محلی از metadata و کلید RSA مستقل استفاده می‌کند.

## مالکیت داده و هم‌بستگی

منبع تجاری، envelope نرمال‌شده producer مجاز است؛ `offerId`، `subscriptionId` و مرجع قیمت opaque و متعلق به آن منبع می‌مانند. DTO قدیمی `ContractEntitlement` نسخه و bindingهای لازم را ندارد؛ هیچ نسخه، قیمت یا شناسه subscription از آن حدس زده نمی‌شود. سازنده envelope مسئول mapping معتبر از قرارداد امضاشده است؛ این اتصال اصالت قرارداد تجاری مستقل از JWT را اثبات نمی‌کند.

در Fabric موجود، state کامل و تمام receiptها با read/project/write در یک `BEGIN IMMEDIATE` ذخیره می‌شوند. transaction هم‌زمان با عملیات async اجرا نمی‌شود. duplicate با eventId تازه و stale نیز receipt را حفظ می‌کنند؛ replay دقیق write مؤثر یا outbox جدید نمی‌سازد. conflict/rejection تغییری نمی‌دهد. scope و allocation ثابت‌اند؛ configuration مشترک allocation در سند مستقلی با revision افزایشی ثبت می‌شود. همان revision با limits متفاوت یا revision عقب‌تر پذیرفته نمی‌شود. projection عقب‌مانده از configuration مشترک در PIP قابل استفاده نیست؛ هیچ counter reset نمی‌شود.

سیاست server یک منبع نسخه مستقل است: `policyId` ثابت، `policyVersion` افزایشی و محتوای هر ID/version immutable است. revision مدیریتی با نسخه قرارداد یا policyVersion برابر فرض نمی‌شود. binding سیاست پذیرفته‌شده در projection باید دقیقاً با سیاست فعال منتشرشده برابر باشد؛ mismatch/retirement Permit را می‌بندد. انتشار بعد از retirement به policyVersion جدید نیاز دارد.

PIP در یک transaction همگام از tenant، عضویت، grant، انتساب نقش، سیاست، projection و allocation می‌خواند. registry مرکزی پایگاه داده جدا دارد؛ atomicity بین دو store ادعا نمی‌شود. ثبت محصول/مالک و client تازه بررسی می‌شوند و client/زمان پس از read دوباره بررسی می‌شوند. تطبیق UUID با داده native دقیق است؛ spelling آن تغییر نمی‌کند. برای consumer فقط یک projection پذیرفته‌شده باید وجود داشته باشد؛ چند projection مبهم باعث نبود snapshot می‌شود، نه انتخاب خودکار حقوق بیشتر یا merge قراردادها.

انتساب نقش باید `AccessRole` همان consumer/product با کلید `ApiInvoker` و permission واقعی `organization.write` باشد. این binding به‌تنهایی grant نیست: grant مستقیم جاری `organization.write` نیز لازم است. mapping محدود سرور این permission حاضر را به action `organization.unit.save` نگاشت می‌کند، مشروط به وجود همان scope در JWT و registration. نقش سازمانی/انتصاب شغلی، claim کاربر و خرید قرارداد جای این دو سند را نمی‌گیرند. Deny/error یا obligation اجرا‌نشده در سیاست موجود `AccessPolicyStore` نیز veto است؛ Permit قدیمی نقش یا grant گمشده را تأمین نمی‌کند.

عضویت فعلی زمان پایان ندارد؛ تاریخ پایان context فقط سقف مشاهده کوتاه است و به‌عنوان expiry پایدار عضویت ذخیره نمی‌شود. زمان assignment و grant صریح حفظ می‌شود. deadline خروجی کمینه عمر token، source evidence، observation freshness، پنجره‌های معتبر و سقف پروفایل است. snapshot/Decision cache یا fallback به Permit قبلی وجود ندارد.

## اجرای عملیات و مرز Gateway

`ContractPolicyOrganizationAdmission` به `IOrganizationUnitSaveAdmission` موجود متصل است. این wrapper تصمیم native را می‌گیرد، `IContractPolicyObligationExecutor` مورد اعتماد سرور را برای همان tenant/actor/request UUID/payload فراخوانی می‌کند، سپس سیاست و اطلاعات دسترسی را دوباره می‌خواند و PEP را بررسی می‌کند. timeout/exception، completion ناقص، اضافی یا تکراری، تغییر نسخه/limits، انقضای token یا ابطال grant اجازه اجرا نمی‌دهد. body و header نمی‌توانند completed IDs تأمین کنند. helper بومی حضور همه شناسه‌های لازم را بررسی می‌کند؛ wrapper میزبان علاوه بر آن، مجموعه دقیق و بدون تکرار را لازم می‌داند.

executor پیش‌فرض `IsConfigured=false` و نتیجه خالی دارد. مسیر واقعی `unit.save` همچنان metering، boundary جاری و پذیرش قطعی Gateway را لازم دارد؛ یک پاسخ PDP با `Permit` این مسیر را باز نمی‌کند. در تحویل پایه `0.3.0` گیرنده witness وجود نداشت؛ ادامه [Gateway در 0.4.0](PCC-GATEWAY.md) گیرندهٔ mTLS/HMAC و guard پیش از commit را با فعال‌سازی صریح اضافه می‌کند. اجرای شمارنده، seat admission و budget در مالکیت Gateway باقی می‌ماند.

## آزمون و حدود شاهد

```powershell
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Pcc.Tests/Fanasa.ContractPolicy.Pcc.Tests.csproj
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Bearer.Tests/Organization.Bearer.Tests.csproj
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests/Organization.Tests.csproj
```

PCC tests از controller و DI واقعی، Kestrel روی loopback، JWT امضاشده با RSA موقت و SQLite مستقل استفاده می‌کنند. امضای غلط، issuer/audience/expiry، ابطال client/عضویت/grant/نقش/سیاست، replay، restart، دو نویسنده، rollback و عدم تغییر organization/usage/ledger در PDP بررسی می‌شوند. این شاهد محلی جای registration زنده، قرارداد امضاشده واقعی، ارتباط TLS/Redis/APISIX یا پذیرش end-to-end عملیاتی را نمی‌گیرد. نتیجه نهایی ۱۱۴ آزمون PCC، ۳۴۴ آزمون native، ۷۷ بررسی Bearer و ۲۴۸ بررسی سازمان، همگی با صفر شکست، در [evidence نسخه 0.3.0](EVIDENCE.v0.3.0.md) ثبت شده است.

قرارداد عمومی Neo Framework، Neo MCP یا recipe مهارت‌ها تغییر نکرده است؛ تغییر companion در راهنمای همین ماژول و README میزبان ثبت می‌شود. ادعای موتور کامل یا انطباق کامل OASIS XACML وجود ندارد.
