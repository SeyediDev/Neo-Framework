# Public application domains — source contract

The local source accepts approved HTTPS application URLs at the exact namespace apex `app.fanasacloud.ir` or one DNS label below it, such as `platform.app.fanasacloud.ir`. Existing application hosts ending in `.fanasa.net.local` remain compatible. Arbitrary nested names, the parent `fanasacloud.ir`, sibling suffixes and suffix-confusion names are rejected. Existing URL restrictions remain: HTTPS on port 443, no user information, query or fragment. A board entry must still use its product's host.

This policy is shared by central product registration and the consumer catalog used by the mobile self-service catalog. It does not create another registry or change product IDs, owners, memberships, grants, audience or scope requirements.

## Developer canonical origin

`Platform__DeveloperPublicUrl` selects the origin used for newly registered developer project links and runtime entries. The only accepted values are:

- `https://developer.fanasa.net.local` — the unchanged default when the setting is absent.
- `https://developer.app.fanasacloud.ir` — the approved public origin.

The existing legacy developer runtime entry remains recognized during migration. Another approved application hostname cannot substitute for the developer runtime. Changing this setting does not rewrite existing products or their authorization data.

## TLS and deployment boundary

The `PlatformBearer` and organization bearer metadata transports use ordinary system certificate validation for public application names, even when `Platform__CaFile` is configured. The configured private root applies only to legacy `.fanasa.net.local` names; it cannot validate a public name. Name errors, missing certificates, foreign names and plaintext endpoints are rejected by the custom handler. Redirects are disabled for these transports. This change does not modify the web OIDC transport or any operating-system trust store.

Authentication authorities and exact client callback/logout registrations remain deployment configuration. An issuer cutover must coordinate its consumers; accepting a public application URL does not make multiple issuers valid. Mobile catalog activation remains opt-in under the existing [mobile contract](MOBILE-CATALOG.fa.md).

These are local source changes. They do not perform DNS, certificate issuance, routing, SSO configuration, registration mutations or deployment, and do not prove real browser/device SSO acceptance. A deployment requires the reviewed source, selected settings and independent DNS/TLS, issuer, callback, authorization and restart checks.
