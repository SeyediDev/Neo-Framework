# Server-driven SPA

The existing Razor/BFF pages remain the server-rendered source. `workbench.js`
intercepts same-origin work, workspace and product navigation and forms using
fetch, replaces only `main`, and updates History and title without document reload.
No React/Vue migration or new API permissions are introduced. Native forms still
work without JavaScript. Login/logout, external links and downloads remain native.

POSTs retain antiforgery, request IDs and record versions; the API remains the
authority for grants, workspace boundaries, ownership and lifecycle transitions.
Only one request runs at a time. Mutations are never automatically retried: a
lost response may still represent a committed write. Verify current state first.
A stale-version/validation/permission failure leaves the current DOM and inputs
intact. Refresh obtains fresh hidden versions while restoring editable drafts.
Async cookie challenges return 401/403 instead of redirecting fetch to an issuer.
The navigation header grants no access. CSP permits self-hosted scripts/connects,
not inline code or eval; fetched documents cannot execute scripts.

Back/forward refetch current server data. Edited POST fields, open details and
scroll positions are retained in memory for up to 30 URLs, never in local/session
storage. Hidden CSRF/version/request IDs, passwords, files and fetched HTML are
not cached. Successful submissions discard that form's draft; other edited forms
remain. Reload/closing the document loses drafts (with a dirty-write warning).
This is not offline persistence, live polling, a service worker or multi-tab sync.
Authentication/session and single-instance deployment limits in WEB.md still apply.

Acceptance checks: keep the `html[data-spa-document]` value and performance time
origin stable across links, filters, creation and edits; verify URL/back/forward,
scroll and drafts, error 400/403/409, expired login 401, network loss and double
submit. Test native fallback separately. HTTP tests alone do not prove browser
history, focus or no-reload behavior. Use isolated test records for mutations.

## Verification (2026-09-30)

- Product test build: zero warnings/errors. Focused SpaHost/Host/ProductPage/
  LocalDevelopmentAccess/WebManagement suite: 37 passed, zero skipped/failed;
  SQL form tests used the separate verification database, including both native
  and `X-Neo-Navigation` requests, antiforgery, scopes, permissions and stale versions.
- `node --test products/Neo.AgentOrchestration/tests/client/workbench.test.cjs`:
  eight unit tests for handler selection, duplicate submission, errors and no retry.
  These use a small fake DOM; they do not substitute for browser testing.
- Live Chromium local acceptance: document ID stayed unchanged across product-to-
  board navigation, combined project/domain filtering, draft back/forward, creation,
  claim, estimate, timer stop, notes, archive/restore and item back/forward. Editing
  a second form survived submission of the first. Two tabs produced a real stale
  version; the rejected input survived refresh and subsequent explicit submission.
- A separate temporary host with local access disabled returned 401 to navigation
  without losing the page. Stopping only that host verified network-error display
  and retention of the original document. It was then removed from execution.
- Limits: no live OIDC issuer login or network drop midway through a real mutation;
  ambiguous POST failures are covered by the client unit test. Mobile accessibility
  and cross-browser acceptance remain separate checks. Human acceptance is not
  inferred from these automated/developer checks.
