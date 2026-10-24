# یادداشت استقرار سازمان — ۱۷ اکتبر ۲۰۲۶

وضعیت نهایی در `2026-10-17T15:41:42.699399Z`: **accepted**. CI، انتشار میزبان انسانی و نسخهٔ جدید Kubernetes تأیید شده‌اند؛ هر ۳۳ بررسی پذیرش Gateway موفق و هر دو timer به حالت `active/waiting/enabled` بازگردانده شده‌اند. این یادداشت خارج از inventory سورس CI قرار دارد.

| مورد | شاهد تأییدشده |
| --- | --- |
| مبدأ | پروژهٔ داخلی GitLab شمارهٔ ۲، شاخهٔ محافظت‌شدهٔ `develop` |
| CI | [pipeline 20](https://gitlab.fanasa.net.local/javadsayedi/organization-hr-api/-/pipelines/20)، همهٔ jobها موفق؛ job بسته‌بندی `37` |
| commit | `958e9abd658beda09d77a03d74aa3fc2574efb52` |
| inventory | ۵۴۶ فایل؛ SHA-256 برابر `55f9cb82f454039287fcca7005ce77fe2fdb45f81c4c515622f6d00a23a103c3` |
| release | `hr-development-958e9abd658b-55f9cb82f454` |
| verifier مورد اعتماد | ارتقای محدود verifier نصب‌شده تأیید شده است؛ رسید ارتقا به‌تنهایی فعال‌سازی runtime نیست. |
| میزبان انسانی | Web و Workbench با release جدید در systemd فعال‌اند. |
| نمونهٔ توسعه | الگوی `56b820c8-3a7b-40d3-944a-b09d4c7b803e`، با ۹ policy پیش‌نویس، ۳ تقویم و ۲۴ سناریوی مرجع ذخیره‌شده و معتبر، حفظ شده است؛ بررسی قبلی UI نیز ۲۴ از ۲۴ موفق بود. این دادهٔ آموزشی شاهد enforcement واقعی نیست. |
| HR در K8s | commit و release جدید با رسید واقعی Gateway پذیرفته شده‌اند؛ Application و runtime در وضعیت `Healthy/Synced` تأیید شده‌اند و فرمان‌های Gateway باز هستند. |
| timerها | هر دو timer در `2026-10-17T15:40:34.441543Z` به حالت `active/waiting/enabled` بازگشته و در بررسی نهایی دوباره تأیید شده‌اند. |

## اصلاح محیط کلاینت Docker

نخستین اجرای GitOps پس از ایجاد context معتبر OCI و پیش از صدور `oci.json` متوقف شد. خطای اصلی در تشخیص محافظت‌شده، `ERROR mkdir /root/.docker: read-only file system` بود. اجرای کامل `build-smoke` با همان حفاظت‌های systemd و فقط انتقال state کلاینت Docker/buildx به RuntimeDirectory خصوصی موفق شد؛ هر دو image و state مصنوعی تازه سالم بودند. این smoke، پذیرش عملیاتی K8s را اثبات نمی‌کند.

تنها فایل افزوده‌شده به تنظیم واحد:

```text
/etc/systemd/system/fanasa-organization-hr-gitops-reconcile.service.d/20-docker-client-state.conf
```

محتوای دقیق، با newline پایانی:

```ini
[Service]
Environment=DOCKER_CONFIG=/run/fanasa-organization-hr-gitops/docker-client
Environment=BUILDX_CONFIG=/run/fanasa-organization-hr-gitops/docker-buildx
```

SHA-256 فایل drop-in:

```text
66cc891c636b800d1077b83b87f1c44f01b3612e6711bbe020b00907cb117023
```

فایل اصلی واحد با SHA-256 زیر تغییر نکرده است:

```text
ff2227065b916624460aa46dc9c7604bf6707d111cfb6efe0b45c9d0279e73dc
```

پس از `daemon-reload`، حفاظت‌ها و فرمان اجرا به‌صورت معنایی بررسی شدند: `ProtectHome=yes`، `ProtectSystem=strict`، `PrivateTmp=yes`، `NoNewPrivileges=yes`، کاربر/گروه `root` و RuntimeDirectory برابر `fanasa-organization-hr-gitops` با mode `0700`. `ExecStart` همچنان کنترل‌کنندهٔ نصب‌شدهٔ مورد اعتماد است:

```text
/usr/bin/python3 /usr/local/lib/fanasa-organization-hr/gitops/deploy/fanasa-organization-hr/gitops-reconcile.py
```

فهرست `ReadWritePaths` در این مرحله همان چهار مسیر باقی ماند؛ مسیر یک PV موجود در اصلاح SQLite زیر افزوده شد:

```text
/var/lib/fanasa-organization-hr
/etc/fanasa-organization-hr
/opt/fanasa-gateway/certs
/run/fanasa-organization-hr-gitops
```

credential کپی نشده و دسترسی نوشتن به home یا storage کل Docker اضافه نشده است. مقایسهٔ خام `systemctl show ExecStart` می‌تواند پس از `daemon-reload` به‌دلیل حذف PID و timestamp تاریخی فرق کند؛ بررسی حفظ فرمان و حفاظت‌ها از آن metadata موقت مستقل است.

## اصلاح CAS کنترل‌کننده

اجرای بعدی از ساخت OCI عبور کرد، اما انتشار Application به‌دلیل قدیمی‌شدن `resourceVersion` متوقف شد. در دو server dry-run محافظت‌شده با UID، spec و annotations یکسان، نسخهٔ ذخیره‌شده رد و نسخهٔ تازه پذیرفته شد؛ این مشاهده با رقابت به‌روزرسانی status توسط Argo بین خواندن Application و patch سازگار است.

کنترل‌کنندهٔ v7 پس از ایجاد Secret و بررسی دوبارهٔ CI و trust، Application را بلافاصله پیش از patch می‌خواند. UID، کل spec و annotations به مقدار اصلی مقید می‌مانند و آزمون `resourceVersion` نیز در JSON patch حفظ شده است. فقط رد قطعی API همراه با RV تازه و بدون تغییر این سه مقدار، حداکثر سه تلاش را مجاز می‌کند؛ RV ثابت یا تغییر UID/spec/annotations انتشار را متوقف می‌کند. هر تلاش انتشار، CI و trust را دوباره بررسی می‌کند؛ rollback نیز همان CAS را با بررسی trust و annotations نسخهٔ کاندیدا به کار می‌برد.

رد قطعی API در مسیر موجود، اجرای بدون تغییر محسوب می‌شود و می‌تواند به `rollback-complete` برسد. timeout، خروجی ناقص یا پاسخ نامنطبق patch نتیجهٔ نامطمئن دارند: `CasOutcomeUnknown` مانع فرض اشتباه `changed=False` می‌شود و فرمان‌ها برای بررسی frozen می‌مانند؛ چنین خطایی کورکورانه تکرار نمی‌شود. پاسخ موفق نیز باید UID/spec/annotations دقیق کاندیدا را تأیید کند.

سورس بررسی‌شده در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v7/deploy/fanasa-organization-hr/gitops-reconcile.py` و تست‌های آن در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v7/deploy/fanasa-organization-hr/tests/test_gitops_reconcile.py` قرار دارند؛ v6 تغییر نکرده است. هر ۶۱ تست محلی موفق و AST هر دو فایل معتبر بود. شواهد و log در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v7/controller-cas-review.json` و `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v7/controller-cas-tests.log` ثبت شده‌اند؛ این تست‌ها پذیرش زندهٔ K8s را اثبات نمی‌کنند. اسکریپت ارتقای محدود اعتماد در `E:/SJVS/Projects/Neo/.build/hr-controller-trust-update-20261017.py` است.

نصب اصلاح موفق بوده و رسید آن در مسیر زیر ثبت شده است:

```text
/var/lib/fanasa-organization-hr/controller-trust-update-20261017/05d17ef5a0cc3447/updated.json
```

فقط سه فایل کنترل‌کننده، manifest اعتماد و config تغییر کرده‌اند؛ sentinelهای سایر فایل‌ها ثابت مانده‌اند:

| فایل نصب‌شده | SHA-256 پس از اصلاح CAS؛ پیش از اصلاح رجیستری زیر |
| --- | --- |
| `/usr/local/lib/fanasa-organization-hr/gitops/deploy/fanasa-organization-hr/gitops-reconcile.py` | `05d17ef5a0cc344714358558b76afdb7f51f9ab4a775076085f0dc611715ca10` |
| `/usr/local/lib/fanasa-organization-hr/gitops/trusted-files.json` | `7f60c3af031d9f07425771ef65f229efb4bd0098570970ffb408e15298390eff` |
| `/etc/fanasa-organization-hr/gitops-reconcile.json` | `02bd58a2c1f49390c685263b62f1a8a2fcbe42dee2409ee8c997108c86bcadb4` |

اجرای عادی GitOps پس از نصب آغاز شد. نتیجهٔ نهایی پس از اصلاحات بعدی در بخش «پذیرش نهایی» ثبت شده است.

## اصلاح انتشار تکرارپذیر رجیستری

تلاش بعدی پیش از CAS متوقف شد و Application زنده تغییر نکرد. context هر دو ساخت برابر `54b05e31ec4681e4a1a62433f3d4ab95c277210f57a68d23d5e4a7b6aa7b8920` بود، اما بازتولید attestation توسط Docker/buildx، digest مربوط به index محلی را تغییر داد؛ tag تغییرناپذیر رجیستری index قبلی را داشت. child اجرایی هر دو نقش همان manifest قبلی بود. برابری context یا RootFS به‌تنهایی برای پذیرش image کافی محسوب نشد.

اصلاح v8 فقط برای index محلی پذیرفته‌شده و متفاوت، `docker image inspect --platform linux/amd64 <acceptedLocalId>` را روی digest تغییرناپذیر اجرا می‌کند. Id و Descriptor شامل digest، mediaType، size و platform باید دقیقاً با child سرو‌شده برابر باشند؛ Config کامل و RootFS دو مشاهدهٔ محلی نیز یکسان و tag محلی در پایان دوباره بررسی می‌شود. کنترل‌های قبلی bytes/digest/config/attestation رجیستری حفظ شده‌اند. رسید اصلی OCI و tag رجیستری بازنویسی نمی‌شوند؛ proof جدید، index محلی و child مشترک را جدا ثبت می‌کند.

هر ۶۹ تست مجموعهٔ محلی موفق شد. سپس اجرای واقعی و فقط‌خواندنی `verify_graph` روی میزبان، پیش از نصب برای هر دو نقش Web و Workbench موفق بود؛ شاهد آن `/var/tmp/fanasa-hr-publication-20261017/registry-candidate-live-check.json` است. سورس در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v8/deploy/fanasa-organization-hr/registry_runtime.py`، شواهد در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v8/registry-review.json` و log در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v8/registry-tests.log` قرار دارند. اسکریپت نصب محدود، `E:/SJVS/Projects/Neo/.build/hr-registry-trust-update-20261017.py` است.

نصب موفق در `/var/lib/fanasa-organization-hr/registry-trust-update-20261017/786924caee270deb/updated.json` ثبت شد. فقط registry، manifest اعتماد و config تغییر کردند؛ controller با هش `05d17ef5a0cc344714358558b76afdb7f51f9ab4a775076085f0dc611715ca10` ثابت ماند و journalها، PVC و Gateway حفظ شدند:

| فایل نصب‌شده | SHA-256 پس از اصلاح رجیستری؛ پیش از اصلاح readiness زیر |
| --- | --- |
| `/usr/local/lib/fanasa-organization-hr/gitops/deploy/fanasa-organization-hr/registry_runtime.py` | `786924caee270deb27920f520a19e25f413de2b0373a964a77300bc86bd9570f` |
| `/usr/local/lib/fanasa-organization-hr/gitops/trusted-files.json` | `98ca1116272f5469508aed2a3c2c2368bb5a6a59a820bf39cb9744942361ed5a` |
| `/etc/fanasa-organization-hr/gitops-reconcile.json` | `48260bb334ec2a5b281dcf8dc1038e178a096d86ad203ca10f5c439fa5b35485` |

اجرای عادی GitOps دوباره آغاز شد. بررسی graph شاهد هویت image بود؛ پذیرش مستقل runtime در بخش «پذیرش نهایی» ثبت شده است.

## اصلاح انتظار آمادگی واقعی runtime

تلاش بعدی از CAS عبور کرد، اما guard مرحلهٔ network پیش از اجرای probe متوقف شد: رسید شکست در `14:48:47` ثبت شده، intent مربوط به `network-probe-intent` ایجاد نشده و رویدادهای Kubernetes ایجاد Pod کاندیدا را در `14:48:50` و شروع آن را در `14:48:55` نشان می‌دهند. کنترل‌کننده به‌علت ثابت‌بودن chart revision، status قبلی `Healthy/Synced/Succeeded` در Argo را زودتر از rollout واقعی پذیرفته بود. عملیات rollback نیز در `14:48:58` موفق گزارش شد، درحالی‌که Pod بازگشت در `14:49:05` شروع شد؛ تطبیق نتیجهٔ عملیات Argo به‌تنهایی مرز آمادگی runtime نیست. نسخهٔ قبلی پس از rollback سالم و فرمان‌ها باز بودند.

در v9، `wait_healthy` علاوه بر UID و کل spec، تطبیق دقیق `status.sync.comparedTo.source/destination` و `operationState.syncResult.source/revision` با Application را لازم می‌داند. هر دو Deployment ثابت باید `observedGeneration >= generation` و دقیقاً یک replica جاری، updated، ready و available داشته باشند. template و Pod یکتای غیرحذف‌شوندهٔ HR و relay باید تصاویر، provenance و imageID دقیق نسخهٔ مورد انتظار را داشته باشند؛ Podها Running/Ready و containerهایشان running/ready باشند. init مربوط به retained state نیز باید با image دقیق Web موفق شده باشد. hash annotation مربوط به relay از فایل chart مورد اعتماد محاسبه می‌شود. UID/spec پس از مشاهدهٔ runtime دوباره کنترل می‌شوند. این مرز مشترک در شروع، ارتقا و rollback اعمال می‌شود؛ rollout عادی به انتظار محدود ادامه می‌دهد و تأخیر ثابت تازه‌ای افزوده نشده است.

هر ۷۹ تست محلی موفق شد و predicate واقعی آمادگی، پیش از نصب به‌صورت فقط‌خواندنی روی نسخهٔ قبلی پذیرفته‌شده نیز موفق بود. سورس در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v9/deploy/fanasa-organization-hr/gitops-reconcile.py`، شواهد در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v9/readiness-review.json` و log در `E:/SJVS/Projects/Neo/.build/hr-k8s-live-20261014/operator-review-v9/readiness-tests.log` قرار دارند. اسکریپت نصب محدود، `E:/SJVS/Projects/Neo/.build/hr-readiness-trust-update-20261017.py` است.

نصب موفق در `/var/lib/fanasa-organization-hr/readiness-trust-update-20261017/43cef1071f83fad9/updated.json` ثبت شد. registry با هش `786924caee270deb27920f520a19e25f413de2b0373a964a77300bc86bd9570f` حفظ شد؛ فقط controller، manifest اعتماد و config تغییر کردند:

| فایل نصب‌شده | SHA-256 پس از اصلاح readiness |
| --- | --- |
| `/usr/local/lib/fanasa-organization-hr/gitops/deploy/fanasa-organization-hr/gitops-reconcile.py` | `43cef1071f83fad9b019f834febec2b2291f7b66abc8c08836560e8067c67f7b` |
| `/usr/local/lib/fanasa-organization-hr/gitops/trusted-files.json` | `83df7f80886edf1dfb318738a112a0358dd029ad18edc4c273826311ca1f1408` |
| `/etc/fanasa-organization-hr/gitops-reconcile.json` | `07a24f45d231be1cb5e1195b03d169597484f18fa8ecb1ea241d83e9b9dd6952` |

اجرای عادی GitOps پس از این اصلاح دوباره آغاز شد. نتیجهٔ نهایی rollout و بازگرداندن timerها در بخش «پذیرش نهایی» ثبت شده است.

## تشخیص و اصلاح sandbox SQLite

تلاش پیشین پس از پذیرش درخواست مصنوعی، هنگام شواهد SQLite با کلاس خطای `OperationalError` متوقف شد. در آن مرحله سلامت دیتابیس زنده تأیید و درخواست مصنوعی با revision برابر `3` حفظ شد. rollback به نسخهٔ قبلی پذیرفته‌شدهٔ K8s موفق بود و timerها تا پذیرش نهایی در hold ماندند.

بازتولید قطعی روی fixture مصنوعی WAL بدون sidecar، در volume فقط‌خواندنی و با همان حفاظت‌های سخت‌گیرانه، خطای `SQLITE_CANTOPEN` با کد `14` داد. با تغییر صرفاً همان دایرکتوری fixture به writable، همان خواندن‌های readonly، integrity و dump موفق شدند و فایل fixture تغییر نکرد. این شاهد، فرضیهٔ محدودیت mount برای هماهنگی SQLite/WAL را تقویت می‌کند؛ رسید شکست تاریخی فقط کلاس exception را دارد و کد SQLite آن شکست تأیید نشده است.

رسید بازتولید `/var/tmp/fanasa-hr-publication-20261017/sqlite-sandbox-reproduction.json` با SHA-256 برابر `22bb51efe900c856fd7d4ceb0488e5eaf73b70140e45004eb80118ce9c5c4484` و سورس آن `E:/SJVS/Projects/Neo/.build/hr-r4-sqlite-sandbox-reproduce-20261017.sh` است.

drop-in در `/etc/systemd/system/fanasa-organization-hr-gitops-reconcile.service.d/30-retained-sqlite-state.conf` نصب و بررسی شد؛ SHA-256 آن `0132c9de8db3537e7fb0ba219a78dd81c3a48b0baa61d02f29346714b0a6a97f` است. محتوای دقیق، با newline پایانی:

```ini
[Service]
ReadWritePaths=/var/lib/rancher/k3s/storage/pvc-12698725-d049-4989-bb91-b2e90c4712f4_fanasa-organization-hr-development_organization-hr-retained-state
```

این تنظیم، مجوز نوشتن در **کل همین PV موجود** را به مسیرهای قبلی اضافه می‌کند؛ دامنهٔ مجوز صرفاً بایت‌های WAL/SHM نیست. نصب هیچ دیتابیس یا trust اپراتور را تغییر نداد. سایر حفاظت‌های systemd، هش‌های کنترل و هر چهار تاریخچهٔ ارتقای trust ثابت ماندند.

رسید نصب `/var/lib/fanasa-organization-hr/sqlite-sandbox-update-20261017/22bb51efe900c856/updated.json` وضعیت همان لحظه، `installed-inactive`، را ثبت کرده است. helper نهایی `E:/SJVS/Projects/Neo/.build/hr-sqlite-sandbox-update-20261017.py` با SHA-256 برابر `940717f7201b6b837c6c58c26b447a6cd45e7a361c59f57e819b60ac4d40b337` بازبینی مستقل شد و تست متمرکز normalization موفق بود. اجرای عادی بعدی از این مرحله عبور کرد و پذیرش نهایی زیر را ثبت کرد.

## پذیرش نهایی

رسید واقعی Gateway در `2026-10-17T15:40:08.583054Z` با وضعیت `accepted` برای commit، inventory و release بالا ثبت شد:

```text
/var/lib/fanasa-organization-hr/operators/hr-development-958e9abd658b-55f9cb82f454-gitops-7664631d1583/gateway-acceptance-receipt.json
```

هر ۳۳ بررسی موفق بود؛ از جمله رد دسترسی ناشناس، token و peer نامعتبر، scope/tenant نامجاز، کنترل idempotency و محدودیت نرخ. Pod کاندیدا واقعاً بازسازی شد؛ revision فرمان مصنوعی از `3` به `4` رسید و receipt/history/outbox پس از بازسازی حفظ شدند. رد token واقعاً امضاشده و منقضی، سپس پذیرش token تازه، آزموده شد. `grantExpires` بدون تمدید برابر `2026-10-19T14:35:12.5338158Z` باقی مانده است.

بررسی مستقل زنده در `2026-10-17T15:41:26.101201Z`، hash فایل‌های `System.Security.Cryptography.Pkcs.dll` را از root فرایندهای systemd و Kubernetes با آرشیو تأییدشده یکسان یافت. در این بررسی mapping این DLLها مشاهده نشد؛ این شاهد تطبیق فایل‌هاست و ادعای بارگذاری assembly ندارد. integrity هر دو دیتابیس fabric و Workbench برابر `ok` بود. الگوی آموزشی با ۹ policy پیش‌نویس، صفر policy منتشرشده، ۳ تقویم و ۲۴ سناریوی مرجع ذخیره‌شده معتبر و فعال باقی ماند. این اسکریپت اجرای سناریوها را مشاهده نکرد (`scenarioExecutionObserved=false`)؛ شاهد مستقل قبلی UI برای ۲۴ از ۲۴ بررسی جداست و هیچ‌یک به معنی enforcement واقعی policyهای آموزشی نیست.

health میزبان Web و Workbench پاسخ `200` داشت؛ مسیرهای محافظت‌شده پاسخ مورد انتظار `302/401` دادند. به‌علت در دسترس نبودن DNS میزبان خصوصی در محیط بررسی، probeهای TLS روی VPS از loopback به Gateway موجود رفتند و اعتبارسنجی نام میزبان و گواهی حفظ شد. این روش بررسی، اصلاح سراسری DNS نیست.

رسید بازگردانی timerها در `2026-10-17T15:40:34.441543Z` وضعیت هر دو `fanasa-organization-hr-gitlab-cd.timer` و `fanasa-organization-hr-gitops-reconcile.timer` را `active/waiting/enabled` ثبت کرد. بررسی نهایی در `2026-10-17T15:41:42.699399Z` مجدداً همین وضعیت، فعال و enabled بودن هر دو سرویس انسانی، `Healthy/Synced` بودن Application و آمادگی واقعی runtime، و باز بودن فرمان‌های Gateway را تأیید کرد. manifest مورد اعتماد نهایی همچنان `83df7f80886edf1dfb318738a112a0358dd029ad18edc4c273826311ca1f1408` است.

Application موجود با UID برابر `9220d045-abfe-4f35-b0de-a8990e9b0cc8` و همان PVC با نام `organization-hr-retained-state` حفظ شدند. UID مربوط به PVC برابر `12698725-d049-4989-bb91-b2e90c4712f4`، UID مربوط به PV برابر `aa04ceab-97b0-479f-a4e6-a6280051d6aa` و reclaim policy برابر `Retain` است. پذیرش در حالت `reuse-authoritative-pvc` انجام شد؛ state میزبان انسانی دوباره وارد PVC نشد.

نسخه‌های محلی پالایش‌شدهٔ رسیدهای نهایی:

| رسید در `E:/SJVS/Projects/Neo/.build/` | SHA-256 |
| --- | --- |
| `hr-r4-gateway-acceptance-20261017.json` | `d3c134de918964601098e1770a939a6d0915d9b3528e7ae46720f00bc921031c` |
| `hr-r4-live-acceptance-20261017.json` | `e598ee1a3442fe0eb3765b3288fac98c814e58c0c792f30aa1d0f334e923bf59` |
| `hr-r4-timer-restored-20261017.json` | `b5068c6a063ad18887dd717dac6d23df90473d6c7f60ee5e477ad97e076433b0` |
| `hr-r4-final-status-20261017.json` | `941daa7e0286283fc7147014f63f4c8ea1dcff7de474eb822c06e4e151cd2f9d` |

## بازیابی

برای بازیابی احتمالی، Application و PVC/PV موجود و policy برابر `Retain` حفظ شوند. هنگام شکست، رسیدهای operator و وضعیت freeze فرمان‌ها بررسی شوند؛ rollback فقط پس از اثبات سازگاری image قبلی با schema و آخرین state حفظ‌شده انجام شود. دیتابیس کامل قدیمی، seed مجدد یا PVC تازه جایگزین state جاری نشود.

برای بازیابی تنظیم Docker، ابتدا پایان اجرای oneshot و hold بودن timerها کنترل و hash دقیق drop-in و فایل اصلی واحد بررسی شود. فقط همین drop-in می‌تواند در بازگشت بررسی‌شده حذف و سپس `systemctl daemon-reload` اجرا شود؛ فایل اصلی، adapter و سایر drop-inها دست‌نخورده بمانند. حذف این اصلاح، مشکل مسیر پیش‌فرض `/root/.docker` را بازمی‌گرداند؛ حفاظت‌های systemd برای رفع آن غیرفعال نشوند.

رسیدهای محدود اپراتور در `/var/tmp/fanasa-hr-publication-20261017/` و رسید ارتقای verifier در `/var/lib/fanasa-organization-hr/verifier-trust-update-20261017/958e9abd658b-job-37/updated.json` نگهداری می‌شوند. این سند credential یا محتوای تنظیمات محرمانه ندارد. استقرار نسخهٔ جدید، پذیرش واقعی Gateway و بازگرداندن هر دو timer تکمیل شده‌اند.
