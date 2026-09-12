# استفاده از GenericCrudControllerBase

این Controller برای داده‌های سادهٔ مدیریتی با نگاشت مستقیم DTO به Entity مناسب است. برای عملیات دامنه مانند تأیید سفارش، انتقال وجه یا تغییر وضعیت دارای مجوز و قواعد اختصاصی، از Handler و Endpoint اختصاصی استفاده کنید.

## قرارداد فعلی

- نوع پایه: `GenericCrudControllerBase<TDto, TEntity, TKey>`؛ DTO باید `IDto<TKey>` و Entity باید `IEntity<TKey>` و `IDomainEventEntity` را پیاده کند. هر دو سازندهٔ بدون پارامتر دارند و کلید از نوع struct است.
- مسیر مدیریتی پیش‌فرض: `api/v{version}/admin/[controller]`. واژهٔ admin در مسیر، مجوز دسترسی ایجاد نمی‌کند؛ policy/authentication برنامه را جداگانه تنظیم کنید.
- Handlerها و سرویس‌های هر action صریحاً از DI دریافت می‌شوند. ثبت generic Handlerها، repositoryهای واقعی برنامه، `IGenericServiceHandler` و وابستگی‌های آن لازم است.
- موفقیت Create: کد 201، شناسهٔ برگشتی Handler در `dto.Id` و Location تولیدشده توسط مسیریابی MVC. اگر Handler شناسه برنگرداند، کد 400 برمی‌گردد.
- تولید Location نام پیش‌فرض action در MVC، یعنی `GetById`، را استفاده می‌کند. اگر نام action یا تنظیم حذف پسوند Async را عوض کردید، `GetResourceLocation` را override کنید.
- Update بدون شناسه یا با تفاوت شناسهٔ مسیر و بدنه: کد 400، بدون فراخوانی Handler.
- Update و Delete نتیجهٔ `Unit` دارند؛ شکست از مسیر exception منتقل می‌شود. Handler پیش‌فرض برای رکورد ناموجود `NotFoundException` می‌دهد. `CustomExceptionHandler` نئو را ثبت و فعال کنید تا این exception به HTTP 404 تبدیل شود.
- رفتار Delete تغییر کرده است: حذف رکورد ناموجود قبلاً بی‌صدا تمام می‌شد؛ اکنون Handler پیش‌فرض exception می‌دهد. برنامه‌هایی که Delete تکراری را همیشه 204 می‌خواهند باید این سیاست را صریحاً پیاده کنند.

## ترجمه و ذخیره‌سازی

`CultureFields` به‌صورت پیش‌فرض خالی است. در این حالت هیچ query یا write ترجمه‌ای اجرا نمی‌شود و کلیدهای Guid نیز وارد تبدیل عددی ترجمه نمی‌شوند. سرویس‌های پارامتری action همچنان باید در DI ثبت شوند.

وقتی ترجمه فعال است، `CultureTerm.SubjectId` عدد int است؛ کلید باید قابل تبدیل بدون سرریز به int باشد. این قابلیت نگاشت عمومی برای Guid یا همهٔ مقادیر long نیست. DTO باید فیلدهای نام‌برده در CultureFields را داشته باشد.

Create و Update، تغییرات ترجمه را از UnitOfWork همان repository ذخیره می‌کنند؛ Delete فقط ترجمه‌های شناسهٔ درخواستی را منقضی و ذخیره می‌کند. نبود ترجمه هنگام خواندن، مقدار اصلی DTO را پاک نمی‌کند. cancellation token به عملیات async ترجمه منتقل می‌شود.

**ذخیرهٔ موجودیت و ترجمه اتمیک نیست:** Handler اصلی ابتدا موجودیت را ذخیره می‌کند و سپس ترجمه ذخیره می‌شود. شکست مرحلهٔ دوم می‌تواند موجودیت ذخیره‌شده باقی بگذارد. برای تضمین تراکنش مشترک، عملیات را در لایهٔ Application و با UnitOfWork مناسب پیاده کنید.

این Controller جایگزین validation دامنه یا کنترل فیلدهای قابل ویرایش نیست. Handlerهای generic از نگاشت DTO استفاده می‌کنند؛ فقط فیلدهای مجاز را در DTO ورودی قرار دهید و اعتبارسنجی و مجوزهای واقعی برنامه را تست کنید.

## اعتبارسنجی

تست‌های `tests/Neo.Endpoint.IntegrationTests/Controller/GenericCrudControllerTests.cs` رفتار HTTP، دنبال‌کردن Location، شناسهٔ ایجادشده، ترجمهٔ خالی، ذخیرهٔ ترجمه، cancellation و خطای رکورد ناموجود را پوشش می‌دهند. تست HTTP از TestServer و Handlerهای کنترل‌شده استفاده می‌کند؛ تست تراکنش دیتابیس واقعی محسوب نمی‌شود.

```powershell
dotnet test tests/Neo.Endpoint.IntegrationTests/Neo.Endpoint.IntegrationTests.csproj --filter FullyQualifiedName~GenericCrudControllerTests
```
