# پنجره واحد فن‌آسا · استقرار

CI/CD روی `develop` محیط توسعه و روی `master` محیط production را deploy می‌کند.
هر محیط release مستقل، systemd service و environment file جدا دارد.

مقادیر لازم در `/etc/fanasa-unified-portal/<environment>.env`:

```dotenv
FANASA_PORTAL_PORT=5190
Authentication__Authority=https://<identity-host>/realms/fanasa
Authentication__ClientId=fanasa-unified-portal-web
Authentication__ClientSecret=
Products__WorkManagementUrl=https://<work-host>
Products__ContractManagementUrl=https://<contract-host>
```

برای development می‌توان Authority را loopback و محیط سرویس را Development
تنظیم کرد. در production باید HTTPS و callback دقیق `/signin-oidc` در client
همین محصول در Keycloak ثبت شود. پنجره واحد user store یا password ندارد و هر
محصول هم client مستقل خودش را نگه می‌دارد.
