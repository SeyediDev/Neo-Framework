# Legacy Keycloak Credential Migration

Local source correction, 2026-10-15. Not deployed; no existing identity, realm,
client, password, session, authorization grant or TLS trust was changed.

`KeycloakIdpService.GetUserTokenAsync(mobile, cancellationToken)` previously
used a shared password. A mobile number is not proof of identity. The method
keeps its nullable API signature but now returns null without contacting the
identity provider; pre-cancellation is honored. There is no configuration
switch or shared-password fallback that silently restores the insecure flow.

`AddUserAsync` keeps its existing administrative user and role operations but
sends an empty credentials array. It no longer creates a shared password or
generates/delivers a replacement password. New users must enroll/authenticate
through the configured identity provider's verified flow. This change does
not implement enrollment, OTP verification, token exchange or an OIDC client.
Applications must not interpret null as successful authentication.

Explicit caller-password and refresh-token APIs are unchanged. Their presence
is not a recommendation to enable password grants or weaken existing SSO.
The in-memory development adapter is unchanged and is not production SSO.

The bounded in-tree caller search found no invocation of the mobile-only
method or AddUserAsync outside their implementations/interfaces. It excluded
generated/build outputs and does not exclude external package consumers or
runtime configuration. Before release, inventory deployed consumers and
replace any mobile-only flow with independently verified authentication.

The historical password finding is NOT resolved by editing current source:
identify the affected realm/users and revoke/reset applicable credentials and
sessions with an owner-reviewed migration and recovery path. Do not perform a
blanket reset or rotate unrelated client secrets. Do not suppress the scanner,
rewrite history or claim the old password is invalid without evidence.

`KeycloakCredentialBoundaryTests` uses synthetic HTTP handlers and credentials
to check no-network legacy denial/cancellation, credential-free registration
and role requests, no fallback on registration failure, and preserved explicit
password/refresh request behavior. These are local contract tests, not real
Keycloak login/enrollment, credential revocation, scanner or deployment tests.
Existing HTTP transport/TLS policy needs its own security review; these tests
do not certify certificate verification or complete Zero Trust.
