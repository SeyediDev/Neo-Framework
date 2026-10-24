# قرارداد UsageEvent 1.0 و مرز billable

این wire schema، قرارداد پیشنهادی محدود یک operation است؛ با `UsageEvent` قدیمی در AccessDomain یکسان نیست. version `1.0` و `additionalProperties=false` هستند. تغییر meter، boundary، نوع یا معنا به نسخهٔ تازه و آزمون compatibility نیاز دارد. نرخ و contractVersion می‌توانند بدون تغییر schema عوض شوند، اما مصرف قبلی را بازنویسی نمی‌کنند.

## داده و مرجع هر فیلد

| فیلد | معنا و مرجع اعتبار |
| --- | --- |
| `schemaVersion/eventId` | نسخه و هویت delivery پایدار؛ producer هنگام commit یک بار می‌سازد و همان payload را بازمی‌فرستد |
| `producer.id/kind/deploymentId` | backend محصول ثبت‌شده؛ از authenticated workload و binding معتبر تأیید می‌شود؛ header ارسالی مشتری مرجع نیست |
| `environment` | محیط deployment مجاز؛ DB/accounts این برش در هر محیط جدا هستند |
| `ownerTenantId` | مالک Product از رجیستری مرکزی؛ جای consumer در حساب را نمی‌گیرد |
| `consumerTenantId` | tenant مصرف‌کننده و حساب بدهکار؛ از context معتبر backend، نه body عمومی مشتری |
| `productKey` | کلید ثابت محصول موجود؛ رجیستری دوم ایجاد نمی‌شود |
| `contractId/contractVersion` | نسخهٔ تجاری مؤثر هنگام پذیرش operation؛ به projection تاریخی معتبر reference دارد |
| `allocationId/allocationPeriodId` | تخصیص پایدار و دورهٔ مصرف؛ با تمدید contractVersion reset نمی‌شود |
| `operation.id/version` | شناسهٔ business operation و نسخهٔ معنای فرمان؛ همان `OrgCommand.RequestId` پایدار در این برش |
| `operation.actorId/requestFingerprint` | actor معتبر و digest درخواست canonical backend؛ تغییر actor یا payload در همان operation تعارض است |
| `operation.requestId` | attempt اولیهٔ ثبت‌شده؛ attemptهای بعدی در audit جدا هستند و رویداد موفق را بازنویسی نمی‌کنند |
| `operation.acceptedAt` | زمان قفل‌شدن rate هنگام پذیرش backend؛ مرجع انتخاب نرخ تاریخی |
| `operation.billableBoundary` | `organization-unit-committed/v1` |
| `authorization.*` | reference تصمیم/admission واقعی؛ schema مصرف Permit صادر نمی‌کند |
| `meter` | `organization.change/v1`، واحد `change` و quantity رشتهٔ دقیق `"1"` |
| `rateSnapshot` | rateId/version، بازه، unitPrice اعشاری رشته‌ای، ارز، scale، rounding و مدل postpaid؛ از مرجع قیمت معتبر سروری |
| `occurredAt` | زمان نتیجهٔ committed گزارش‌شده توسط backend؛ ترتیب رویدادها با آن حدس زده نمی‌شود |
| `producedAt` | زمان ثبت publish intent؛ `acceptedAt <= occurredAt <= producedAt` |
| `evidence` | business receipt، digest خروجی امن و reference قابل پیگیری؛ متن درخواست، token یا دادهٔ شخصی نیست |
| `correlationId/traceId` | اتصال مشاهده‌پذیری؛ هویت مالی یا مجوز نیستند |

زمان‌ها UTC با Z هستند. `receivedAt` عمداً در event producer نیست؛ intake آن را در receipt از ساعت خودش ثبت می‌کند. همراه آن eventDigest، logicalKey، ledgerKey، وضعیت و نتیجهٔ پایدار نگه داشته می‌شوند. تأخیر و clock skew با receivedAt ثبت می‌شوند؛ صرف تأخیر مصرف را حذف نمی‌کند. clock نامعتبر به quarantine/reconciliation می‌رود.

## نرخ و validation معنایی

JSON schema صحت تجارت یا provenance را ثابت نمی‌کند. intake باید authenticated producer را به product/environment/owner binding، consumer، نسخهٔ قرارداد، allocation و operation مجاز متصل کند. شناسهٔ Decision/Admission هم شاهد قابل بررسی است؛ صرف حضور رشتهٔ `decisionId` مجوز نیست. منبع حقیقی این referenceها نزد سیاست/Gateway/محصول است؛ fixture از resolver مصنوعی ثابت استفاده می‌کند.

در [fixture سازگاری مشترک](COMPATIBILITY.md)، `authorization.decisionId` دقیقاً `Decision.requestId` و `admissionId` برابر `admission:` به‌علاوهٔ همان nonce هستند؛ این نگاشت فقط با Decision تولیدشده و witness تأییدشدهٔ Gateway معتبر می‌شود. `policyVersion` یک reference منفرد `policyId@policyVersion` است؛ چند policy بدون نگاشت composition رد می‌شوند. `contractVersion` قبل از تبدیل، دقیق و در بازهٔ `1..2147483647` اعتبارسنجی می‌شود. configRevision مستقل از policy/contract است. دوره `2026-10` به بازهٔ UTC `[2026-10-01,2026-11-01)` bind می‌شود. نتیجهٔ admitted هنوز مرز committed billable نیست.

نرخ باید دقیقاً با نسخهٔ تاریخی معتبر و bindشده به همان product/consumer/contract/meter همسان باشد؛ `effectiveFrom <= operation.acceptedAt < effectiveTo`. زمان دریافت یا tariff جاری جای این انتخاب را نمی‌گیرد. قیمت قفل‌شده برای operation در جریان حفظ می‌شود؛ سیاست revoke/checkpoint همچنان نزد محصول است. قرارداد v8 یا tariff جدید، event v7 معتبر دیررس را reprice نمی‌کند.

`quantity` و `unitPrice` decimal رشته‌ای غیرمنفی با حداکثر شش رقم اعشار هستند. quantity این برش فقط `"1"` است. parsing با culture مستقل، multiplication checked و rounding `AwayFromZero` انجام می‌شود. ارز IRR scale=0 و USD/EUR scale=2 است؛ schema range را محدود می‌کند و intake رابطهٔ currency/scale را کنترل می‌کند. zero price مصرف رایگان معتبر ثبت می‌کند، با amount صفر؛ quantity صفر event billable نیست. مالیات، تخفیف، FX و سهم درآمد در این نسخه نیستند.

amount از quantity و snapshot محاسبه می‌شود و از caller پذیرفته نمی‌شود. schema تازهٔ پایلوت اجازهٔ unknown/failed/cancelled نمی‌دهد: این‌ها در operation status پایدار جدا ثبت می‌شوند و تا شاهد completed معتبر ندارند ledger debit نمی‌سازند. refund/credit و partial outcome به policy و schema مستقل نیاز دارند؛ event موفق سابق edit یا حذف نمی‌شود.

مصرف دیررس در دورهٔ باز با rate قدیمی و recordedAt جدید ثبت می‌شود. اگر دورهٔ مربوطه بسته/صورتحساب صادر شده باشد، مصرف در `accounting-pending` حفظ و به adjustment مصوب ارجاع می‌شود. مسیر runtime جدید invoice را برای مصرف نسخه‌دار با `LedgerEntry.UsageOccurredAt` گروه‌بندی می‌کند؛ legacy همچنان از `At` استفاده می‌کند. `At` زمان ثبت حسابداری می‌ماند و صورتحساب صادرشده خودکار اصلاح نمی‌شود؛ sample تاریخی پذیرش مالی دوره‌بسته را ادعا نمی‌کند.

## هویت، receipt و تکرار

سه هویت مستقل هستند:

1. attempt: `operation.requestId` و trace؛ هر بار network می‌تواند attempt تازه داشته باشد.
2. delivery receipt: producer + environment + consumer + eventId. همان event و همان fingerprint، نتیجهٔ قبلی را بدون debit/outbox تازه برمی‌گرداند. تغییر هر فیلد همان event تعارض 409 است.
3. logical usage: consumer + environment + product + operation.version + operation.id + meter/boundary. actor، fingerprint درخواست، contract/rate snapshot و evidence در fingerprint این هویت bind می‌شوند؛ actor عوض‌شده operation دوم ایجاد نمی‌کند. contractVersion و rateVersion بخش کلید یکتا نیستند.

logical key پایدار ترجیحاً با SHA-256ِ tuple ساختاری و prefix کوتاه `usage:v1:` در LedgerEntry.Key نگه داشته می‌شود؛ الحاق متن با delimiter بدون encoding کافی نیست. schemaVersion minor نیز نباید راه ایجاد debit دوم برای همان operation باشد. schema migration باید map همین هویت را حفظ کند.

event جدید برای همان operation با business payload یکسان، alias receipt می‌سازد و به ledger قبلی اشاره می‌کند؛ charge/outboxِ مالی تازه نمی‌سازد. اگر business payload متفاوت باشد، conflict/quarantine است. برای operation fingerprint فقط metadata انتقال `eventId/producedAt/correlationId/traceId/operation.requestId` حذف می‌شوند؛ quantity، outcome، owner/consumer، actor، acceptedAt، evidence و snapshot حفظ می‌شوند.

sample کلیدهای object را با ترتیب ordinal canonical می‌کند، arrayها را به ترتیب نگه می‌دارد و SHA-256 می‌گیرد. decimal رشته‌ها عیناً حفظ می‌شوند؛ `"12"` و `"12.0"` در receipt برابر فرض نمی‌شوند. duplicate property names باید قبل از hashing رد شوند. transport hash/receipt برای همان wire event ثابت است؛ consumer eventId یا snapshot producer را بازسازی نمی‌کند. ادعای انطباق با RFC canonicalization عمومی مطرح نیست؛ الگوریتم sample و golden fixture قرارداد محدود هستند.

receipt مالی TTL ترافیکی ندارد. پاک‌سازی فقط با retention مصوب و حفظ tombstone/هویت مالی و backup قابل replay مجاز است. پایان lease، reset quota یا تعویض نسخهٔ policy/contract، receipt مالی را باطل نمی‌کند.

## timeout و unknown

اگر ارتباط پس از ارسال فرمان قطع شد، backend ممکن است commit کرده باشد. Gateway/client باید status همان `operation.id` را بخواند. وقتی status unavailable است، operation `unknown` می‌ماند؛ ساخت کلید تازه، retry با payload تازه یا اجرای دوباره به دلیل پایان timeout/lease ممنوع است. «not found» در replica lagged یا بعد از restore، اثبات عدم اجرا نیست.

reconciliation سه نتیجه دارد: completed با business receipt معتبر → بازنشر همان event؛ definitely-not-executed از مرجع معتبر → retry همان business key فقط در مسیر ایمن محصول و با authorization تازه؛ evidence ناکافی → needs-intervention با مسئول و audit. replay مصرف به Accounting با replay فرمان side-effect دار backend متفاوت است.

برای API انتخاب‌شده، RequestId باید اجباری شود؛ مدل فعلی آن را optional می‌داند. replay پاسخ دادهٔ کسب‌وکار پس از revoke همچنان authorization تازه می‌خواهد؛ receipt مالی داخلی، مجوز مشاهدهٔ response برای caller ایجاد نمی‌کند.
