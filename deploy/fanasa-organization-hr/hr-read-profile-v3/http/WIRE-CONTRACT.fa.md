# قرارداد محلی receiver خواندن HR3

این نسخه برای همین نامزد تعریف شده است؛ sender جاری Gateway آن را پیاده نکرده و پذیرش عملیاتی ندارد. پیام امضاشده به‌تنهایی مجوز نیست. مرجع رفتار parser، `HrReadEnvelopeVerifier` و آزمون‌های منفی آن است.

سه header لازم‌اند: `Authorization` با Bearer معتبر، `X-Fanasa-Context` شامل Base64 استاندارد و canonical از JSON UTF8 و `X-Fanasa-Context-Signature` شامل HMAC-SHA256 به شکل۶۴رقم hex کوچک. هر header دقیقاً یک مقدار دارد. `Idempotency-Key` اختیاری است؛ در صورت حضور خالی نیست و حداکثر۱۲۸کاراکتر از `[A-Za-z0-9._:-]` دارد. غیبت آن در قاب امضا برابر صفرbyte است. header گواهیِ forwardشده جای گواهی native TLS را نمی‌گیرد.

JSON ریشه دقیقاً این فیلدها را دارد؛ هیچ فیلد اضافی یا تکراری در هیچ عمقی پذیرفته نمی‌شود:

| فیلد | مقدار یا قاعده |
|---|---|
| envelopeVersion | `fanasa.hr-read.context/1` |
| schemaVersion / profileId | `3.0.0` / `fanasa.organization-hr-read/1` |
| keyId / tier | عین binding معتبر میزبان؛ tier برابر `edge` یا `internal` |
| issuedAt / expiresAt | عدد ثانیهٔ Unix، حداکثر۷رقم اعشار، بدون exponent یا صفر انتهایی اضافی |
| tokenSha256 | SHA256 کوچک از bytes دقیق خود token، بدون پیشوند Bearer |
| context | شیء با تمام فیلدهای جدول بعد |

| فیلدهای context | قاعده |
|---|---|
| requestId / decisionId / admissionId | requestId برابر `g-` و۳۲رقم hex کوچک؛ decisionId همان requestId؛ admissionId برابر `admission:` و requestId |
| method / rawTarget | `GET` و مسیر دقیق backend؛ query، slash اضافه، encoding جایگزین و UUID بزرگ رد می‌شوند |
| subjectId / clientId / consumerTenantId | هویت و مصرف‌کنندهٔ scalar دقیقاً مطابق binding؛ tenant مسیر همان مصرف‌کننده است |
| ownerTenantId / ownership | Platform: مقدار null و `{kind:"Platform",owner:"platform",registeredProductId:"UUID"}`؛ Tenant: UUID مالک در هر دو محل یکسان |
| productId / audience / issuer | key محصول `organization-fabric`، audience برابر `fanasa-organization-fabric` و issuer معتبر binding |
| scope / environment / zone | scope فقط `organization.hr.read`؛ محیط و zone عین binding |
| action / operationGroup | action متناظر chart یا history؛ گروه `organization.hr.read` |
| contractId / contractVersion | عین binding قرارداد؛ نسخه عدد صحیح مثبت canonical |
| policyId / policyVersion | عین binding سیاست؛ نسخه عدد صحیح مثبت canonical |
| allocationId / allocationScope / configRevision | عین binding تخصیص و canonicalScope؛ revision صحیح مثبت مستقل |
| allocationPeriodId / periodStartMs / periodEndMs | دورهٔ ماهانهٔ `yyyy-MM` و ابتدا/انتهای همان ماه UTC به میلی‌ثانیهٔ صحیح |
| decisionValidUntil / tokenExpiresAt | deadline تصمیم به ثانیهٔ اعشاری و انقضای token به ثانیهٔ صحیح؛ exp احراز JWT نیز باید دقیقاً همین باشد |
| allowed / confirmed | فقط true؛ این دو مقدار امضاشده جای تأیید جاری مرجع را نمی‌گیرند |

عمر پیام و فاصلهٔ deadline تصمیم تا issuedAt حداکثر۵ثانیه است. انقضای پیام از deadline تصمیم، token یا پایان دوره فراتر نمی‌رود و زمان فعلی باید در تمام پنجره‌ها باشد. تنظیم میزبان می‌تواند حد کوچک‌تری بدهد. مهلت admission نیز از پایان عمر گواهی native فراتر نمی‌رود. شناسه‌های نسخهٔ سیاست/قرارداد/revision حداکثر `9007199254740991` هستند؛ revision مسیر history یک Int64 مثبت مستقل است.

قاب امضا همان هفت جزء قبلی است: domain `fanasa.context.v1`، method، RawTarget، hash متن دقیق Authorization، hash بدنهٔ صفرbyte، hash Idempotency-Key و متن دقیق Base64. هر جزء با طول UTF8 و colon آغاز می‌شود. hashها hex کوچک‌اند؛ JSON قبل از امضا canonicalize نمی‌شود. امضای نسخهٔ فرمان قبلی برای این پیام کافی نیست: discriminator، profile و تمامی bindingهای بالا باید مستقل پاس شوند.

گیرنده فقط با `MapHrReadCandidate` در میزبان ثبت می‌شود. میزبان باید بین این routeها و GETهای قبلی همان مسیر انتخاب کند؛ ثبت هم‌زمانشان مسیر را مبهم می‌کند. میزبان باید scheme `OrganizationHrBearer` را از JWT واقعی با اعتبارسنجی issuer/audience/signature/lifetime ثبت کرده باشد؛ claim mapping خام و scalar این قرارداد را حفظ کند و certificate forwarding را روی listener مستقیم اجرا نکند. chain بومی با trust سیستم، clientAuth EKU، revocation آنلاین و pin همان DER بررسی می‌شود. این کتابخانه root نصب نمی‌کند. کلید از `IHrReadTrustedKeySource` و authority از `IHrReadHttpAuthoritySource` می‌آیند؛ هر دو پیش‌فرض unavailable هستند. authority باید نگاشت یکتای introspection، ثبت مرکزی جاری و contract/grant/allocation واقعی را بررسی کند. guard همگام Fabric باید این شواهد و درخواست اصلی را در هر snapshot و gate نهایی تازه دوباره کنترل کند. فایل تنظیمات و proof امضا هیچ‌کدام آن منابع را ایجاد نمی‌کنند.

`ExecuteAsync` و نگاشت صریح مسیرها، پاسخ Denied را403 با بدنهٔ خالی و `Cache-Control: no-store` می‌دهند. پاسخ مجاز فقط پس از nonce مستقل commitشده، materialization، serialization محدود و gate تازه به‌صورت bytes کامل ارسال می‌شود. خطای پس از شروع ارسال به403 تبدیل نمی‌شود.
