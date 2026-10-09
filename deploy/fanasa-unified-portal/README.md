# پنجره واحد فن‌آسا · استقرار

CI/CD روی `develop` محیط توسعه و روی `master` محیط production را deploy می‌کند.
هر محیط release مستقل، systemd service و environment file جدا دارد.

مقادیر لازم در `/etc/fanasa-unified-portal/<environment>.env`:

```dotenv
FANASA_PORTAL_PORT=5190
Authentication__Authority=https://<identity-host>/realms/fanasa
Authentication__ClientId=fanasa-unified-portal-web
Authentication__ClientSecret=
PlatformControlCenter__BaseUrl=https://<platform-host>
PlatformControlCenter__Authority=https://<identity-host>/realms/fanasa
PlatformControlCenter__ClientId=fanasa-unified-portal-service
PlatformControlCenter__ClientSecret=<server-side-secret>
PlatformControlCenter__Scope=platform.catalog
```

برای development می‌توان Authority را loopback و محیط سرویس را Development
تنظیم کرد. در production باید HTTPS و callback دقیق `/signin-oidc` در client
همین محصول در Keycloak ثبت شود. پنجره واحد user store یا password ندارد و هر
محصول هم client مستقل خودش را نگه می‌دارد.

کاتالوگ از `application-tenants` و `products?subject=...&tenant=...` خوانده می‌شود؛
نشانی محصولات در تنظیمات پنجره واحد نگهداری نمی‌شود. scope سرویس توسعه‌دهنده
`platform.registry` جایگزین scope پنجره واحد نیست. این تنظیمات، مجوز یا client جدید
ایجاد نمی‌کنند؛ audience و scope باید در کلاینت اختصاصی موجود تأیید شده باشند.
