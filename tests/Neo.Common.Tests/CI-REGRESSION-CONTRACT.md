# Common CI Regression Contract

Reviewed source revision, 2026-10-15: this revision contains only the Common
corrections/tests described below and the separately documented Keycloak
credential-flow correction. The owner authorized scoped commits and pushes to
internal GitLab, not GitHub or unrelated dirty product work. This is not CI,
scanner, credential revocation or deployment acceptance. Older local results
and their uncommitted status below are historical observations.

Local source corrections on 2026-10-13, based on committed source
`9698138aa22434967142fda23de0f12643689eaf`. These are uncommitted working-tree
changes, not acceptance of that unchanged commit or permission to release it.

- Kebab case keeps acronym groups and uses invariant lowercase.
- `Int`/`Long` truncate fractions toward zero. Checked overflow/nonfinite or
  invalid input retains the existing zero fallback. String numbers use the
  current culture, including exponent syntax; decimal conversion preserves
  64-bit integer precision rather than passing strings through a double.
- Dictionary-to-object mapping converts using invariant TypeDescriptor
  converters. Empty nullable values become null. Only public instance writable
  properties/non-readonly fields are assigned; private/static/indexer/read-only
  members and unknown keys are ignored. Invalid conversion still throws.
- Known file signatures precede the printable-text heuristic. PDF/PostScript
  identification is not structural validation, malware scanning or an upload
  security policy. Existing signatures/format limitations remain.
- PascalCase implementation is unchanged: the EF table-name consumer needs
  full words, not initials. Incorrect acronym expectations were corrected.
- Generic null/default detection is unchanged: an empty string is not
  `default(string)`. Its test now reflects the existing contract; this does not
  alter the separate string null/whitespace helpers.

`Extensions/CiRegressionTests.cs` covers these boundaries without skips or
external infrastructure. Domain/Application and reviewed consumer tests should
be run separately with native counters. The original failed source results and
historical-secret finding must remain visible. Local passes are not trusted CI
evidence or security/license/dependency-audit acceptance.

Local verification on2026-10-13: canonical native Common185, Domain23 and
Application13 passed with no failures/skips/not-run. Separate modified archive
consumers passed Discovery47, Portal49, Node19 and the Endpoint build. A scoped
Companion native run passed15 CrudResourceTests/ProductTests using local
SQLite, including transaction/concurrency and product create/read/restart and
invalid-input behavior. This is not the full Companion suite or external
SQL/Redis/operational acceptance. Fluent Assertions/MediatR license notices
remain unresolved. Evidence is in sibling NeoCluster's
`.build/neo-ci-common-integration-receipt-20261013.json`; at that observation
the source remained uncommitted/unpublished.

The affected helpers are not in Neo.Companion's pinned source manifest. Its
unchanged repository/controller/EF/event contracts and fingerprints must not be
refreshed merely because these Common fixes were applied.
