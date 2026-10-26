(() => {
  'use strict';
  const systems = {
    control: ['مرکز راهبری پلتفرم','هسته حاکمیت','مدیریت یکپارچه عرضه و دسترسی محصولات برای هر سازمان.',['ثبت محصول و مالک','سازمان، عضویت و نقش','اشتراک، مجوز و حسابرسی'],'مرجع مرکزی رجیستری است؛ پنجره واحد مصرف‌کننده کاتالوگ آن و محصولات مجری سیاست دسترسی هستند.','/Platform'],
    identity: ['هویت یکپارچه','سرویس مشترک','یک هویت قابل اعتماد در سراسر محصولات.',['ورود یکپارچه','احراز چندمرحله‌ای و نشست','کلاینت‌های مستقل محصول'],'Keycloak هویت را تأیید می‌کند؛ تصمیم مجوز دامنه در مرکز راهبری و محصول مقصد باقی می‌ماند.','/Governance'],
    portal: ['پنجره واحد','تجربه مشتری','پیدا کردن خدمات مجاز در زمینه سازمان فعال.',['کشف محصول و مرکز قابلیت','انتخاب سازمان فعال','ورود به سامانه‌های مجاز'],'کاتالوگ از رجیستری مرکزی دریافت می‌شود و کاتالوگ مستقل ایجاد نمی‌کند.','/Products'],
    developer: ['پنل توسعه‌دهندگان','توانمندی محصول','مدیریت پروژه و سرویس روی زیرساخت مشترک.',['پروژه، جزء و محیط','اتصال سرویس‌های داده','انتشار فراداده محصول'],'مالک فراداده استقرار است و ثبت محصول را از قرارداد رجیستری مرکزی انجام می‌دهد.','https://developer.fanasa.net.local/?workspace=studio&view=roadmap'],
    contracts: ['قرارداد و اقتصاد خدمات','دامنه کسب‌وکار','تبدیل توافق تجاری به خدمت و مصرف قابل توضیح.',['چرخه قرارداد و قیمت نسخه‌دار','اشتراک و مجوز خدمت','اعتبار، بدهی و صورتحساب'],'قرارداد به اشتراک و مجوز متصل می‌شود؛ محصول مصرف را در مرز خودش ثبت می‌کند.','/Accounting'],
    observe: ['دیده‌بانی خدمات','چشم‌انداز','دید یکپارچه از سلامت، وابستگی و کیفیت تجربه مشتری.',['نقشه وابستگی سرویس‌ها','متریک، لاگ و ردگیری','هشدار و بررسی رخداد'],'تله‌متری خدمت و درگاه را کنار هم قرار می‌دهد؛ نقشه حاضر مانیتورینگ زنده نیست.',null],
    agents: ['مدیریت کار و عامل‌ها','محصول و چشم‌انداز همکاری','هماهنگی کار تیم و عامل‌ها با خروجی قابل ارزیابی.',['فضای کار، پروژه و وظیفه','وابستگی و شواهد','معیار پذیرش و بازبین مستقل'],'هویت و سازمان مشترک را مصرف می‌کند؛ اجرای خودکار باید با مجوز و بازبینی کنترل شود.','/Products'],
    gateway: ['درگاه ارتباطات','معماری هدف','رابط مشترک ارتباطات میان محصولات و مصرف‌کنندگان.',['مسیریابی API','سیاست و محدودیت مصرف','همبستگی درخواست و ردگیری'],'نقطه مشترک سیاست ارتباطی است؛ کنترل مجوز داده همچنان در خدمت مقصد انجام می‌شود.','/OrganizationHr'],
    cloud: ['زیرساخت مشترک','پایه پلتفرم','اجرای محصولات با مرزهای روشن محیط و سازمان.',['Kubernetes و Helm','GitOps و مدیریت استقرار','سرویس‌های داده و منابع'],'پنل توسعه فراداده را مدیریت می‌کند و زیرساخت اجرای سرویس را بر عهده دارد.','/Platform'],
    policy: ['سیاست و تصمیم XACML','هدف','تصمیم نسخه‌دار بر پایه هویت، قرارداد، عضویت و شرایط معتبر.',['PAP: نگارش و انتشار سیاست؛ PIP: attributes معتبر','PDP: Permit، Deny، NotApplicable و Indeterminate','PEP: فقط Permit همراه obligations قابل اجرا'],'PAP/PIP در مرکز هستند. Gateway و backend تصمیم را اعمال می‌کنند. PDP شمارنده یا بودجه را تغییر نمی‌دهد؛ همه شروط لازم با AND برقرار می‌شوند.','/Governance'],
    limits: ['پذیرش اتمیک مصرف','هدف','اجرای محدودیت قرارداد پیش از ارسال درخواست به محصول.',['TPS و Burst در allocation قراردادی','Quota دوره‌ای و Concurrency مشترک','رزرو مالی مستقل از تصمیم PDP'],'همه replicaها از وضعیت اتمیک مشترک مصرف می‌کنند. نسخه تازه سیاست سهمیه را صفر نمی‌کند. Permit بدون admission موفق کافی نیست.',null],
    backend: ['محصول و مرز داده','پذیرش‌نشده','اجرای عملیات مجاز در سازمان مصرف‌کننده و مرز دامنه محصول.',['PEP مستقل در backend','consumerTenantId مستقل از مالک محصول','Idempotency کسب‌وکار و UsageEvent'],'Gateway مجوز مسیر را کنترل می‌کند؛ backend همچنان داده و عملیات دامنه را بررسی می‌کند. هدر tenant یا role مشتری مرجع اختیار نیست.','/Products'],
    accounting: ['مصرف و حسابرسی مالی','موجود','ثبت قابل تطبیق مصرف و محاسبه هزینه با نسخه قیمت معتبر.',['Usage idempotent و تراکنش محلی','اعتبار، بدهی و reservation مالی','Reconciliation، charge و صورتحساب'],'Gateway شاهد فراخوانی و محصول رویداد مصرف تولید می‌کند. Accounting مرجع اثر مالی است؛ تکرار رویداد نباید هزینه تکراری بسازد.','/Accounting'],
    ai: ['هوش مصنوعی و اعتبار','چشم‌انداز','دسترسی کنترل‌شده به مدل و ارائه‌دهنده پردازش.',['رابط مشترک مدل','اعتبار و تعرفه مصرف','تفکیک اعتبارنامه ارائه‌دهنده'],'واسط اعتبار، برنامه را از کلید ارائه‌دهنده جدا می‌کند و مصرف را به اکانتینگ متصل می‌کند.',null]
  };
  const baseline = {
    control: ['موجود','Fabric SQLite و رجیستری پایدار موجودند؛ فرم ثبت محصول در توسعه با Pipeline ۱۰ و حفظ داده تأیید شده است. ثبت محصول به‌تنهایی انتشار، دسترسی یا نمایش در پنجره واحد ایجاد نمی‌کند.'],
    identity: ['موجود','Keycloak و اتصال OIDC مستندند؛ پذیرش client و audience مستقل هر محصول/API باقی است.'],
    portal: ['پذیرش‌نشده','جمع‌بندی ۲۵ اکتبر ۲۰۲۶: بستهٔ محلی سورس dca2f5a با اصلاح تازه‌سازی کاتالوگ و درخواست scope اختیاری مالی ساخته شده است. مالی غیرفعال است؛ نصب بستهٔ نهایی روی VPS، پذیرش نشست انسانی و اتصال کامل گزارش مصرف به API مرکزی هنوز تأیید نشده‌اند. این وضعیت مربوط به پنجره واحد است، نه پنل توسعه.'],
    developer: ['پذیرش جزئی','جمع‌بندی ۲۶ اکتبر ۲۰۲۶: مسیر فرم، انتشار سورس، CI و استقرار خودکار پنل توسعه روی VPS شاهد دارد. شکست واقعی CI28، rollback خودکار و بازیابی سالم با CI29 با رسید مستقل پذیرفته شده‌اند. تکمیل مسیر قراردادها و NeoAgA باقی است؛ این پذیرش مربوط به پنل توسعه است، نه پنجره واحد یا همهٔ محصولات. localhost فقط محیط توسعه است.'],
    contracts: ['پذیرش‌نشده','شواهد تاریخی entitlement و receipt نیازمند تطبیق artifact هستند؛ projection نسخه‌دار Rule/Role/TPS گسترش هدف است.'],
    observe: ['هدف','نقشه تله‌متری، هشدار و مشاهده‌پذیری یکپارچه طراحی مقصد است؛ نمایش حاضر سلامت زنده را گزارش نمی‌کند.'],
    agents: ['پذیرش‌نشده','دامنه مدیریت کار موجود است؛ اجرای عامل تحت بودجه، workspace و بازبینی به پذیرش مستقل نیاز دارد.'],
    gateway: ['موجود','در بررسی ۱۴ اکتبر، خواندن HR از گیت‌وی توسعه پاسخ ۲۰۰ و اتصال مستقیم بدون گواهی گیت‌وی پاسخ ۴۰۳ داشت. این شاهد محدود، پذیرش کامل APISIX، سهمیه مشترک چند replica یا مسیر مالی را اثبات نمی‌کند.'],
    cloud: ['پذیرش‌نشده','زیرساخت و مسیر استقرار مبنا دارند؛ desired/observed، ack و منع bypass در مسیر مصرف واقعی باید آزموده شوند.'],
    ai: ['هدف','Provider adapter و Token Broker طراحی شده‌اند؛ اتصال end-to-end مدل/GPU، اعتبار و مصرف تأییدشده باقی است.'],
    policy: ['پذیرش‌نشده','نگارش و شبیه‌سازی typed و شواهد محدود OIDC/PDP و receiver با HTTPS/mTLS و داده مصنوعی موجود است. بستهٔ محلی ۲۵ اکتبر ۲۰۲۶ برای اصلاح تنظیمات HR/PIP و تولید درخواست ثبت منبع آماده و آزموده شده است؛ نصب تنظیمات در سرویس اصلی و پذیرش واقعی منبع، DB/PDP، انقضا و جداسازی سازمان باقی است. ورودی تجاری authoritative و انطباق کامل پروفایل منتخب پذیرش نشده‌اند.'],
    limits: ['پذیرش‌نشده','گزارش پایلوت یک admission و forwarding مجاز در single-Gateway را ثبت می‌کند. سهمیه مشترک چند replica، dedup عملیات، amendment بدون reset و بازیابی شمارنده هنوز پذیرش کامل ندارند.'],
    backend: ['موجود','API چارت سازمانی در توسعه فعال است؛ در بررسی ۱۴ اکتبر خواندن مجاز و رد اتصال مستقیم بدون گواهی تأیید شد. آماده‌بودن بستهٔ محلی HR/PIP در ۲۵ اکتبر، نصب یا فعال‌سازی اتصال جدید را تأیید نمی‌کند. SaaS با مالک متفاوت و زنجیره تجاری کامل هنوز پذیرش مستقل می‌خواهند.'],
    accounting: ['موجود','مصرف idempotent، snapshot قیمت و wiring ledger در سورس و آزمون محلی موجود است؛ فعال‌سازی metering پیش‌فرض خاموش است. اتصال authoritative قرارداد و admission، dispatcher/ack/DLQ و پذیرش مالی عملیاتی باقی‌اند.']
  };
  const root = document.querySelector('.architecture'); if (!root) return;
  const buttons = [...root.querySelectorAll('[data-system]')];
  document.getElementById('arch-title').setAttribute('aria-live','polite');
  function select(key, history = true) {
    if (!Object.hasOwn(systems,key)) return;
    const [title,stage,goal,capabilities,relation,url] = systems[key];
    const [status,evidence] = baseline[key];
    for (const [id,value] of [['title',title],['stage',status],['goal',goal],['relation',relation],['evidence',evidence]]) document.getElementById('arch-'+id).textContent=value;
    document.getElementById('arch-stage').setAttribute('data-status',status);
    document.getElementById('arch-capabilities').replaceChildren(...capabilities.map(value=>{const li=document.createElement('li');li.textContent=value;return li;}));
    const link=document.getElementById('arch-local');link.hidden=!url;if(url)link.href=url;
    buttons.forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.system===key)));
    if(history)window.history.replaceState(null,'','#system-'+key);
  }
  buttons.forEach(button=>button.addEventListener('click',()=>{
    select(button.dataset.system);
    if(window.matchMedia?.('(max-width: 1200px)').matches)
      document.getElementById('arch-title').scrollIntoView({block:'center',behavior:'instant'});
  }));
  root.querySelectorAll('[data-view]').forEach(button=>button.addEventListener('click',()=>{
    root.querySelectorAll('[data-view]').forEach(item=>item.setAttribute('aria-pressed',String(item===button)));
    buttons.forEach(item=>item.classList.toggle('arch-dim',button.dataset.view!=='all'&&item.dataset.layer!==button.dataset.view));
    const first=buttons.find(item=>button.dataset.view==='all'||item.dataset.layer===button.dataset.view);if(first)select(first.dataset.system);
  }));
  document.getElementById('arch-print').addEventListener('click',()=>window.print());
  window.addEventListener('hashchange',()=>select(location.hash.replace('#system-',''),false));
  select(Object.hasOwn(systems,location.hash.replace('#system-',''))?location.hash.replace('#system-',''):'control',false);
})();
