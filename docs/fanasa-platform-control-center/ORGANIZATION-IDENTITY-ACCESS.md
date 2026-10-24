# چارت سازمانی، هویت و مدیریت دسترسی فن‌آسا

نسخه طراحی ۱٫۰ — ۱۲ اکتبر ۲۰۲۶. مکمل بخش ۵٫۱ [معماری پلتفرم](ARCHITECTURE.md) است. وضعیت آزمون و فعال‌سازی API در [راهنمای HR](pilot/HR-API-INTEGRATION.md) ثبت می‌شود؛ نهایی‌شدن طراحی، شاهد استقرار نیست.

**چارت رابطه سازمانی شخص، هویت حساب ورود، و دسترسی اختیار انجام عملیاتی روی منبع مشخص را تعیین می‌کند.** ثبت عنوان مدیر، واحد یا سمت مجوز نرم‌افزاری ضمنی ایجاد نمی‌کند.

## ۱. مرجع داده و هویت

| داده | مرجع | مسئولیت |
| --- | --- | --- |
| حساب، sub، گذرواژه، MFA و credential سرویس | Keycloak مرکزی | احراز هویت؛ محصول مخزن گذرواژه موازی ندارد. |
| پرونده و رویداد پرسنلی | HR متصل | مرجع پرسنلی؛ مرکز ارتباط لازم با هویت و چارت را مصرف می‌کند. |
| tenant، عضویت، چارت و اختیار | Access Management و Fabric موجود | رابطه سازمانی و دسترسی همان tenant |
| محصول، audience و client metadata | رجیستری مرکزی | یک مرجع ثبت برای مرکز، Gateway و پنجره واحد |
| قرارداد و entitlement | قراردادها و projection مرکز | حدود قابلیت خریداری‌شده؛ خرید انتساب نقش فرد نیست. |
| مجوز رکورد و اثر عملیات | backend محصول | کنترل resource tenant، مالکیت و عملیات |

کلید هویت در طراحی **(issuer, sub)** است. OIDC این زوج را شناسه پایدار معرفی می‌کند؛ ایمیل، نام و کد پرسنلی کلید اعطای مجوز نیستند. [OIDC Core، بخش ۵٫۷](https://openid.net/specs/openid-connect-core-1_0.html#ClaimStability).

امروز TenantUser.KeycloakSubject با فرض یک issuer مرکزی پیکربندی‌شده ذخیره می‌شود. TenantUser عضویت است، نه پرونده جامع شخص. پشتیبانی چند issuer به کلید صریح issuer و migration نیاز دارد و هنوز موجود نیست. یک حساب می‌تواند چند عضویت مستقل داشته باشد؛ خروج از یک tenant حساب سراسری یا عضویت دیگر را حذف نمی‌کند. [مدل عضویت](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccessDomain.cs).

سمت خالی موجود است، ولی انتصاب فعلی عضو فعال دارای subject می‌خواهد. ثبت شخص فاقد حساب و اتصال تأییدشده بعدی توسعه آینده است؛ HR API حساب یا عضویت را از نام/ایمیل نمی‌سازد. مدل فعلی discriminator معتبر human/service ندارد؛ منع انتصاب service subject پیکربندی‌شده انسانی‌بودن همه حساب‌های دیگر را ثابت نمی‌کند.

## ۲. اجزای چارت

| مدل موجود | معنا و قواعد |
| --- | --- |
| OrgUnit | واحد با شناسه، کد، والد، نوع و وضعیت؛ والد معتبر، کد یکتا و ساختار بدون چرخه لازم است. |
| OrgRole | عنوان مسئولیت سازمانی؛ فهرست permission محصول نیست. |
| OrgPosition | سمت در یک واحد با ظرفیت و RoleId اختیاری نقش سازمانی |
| OrgAppointment | اتصال زمان‌دار subject به سمت؛ اعتبار From <= now < To و پایان اختیاری |
| OrgState / OrgChange | revision چارت، actor، reason، زمان و عملیات تغییر |

انتصاب عضو فعال همان tenant و سمت فعال می‌خواهد. ظرفیت بر تمام بازه‌های هم‌پوشان سنجیده می‌شود. هم‌پوشانی همان فرد در همان سمت ممنوع است؛ چند سمت متفاوت منع عمومی ندارد. واحد دارای زیرواحد/سمت فعال و سمت دارای انتصاب جاری/آینده ابتدا باید از وابستگی فعال خالی شوند. مدل جاری درخت واحدهاست؛ گزارش‌دهی ماتریسی توسعه مستقل است. [مدل و قواعد چارت](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs).

## ۳. نقش محصول، grant و binding

| مفهوم | وضعیت و مرز |
| --- | --- |
| OrgRole | موجود؛ مسئولیت سازمانی سمت |
| AccessRole | تعریف موجود در tenant و محصول با مجموعه permission؛ تعریف نقش انتساب فرد نیست. |
| TenantGrant | مجوز مستقیم موجود برای subject/tenant/permission با انقضا |
| binding سازمان به محصول | طراحی مقصد؛ مدل مستقل و reconciliation کامل هنوز پیاده نشده‌اند. |

OrgPosition.RoleId به OrgRole اشاره می‌کند و AccessRole یا grant ایجاد نمی‌کند. فرمان role.save نیز فقط نقش سازمانی است. [AccessRole](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccessDomain.cs)، [grant مستقیم](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PersistentAccessManagement.cs).

binding مقصد در ماژول موجود دسترسی نگهداری می‌شود و حداقل شناسه/نسخه، tenant، مبنای OrgRole یا سمت، محصول و نسخه AccessRole، دامنه منبع، action، محیط، شروع/پایان، منشأ، تأییدکننده و reason دارد. نتیجه ارزیابی باید انتصاب مؤثر، binding و نسخه‌های نقش/قرارداد/سیاست را مشخص کند. دامنه self، واحد یا زیرشاخه باید صریح تصویب شود.

grant مستقیم نیز به provenance و scope نیاز دارد. امروز actor/reason تغییر در outbox موجود است، ولی خود TenantGrant مرجع انتصاب و دامنه رکورد ندارد. بنابراین حذف انتخابی اختیار وابسته به یک سمت هنوز از این مدل نتیجه نمی‌شود. افزودن این اطلاعات به مدل موجود پیش‌نیاز انتقال امن است.

## ۴. سلسله‌مراتب و چند سمت

بالابودن در چارت دسترسی خودکار به زیرواحدها نمی‌دهد. درخت سازمان و ارث‌بری نقش محصول روابط جدا هستند. OASIS RBAC انتساب/فعال‌سازی و ارث‌بری نقش را تفکیک می‌کند؛ نگاشت چارت به اختیار تصمیم طراحی فن‌آسا است. [OASIS RBAC Profile](https://docs.oasis-open.org/xacml/3.0/rbac/v1.0/xacml-3.0-rbac-v1.0.html).

مجوز زیرشاخه به ریشه، محصول، action و نسخه چارت وابسته است؛ جابه‌جایی واحد scope را دوباره resolve می‌کند. بین tenantها ارث‌بری وجود ندارد. در چندسمتی‌بودن، درخواست به context معتبر همان tenant/محصول/منبع متصل می‌شود؛ همه اختیارات تمام سمت‌ها بی‌قید جمع نمی‌شوند. Deny، قرارداد و تفکیک وظایف حفظ می‌شوند. انتخاب context فعال و تفکیک وظایف کامل هنوز پیاده نشده‌اند.

## ۵. زنجیره تصمیم

~~~mermaid
flowchart LR
  I["issuer + sub معتبر"] --> M["tenant و عضویت فعال"]
  M --> A["انتصاب مؤثر و OrgRole"]
  A --> B["binding مصوب و دامنه‌دار"]
  B --> R["AccessRole و permission محصول"]
  M --> D["grant مستقیم معتبر"]
  R --> C["scope منبع، قرارداد و شرایط"]
  D --> C
  C --> P["تصمیم سیاست و obligations"]
  P --> E["Gateway و backend؛ admission و اجرای مجاز"]
~~~

این نمودار مقصد است. اختیار مستقل از انتصاب می‌تواند از grant مصوب بیاید، ولی tenant، resource، قرارداد و Deny را دور نمی‌زند. attributes از منابع سروری resolve می‌شوند؛ role/tenant/TPS مشتری مرجع اختیار نیستند. فقط Permit با obligations انجام‌شده اجازه اجرا می‌دهد؛ limiter و رزرو بودجه از تصمیم مستقل‌اند. backend مالکیت رکورد را دوباره کنترل می‌کند.

امروز AccessPolicyStore پروفایل typed محدود نقش سازمانی/واحد و permissionهای دامنه است؛ binding عمومی به AccessRole ندارد. GovernanceApi شبیه‌سازی را با simulation=true و enforced=false برمی‌گرداند. اتصال زنده این زنجیره و انطباق کامل XACML ادعا نمی‌شود. [پروفایل محدود](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/AccessPolicies.cs)، [مرز فعال‌سازی](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/FabricServices.cs).

## ۶. چرخه عضویت و سمت

| رخداد | رفتار طراحی و وضعیت جاری |
| --- | --- |
| ورود | هویت تأییدشده، عضویت، seat و انتصاب؛ فقط binding مصوب اختیار می‌دهد. عضویت/انتصاب موجودند؛ اجرای عمومی lifecycle فعال نیست. |
| انتقال | پایان انتصاب قبلی و شروع مقصد؛ حذف اختیار وابسته و بررسی grant مستقل. Store transfer دارد، ولی API مرورگر و HR انتقال مستقیم را رد می‌کنند. |
| جانشینی | مبنا، تفویض‌کننده/گیرنده، scope، زمان و تأیید مستقل؛ بیشتر از اختیار معتبر تفویض‌کننده نیست. مدل اختصاصی delegation موجود نیست. |
| پایان انتصاب | فقط مشتقات همان انتصاب پایان یابند؛ سایر سمت‌ها حفظ شوند. فرمان end موجود؛ reconciliation binding آینده است. |
| خروج | توقف عضویت، grants و انتصاب‌های همان tenant؛ هویت سراسری حفظ شود. offboarding محلی Fabric موجود است. |
| بازگشت | تأیید تازه، seat/قرارداد معتبر؛ grant منقضی خودکار احیا نشود. |

IdentityLifecycle درخواست join/move/leave و بررسی مستقل دارد؛ GovernanceApi فقط درخواست، approve/reject/cancel را می‌پذیرد و worker ثبت نشده است. وجود ApplyDue شاهد اجرای زمان‌بندی نیست. محدودیت جاری: Apply(move) همه grantهای همان tenant را منقضی می‌کند؛ provenance حذف انتخابی ندارد. فعال‌سازی انتقال به رفع این محدودیت و پذیرش وابسته است. [چرخه موجود](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/IdentityLifecycle.cs).

SetMembership(false) در تراکنش Fabric عضویت را متوقف، grants همان tenant را منقضی، انتصاب جاری را پایان و انتصاب آینده را از snapshot جاری حذف می‌کند؛ history باقی می‌ماند. این، ابطال اثبات‌شده در همه محصولات نیست. [offboarding](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/PersistentAccessManagement.cs).

## ۷. نسخه، audit و ابطال

فرمان چارت expectedRevision دارد؛ اختلاف نسخه conflict است و caller پس از خواندن تازه reconcile می‌کند. در Fabric مشترک snapshot/history/receipt/outbox یک تراکنش دارند. requestId یکسان با actor/payload یکسان نتیجه تاریخی را برمی‌گرداند؛ payload متفاوت conflict است. revision مورد انتظار در fingerprint منطقی Store کنار گذاشته می‌شود تا retry نتیجه قبلی را بازیابی کند. [receipt](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/OrganizationFabric.cs).

retry و history مجوز جاری می‌خواهند. token معتبر عضویت یا grant لغوشده را زنده نمی‌کند. در مقصد نسخه‌ها و deadline معتبر به cache/تصمیم متصل‌اند؛ پایان اعتبار از ساعت سرور کنترل می‌شود و منتظر worker نمی‌ماند. event، invalidation، acknowledgement و تأخیر ابطال باید اندازه‌گیری شوند؛ outbox شاهد مصرف همه eventها نیست.

امروز FabricClaimsTransformation permissionهای دامنه را از ورودی پاک و از grant معتبر و عضویت فعال بازسازی می‌کند. این، freshness همه محصولات یا raceهای commit را ثابت نمی‌کند؛ HR API منابع معتبر را مستقل می‌خواند. [کنترل جاری](/E:/SJVS/Projects/Neo/products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/TenantAuthorization.cs).

## ۸. API منابع انسانی پشت Gateway

مسیر **HR → Gateway → API سازمان PCC → OrganizationStore/Fabric** از client credentials اختصاصی و رجیستری موجود استفاده می‌کند. API حساب، عضویت، grant ادمین یا نقش محصول ایجاد نمی‌کند.

| مسیر نسخه ۱ | اختیار لازم |
| --- | --- |
| GET /api/organization-hr/v1/tenants/{tenantId}/chart | scope خواندن و grant organization.read همان tenant |
| GET /api/organization-hr/v1/tenants/{tenantId}/history/{revision} | مجوز خواندن جاری |
| POST /api/organization-hr/v1/tenants/{tenantId}/commands | scope نوشتن و grant organization.write همان tenant |

Allowlist: role.save، unit.save، unit.archive، position.save، position.archive، appointment.add و appointment.end. انتقال مستقیم و bulk replace ممنوع‌اند. write، UUID ثابت و برابر در Idempotency-Key و requestId، revision و payload محدود می‌خواهد.

Gateway فقط مسیر/method لازم را منتشر، توکن/scope را کنترل و headerهای هویت/tenant/گواهی مشتری را حذف می‌کند. backend Bearer اصلی و **گواهی واقعی peer TLS** را می‌سنجد؛ header گواهی جای mTLS نیست. client، subject خدماتی، tenant، scope، عضویت و grant تازه بررسی می‌شوند؛ دسترسی مستقیم بدون گواهی Gateway باید رد شود. metered unit.save بدون admission معتبر از این مسیر اجرا نمی‌شود.

schema، تنظیمات و وضعیت واقعی در [راهنما](pilot/HR-API-INTEGRATION.md) و [OpenAPI](pilot/hr-api.openapi.yaml) است. پذیرش به build، HTTPS/JWT/mTLS، deny/replay/conflict/revocation/persistence و route واقعی هم‌قرارداد نیاز دارد؛ داده مثال deployment نیست.

## ۹. پذیرش UI و توسعه

UI باید tenant، سمت خالی، ظرفیت، انتصاب جاری/آینده و عضویت را جدا نمایش دهد؛ کنار دسترسی محصول، permission، scope، منشأ، زمان و قرارداد دیده شوند. نقش سازمانی و نقش محصول جدا باشند. conflict، چرخه، ظرفیت و عضو نامعتبر خطای روشن داشته باشند. چند سمت context مشخص داشته باشد؛ شبیه‌سازی و mock با اجرای واقعی اشتباه نشوند. درخواست/تأیید lifecycle، RTL، موبایل و صفحه‌کلید پذیرفته شوند. صفحات /Organization، /Tenancy و /Governance مبنا هستند.

ترتیب رشد: **API محدود و امن HR → provenance/scope و binding در مدل موجود → انتقال/جانشینی و reconciliation → اعمال زنده پروفایل منتخب و ابطال → گسترش چند issuer یا چارت ماتریسی فقط با نیاز روشن**. مرحله اول منتظر موتور عمومی سیاست نمی‌ماند. مالک و معیار release در [پلن رشد](GROWTH-AND-DEVELOPMENT-PLAN.md) ثبت می‌شود؛ افزودن runtime/store به نیاز اندازه‌گیری‌شده وابسته است.

