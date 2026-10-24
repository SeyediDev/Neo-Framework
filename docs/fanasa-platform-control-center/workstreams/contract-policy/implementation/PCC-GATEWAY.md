# اتصال رمزنگاری Gateway و PCC — تحویل سورس 0.4.0

این ادامه endpoint واقعی مورد انتظار APISIX و receiver context امضاشده را به همان میزبان موجود اضافه می‌کند. نسخه ساخت کتابخانه بومی همچنان `0.2.0` است؛ DTO و schema بومی تغییر نکرده‌اند. [اتصال پایه](PCC-INTEGRATION.md) پیش‌نیاز این مسیر است. ثبت DI از `FabricServices` با `AddContractPolicyHostWithGatewayContext` انجام می‌شود؛ هویت، registry و store مالی جدیدی ساخته نمی‌شود.

فایل‌های جدید: [Gateway Decision HTTP](pcc/ContractPolicyGatewayApi.cs)، [JWT اختصاصی Gateway](pcc/ContractPolicyGatewaySecurity.cs)، [PIP Gateway](pcc/ContractPolicyGatewayDecision.cs) و [receiver/executor context](pcc/ContractPolicyGatewayContext.cs).

## درخواست تصمیم

`POST /api/gateway/decision` از scheme اختصاصی `GatewayDecisionBearer` استفاده می‌کند. Gateway یک client سرویس مستقل و scope صریح دارد؛ client انسانی سازمان و client producer قرارداد نمی‌توانند با همان تنظیمات نقش Gateway بگیرند. JWT با issuer مرکزی، audience گیرنده PCC، امضا، انقضا و claimهای خام بدون تکرار بررسی می‌شود. ثبت مرکزی client/scope و مالک فعال قبل و بعد از body/PIP دوباره خوانده می‌شود. پیش‌فرض غیرفعال است.

```text
ContractPolicy:GatewayDecision:Enabled    = true
ContractPolicy:GatewayDecision:Authority  = دقیقاً Authentication:Authority
ContractPolicy:GatewayDecision:Audience   = fanasa-access-management-web
ContractPolicy:GatewayDecision:ProductKey = محصول ثبت‌شده مالک client سرویس Gateway
ContractPolicy:GatewayDecision:ClientId   = client سرویس Gateway ثبت‌شده و فعال
ContractPolicy:GatewayDecision:Scope      = scope اختصاصی مجاز همان client
```

body دقیقاً شانزده فیلد قرارداد موجود APISIX را می‌گیرد:

```text
schemaVersion = fanasa.gateway-pdp.request.v2
requestId     = g-<32 lowercase hex nonce>
subjectId, clientId, consumerTenantId, ownerTenantId
productId, environment, action, operationGroup, audience, scope
allocationId, allocationScope, requestedAt, tokenExpiresAt
```

`requestedAt` زمان epoch با کسری ثانیه است؛ `tokenExpiresAt` عدد صحیح مثبت canonical است. UUIDها بدون تغییر spelling حفظ می‌شوند. property اضافی، تکراری، coerced یا ناقص پذیرفته نمی‌شود. Gateway معتبر، هویت client انسانی را assert می‌کند؛ یک درخواست انسانی عادی authority این endpoint را ندارد. membership/grant/role/policy در body دریافت نمی‌شوند.

PIP همان tenant، grant مستقیم، انتساب نقش، سیاست منتشرشده، projection و allocation جاری را از منبع موجود می‌خواند. product/audience/client/scope/environment/group/owner و allocation scope باید دقیقاً با binding معتبر سرور برابر باشند. زمان درخواست از سقف freshness تنظیم‌شده نمی‌گذرد و انقضای هویت مصرف‌کننده deadline تصمیم را محدود می‌کند. خروجی Decision بومی، `Cache-Control: no-store` و بدون mutation، admission یا charge است. malformed برابر `400`، oversized برابر `413`، هویت نامعتبر `401` و ابطال binding سرویس `403` است؛ نتیجه PDP معتبر، حتی Deny/Indeterminate، با `200` و مقدار Decision گزارش می‌شود.

## context امضاشده برای عملیات

receiver با قرارداد `adapters/context.lua` و headerهای واقعی `X-Fanasa-Context` و `X-Fanasa-Context-Signature` سازگار است. وجود header یا شناسه admission به‌تنهایی هیچ اجازه‌ای نمی‌دهد. resource filter روی `OrganizationOperationApi.Execute` پیش از model binding، بایت‌های محدودشدهٔ واقعی body را می‌گیرد.

```text
ContractPolicy:GatewayRequest:Enabled = true
ContractPolicy:GatewayRequest:MaximumBodyBytes = 65536
ContractPolicy:GatewayRequest:Keys:0:KeyId = شناسه کلید مورد اعتماد
ContractPolicy:GatewayRequest:Keys:0:Tier = edge یا internal
ContractPolicy:GatewayRequest:Keys:0:Secret = secret خارج از سورس، حداقل 32 بایت UTF-8
ContractPolicy:GatewayRequest:Keys:0:ClientCertificateSha256 = SHA-256 بایت‌های DER گواهی مشتری Gateway
```

نوع تنظیمات این مسیر `ContractSignedGatewayRequestOptions` است. اگر تنظیم قدیمی `ContractPolicy:GatewayContext:Enabled` نیز همراه `GatewayRequest` فعال باشد startup متوقف می‌شود؛ گیرندهٔ جاری فقط از `GatewayRequest` استفاده می‌کند. executor جدید فقط با فعال‌سازی صریح این مسیر ثبت می‌شود؛ در حالت غیرفعال executor پایه بسته می‌ماند.

executor علاوه بر کلیدها به metering فعال و محیط یکسان نیاز دارد؛ در نبود آن `IsConfigured=false` می‌ماند:

```text
UsageMetering:Organization:Enabled = true
UsageMetering:Organization:Environment = همان ContractPolicy:Organization:Environment
```

`ProducerId`، `DeploymentId`، اشتراک/پلن postpaid معتبر و نگاشت مصرف نیز باید مطابق [راهنمای مصرف و حسابداری](../../../../../products/Fanasa.AccessManagement/docs/USAGE-ACCOUNTING.md) فراهم شوند. منبع `configuration` به `Bindings` صریح با snapshot نرخ نیاز دارد؛ منبع `contract-policy` به `ContractLinks` تجاری صریح و قیمت نسخه‌دار پایدار نیاز دارد. این تنظیمات به‌تنهایی تأیید قرارداد یا قیمت تجاری نیستند. کنترل همخوانی binding مصرف با projection جاری در هر دو حالت لازم است.

receiver هم اتصال HTTPS واقعی با featureهای TLS متعلق به یک شیء Kestrel و گواهی مشتری pin‌شده و معتبر در زمان جاری را لازم دارد، هم HMAC را. `X-Forwarded-Proto`، header گواهی و wrapper مربوط به certificate forwarding جای TLS مستقیم را نمی‌گیرند. تنظیم listener برای mTLS مسئول زیرساخت میزبان است؛ این تحویل Program، trust store یا گواهی عملیاتی را تغییر نمی‌دهد. آزمون محلی از گواهی‌های مستقل موقت استفاده می‌کند.

امضا HMAC-SHA256 با hex کوچک است. message از framing طول بایت UTF-8 و `:` برای این هفت مقدار ساخته می‌شود:

```text
fanasa.context.v1
method
raw backend path + query
SHA256(exact Authorization)
SHA256(exact raw body bytes)
SHA256(exact Idempotency-Key, or empty)
canonical base64 envelope
```

plugin بررسی‌شده مقدار `ngx.var.request_uri` را برای امضا به sender می‌دهد. مسیر و query دریافت‌شده در PCC باید همان بایت‌های امضاشده باشند؛ rewrite مسیر/query پس از امضا، HMAC را نامعتبر می‌کند. هماهنگی route و backend یک پیش‌نیاز استقرار است و این harness تغییر مسیر APISIX را اثبات نمی‌کند.

envelope دقیقاً `version=1,keyId,tier,issuedAt,expiresAt,context` است؛ حداکثر عمر ۳۰ ثانیه و حداکثر encoded size برابر ۲۱٬۸۴۸ است. همه کلیدهای context همان ۲۴ فیلد صادرشده از PEP موجودند. nonce `g-...` با business UUID فرمان متفاوت است. `decisionId=requestId` و `admissionId=admission:requestId` باید برابر و `allowed/confirmed` واقعاً boolean true باشند. property تکراری، ناشناخته، base64 غیرcanonical، امضای غلط، key/tier/certificate نامعتبر و deadline منقضی رد می‌شوند.

executor دوباره scheme اصلی `OrganizationBearer` را authenticate می‌کند و subject/client/issuer/audience/scope/انقضای JWT را با context و تصمیم تازهٔ بومی تطبیق می‌دهد. consumer/owner/product/environment/action، policy reference، contract/version، allocation/scope/configRevision و دوره دقیق UTC calendar month باید جاری باشند. command مصرف‌شده باید همان command گرفته‌شده از body امضاشده باشد. شناسه واحد و والد فقط در state همان consumer کنترل می‌شوند.

مرجع metering نیز از همان snapshot ثابت `OrganizationUsageMetering` خوانده می‌شود: contract/version، allocation، ماه، محیط و PricingReference/RateId/RateVersion باید با projection جاری برابر باشند. این mapping محدود، `PricingReference.PriceId/PriceVersion` بومی را به همان `RateId/RateVersion` مستقل و immutable مصرف متصل می‌کند. تنظیم یا fixture، اصالت تجاری یا امضای قیمت را اثبات نمی‌کند.

nonce در همان Fabric با `BEGIN IMMEDIATE` به‌صورت اتمی claim می‌شود و receipt پایدار با کلید `tier:g-nonce` در restart و بین اتصال‌ها مانع replay می‌شود. tombstone پاک یا با key rotation بازنشانی نمی‌شود. veto دیرهنگام nonce را مصرف‌شده نگه می‌دارد؛ refund/retry خودکار quota انجام نمی‌شود. درخواست دوبارهٔ business UUID به context و admission تازه نیاز دارد؛ store موجود مسئول receipt دقیق و جلوگیری از mutation/debit تکراری است.

برای retry دقیق receipt ازپیش‌ثبت‌شده، actor، fingerprint فرمان و history همان tenant/revision باید برابر باشند. هویت، سیاست، allocation، گواهی و مهلت جاری همچنان لازم‌اند؛ اما تعرفهٔ پایان‌یافته دوباره باز نمی‌شود و usage/ledger اولیه تغییر نمی‌کند. این وضعیت در proof قبل از عملیات ثابت می‌شود؛ receipt تازه‌ای که هنگام write ساخته شده، کنترل نهایی نرخ فرمان جدید را دور نمی‌زند.

wrapper فعلی سپس سیاست/مجوز را دوباره می‌خواند، مجموعه دقیق دو completion را می‌سنجد و PEP deadline را بررسی می‌کند. همان executor یک guard همگام را تأمین می‌کند که پس از گرفتن قفل SQLite و پس از تمام writeها، درست پیش از commit اجرا می‌شود. guard هویت اصلی، گواهی، deadline کسری ثانیه، سیاست، allocation و binding مصرف را دوباره بررسی می‌کند؛ خطا کل mutation/history/usage/ledger/outbox را rollback می‌کند و nonce مصرف‌شده باقی می‌ماند. PIP جاری همگام است؛ guard یک ValueTask تکمیل‌نشده را بسته رد می‌کند و داخل تراکنش thread-local انتظار asynchronous ندارد.

executor شمارنده، quota، grant، role، بودجه یا ledger را ایجاد نمی‌کند؛ فقط witness معتبر صادرشده پس از admission را مصرف می‌کند. mutation/history/usage/ledger/outbox در transaction فعلی سازمان باقی می‌مانند. registry و Fabric مستقل‌اند؛ atomicity بین این دو store ادعا نمی‌شود.

## آزمون محلی

از ریشه workspace:

```powershell
dotnet build products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj --nologo
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/Fanasa.ContractPolicy.Gateway.Tests.csproj
```

آزمون Gateway از Kestrel HTTPS واقعی، گواهی‌های موقت مستقل، JWT امضاشده، HMAC و SQLite استفاده می‌کند. signer آزمون، قرارداد framing منتشرشده را مستقل در C# پیاده می‌کند؛ Lua/APISIX خارجی در این harness اجرا نمی‌شود. سناریوها شامل اجرای موفق و ثبت مصرف/بدهکارسازی، replay هم‌زمان و پایدار، retry دقیق business UUID با nonce تازه، امضا/body/path/header نامعتبر، ابطال client/grant/role/policy، tenant منبع ناسازگار، شناسه والد ناشناخته و binding مالی ناسازگار است. دادهٔ منبع متعلق به tenant دیگر در fixture ساخته نشده است. انقضا پس از admission، هنگام write مصرف و یک tick پس از deadline کسری ثانیه باید بدون تغییر business state به `403` برسد. نتایج ساخت، رگرسیون‌ها و hash خروجی‌ها در [شاهد 0.4.0](EVIDENCE.v0.4.0.md) ثبت می‌شوند.

## رسید انتشار کنترل‌پلین

تغییرات هم‌زمان مرکز در [ContractGatewayPublication.cs](pcc/ContractGatewayPublication.cs)، Host، Store و Api نیز در assembly جاری قرار دارند. event پذیرفته‌شده snapshot ثابت publication و hash canonical limits را نگه می‌دارد. در نبود مشاهدهٔ معتبر، ingress با `202` و phase برابر `pending` یا `superseded` پاسخ می‌دهد؛ پذیرش projection به‌تنهایی acknowledgement نیست.

`IContractGatewayObserver` پیش‌فرض پیکربندی نشده است. فقط مشاهدهٔ مورد اعتماد با contractVersion، allocationId، canonicalScope، configRevision، limitsSha256 و observedAt منطبق، همراه مجوز producer جاری و transaction پایدار، می‌تواند `{eventId,aggregateVersion,phase:"acknowledged",gateway:...}` تولید کند. آزمون PCC تطبیق hash با مرجع NeoContract، رد tuple و زمان نادرست، حفظ replay/restart و عدم ثبت acknowledgement در خطای منبع را با observer مصنوعی می‌سنجد. گیرندهٔ درخواست امضاشده جای این مشاهده یا provisioning واقعی limits را نمی‌گیرد.

## حدود تحویل

این receiver و endpoint، پیش‌فرض هیچ credential، product/client، grant، نقش، قرارداد یا نرخ واقعی ایجاد نمی‌کنند. گزینه‌های Gateway و metering غیرفعال باقی می‌مانند و درخواست مستقیم بدون witness معتبر اجازه اجرا ندارد. کد APISIX/Redis خارجی در این تحویل تغییر نمی‌کند؛ secret/keyId/tier، PDP credential، route/backend raw path و گواهی pin‌شده باید با تنظیم معتبر همان Gateway تطبیق داشته باشند.

شاهد محلی با TLS/JWT/HMAC واقعی و داده مصنوعی، ثبت زنده، امضای طرفین قرارداد، قیمت مصوب، provisioning معتبر دوره/limits و استقرار end-to-end عملیاتی را ثابت نمی‌کند. انتشار limits و رسید observed کنترل‌پلین، و پیوند dispatcher قرارداد به آن، مرزی مستقل از این دریافت درخواست است. receipt outbox صرفاً به‌معنای delivery خارجی یا تأیید Gateway نیست.
