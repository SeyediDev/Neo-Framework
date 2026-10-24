# سازگاری میزبان فعلی با HR3

این شاخهٔ محلی فایل مشترک PCC یا بسته‌های ثابت HR/PDP را تغییر نمی‌دهد. نسخهٔ مبنا و هش نسخهٔ کپی‌شده در `source-pin.json` ثبت شده‌اند.

## انتخاب قرارداد قدیمی

`ContractPolicyStore.Find(Guid)` قبلاً تمام projectionها را فقط با consumer انتخاب می‌کرد. وجود یک قرارداد HR3 در کنار قرارداد v2 همان consumer نتیجه را مبهم می‌کرد. افزون بر آن، اعتبارسنج خصوصی store فقط schema 2.0.0 را می‌پذیرفت.

تفاوت کپی محدود است: فقط `Find` اجازه می‌دهد HR3 با schema و profile دقیق، از همان کنترل محصول، validator کامل ماژول و کنترل receipt عبور کند؛ سپس فقط schema `2.0.0` و profile `fanasa.contract-policy/2` را برای مسیر قدیمی انتخاب می‌کند. همهٔ رکوردها پیش از فیلتر اعتبارسنجی می‌شوند. رکورد خراب، schema ناشناخته یا receipt نامعتبر پنهان نمی‌شود. دو قرارداد v2 همچنان نتیجهٔ نامعتبر می‌دهند. `Read` و `Apply` همچنان مرز v2 خود را حفظ می‌کنند.

## ادغام سورس و هویت نوع‌ها

میزبان در `products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj` یک reference به core مشترک و Compile لینک‌شده برای `implementation/pcc/*.cs` دارد. ادغام باید دقیقاً یک core، یعنی پروژهٔ immutable HR3 `contract-policy/src/Fanasa.ContractPolicy/Fanasa.ContractPolicy.csproj` را انتخاب کند. reference قدیمی باید پیش از resolve حذف شود. از میان سورس‌های لینک‌شدهٔ PCC فقط `ContractPolicyStore.cs` مبنا با کپی این پوشه جایگزین می‌شود.

سورس‌های receiver `src/*.cs`، HTTP `http/*.cs`، فقط `fabric/HrReadFabricAdapter.cs` و PDP `src/*.cs` در assembly میزبان کامپایل می‌شوند. **فایل `fabric/FabricDatabase.cs` و ProjectReferenceهای Fabric/PDP نباید دوباره وارد میزبان شوند**؛ آن پروژه‌ها یک نوع دوم با نام کامل یکسان می‌سازند. نوع میزبان و نسخهٔ pin‌شده در بستهٔ HR3 اکنون byte-identical هستند: SHA-256 `7de828e9e414b1c9cd2582e50b72bf2270fd43263429690bec91820252eea38b`. از همان singleton ثبت‌شده در `FabricServices` استفاده می‌شود. بازکردن database مستقل یا مسیر جدید لازم نیست.

مجموعهٔ ۱۴ schema بستهٔ HR3 باید در `Schemas` خروجی میزبان و publish قرار گیرد؛ این مجموعه همان ۱۰ schema قدیمی و چهار schema جدید v3 را در بر دارد. نسخه‌های JwtBearer و Sqlite میزبان و قراردادها اکنون `10.0.8` هستند.

## برخورد مسیر و نام scheme

`OrganizationHrApi.cs` از قبل این مسیرها را دارد:

- `[Route("api/organization-hr/v1/tenants/{tenantId:guid}")]`، خط ۱۴؛ `GET chart`، خط ۳۷؛ `GET history/{revision:long}`، خط ۴۰.
- `HrReadHttpEndpoint.MapHrReadCandidate` همین URIها را با route parameter بدون constraint تعریف می‌کند. ثبت هم‌زمان به معنای فعال‌شدن قطعی HR3 نیست: precedence مسیر constrained قدیمی می‌تواند درخواست معتبر را به read action قدیمی ببرد. قبل از فعال‌سازی receiver، باید دقیقاً همان دو read action در حالت HR3 جایگزین/غیرفعال شوند و انتخاب واقعی endpoint آزمون شود. actionهای نوشتن و probe نباید همراه کل controller حذف شوند.
- scheme با نام `OrganizationHrBearer` از قبل در `OrganizationHrSecurity.AddOrganizationHrSecurity` ثبت شده است. receiver آن را مصرف می‌کند؛ ثبت مجدد همین نام صحیح نیست. دو scheme جدید PDP یعنی `HrReadPdpGatewayBearer` و `OrganizationHrPdpSubjectBearer` نام‌های متفاوت دارند و default cookie انسانی را تغییر نمی‌دهند.
- مسیر `POST /internal/contract-policy/hr-read/v3/evaluate` با مسیر قدیمی Gateway/PDP برخورد ندارد؛ ثبت route به‌تنهایی مجوز یا پذیرش زنده نمی‌سازد.

## APIهای اختیار فعلی

`PlatformRegistry.Products()` مرجع مرکزی `Id/Key/Audience/Owner/TenantId` را می‌دهد و database جداگانه دارد. `PersistentAccessManagement.GetTenants/GetUsers/GetClients/GetRoles/GetGrants` از Fabric موجود می‌خوانند. برای veto قدیمی، `AccessPolicyStore.Evaluate` خواندنی است؛ `Authorize` می‌تواند audit بنویسد و داخل snapshot فقط‌خواندنی مناسب نیست. `OrganizationHrBoundary.Allows` کنترل جاری client/member/grant را دارد، اما نتیجهٔ آن جایگزین introspection جاری، نگاشت یکتای consumer، delegation مخصوص Gateway، role مخصوص HR3 یا اثبات allocation نیست.

writer قدیمی `ContractPolicyStore.Apply` فقط v2 را قبول می‌کند؛ policy و assignment قدیمی نیز کلید consumer-only، نقش `ApiInvoker` و مجوز write دارند. این داده‌ها به‌صورت ضمنی به رکوردهای scoped HR3 تبدیل نمی‌شوند. publisher HR3 باید رکوردهای مستقل و صریح در bucketهای موجود بنویسد.

آزمون‌های `CompatibilityTests.Run()` فقط fixture مصنوعی و SQLite موقت را بررسی می‌کنند؛ اثبات دسترسی عملیاتی، انتشار، introspection یا Gateway زنده نیستند.
