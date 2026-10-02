# استقرار پنل مدیریت دسترسی فن‌آسا

سرویس باید با user جداگانه و environment file خارج از Git اجرا شود:

```dotenv
FANASA_ACCESS_PORT=5191
Authentication__Authority=https://<identity-host>/realms/fanasa
Authentication__ClientId=fanasa-access-management-web
Authentication__ClientSecret=
```

در Keycloak برای همین client، callback دقیق زیر ثبت شود:

```text
https://<access-host>/signin-oidc
```

و logout return URL نیز دقیق و allow-listed باشد. برای local development می‌توان
از `http://127.0.0.1:5191/signin-oidc` استفاده کرد.
