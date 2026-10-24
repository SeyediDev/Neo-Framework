# Offline structural schema checks

From the Neo repository root:

```powershell
python docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py
python -B -m unittest discover -s docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check -p 'test_*.py' -v
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py --event docs/fanasa-platform-control-center/workstreams/usage-accounting/fixtures/integration-usage-event.v1.json
```

The command validates the shipped schema definition, the standalone UsageEvent,
and every `events` member of the reconciliation fixture. The reconciliation
wrapper and financial reconciliation assertions are outside this structural
check. Duplicate JSON keys and non-JSON numeric literals are rejected before
validation, including in schema definitions. No dependencies are required and
nothing is downloaded or installed.

`--event` validates one generated event against the same unchanged schema. It is
useful for the common compatibility output; the sample separately proves the
actual policy/admission references and committed business evidence.

This is a **limited keyword profile, not a general JSON Schema validator**. It
supports object/string/integer types, `properties`, Boolean
`additionalProperties`, `required`, `const`, `enum`, `minimum`, `maximum`, string
lengths, the package's portable ASCII regular expressions via Python `re`, UUID
and Gregorian `date-time` formats, and the four schema annotation keywords.
Single named types only; Boolean schemas, reference resolution, combinators,
array item schemas and leap seconds are unsupported. Unknown keywords, formats,
type unions and malformed keyword values fail schema definition checks instead
of being ignored. UTC is asserted by the shipped schema's terminal uppercase
`Z` pattern as well as timestamp validation. Regex evaluation uses search
semantics, as the schema requires; decimal whitespace must also be rejected by
the independent domain validator.

These checks establish message structure. They cannot authenticate a producer,
prove owner/consumer/contract bindings, authorize a tariff, compare effective
times or establish a billable backend commit. The independent sample and its
reconciliation checks must establish those semantics. Production intake needs
an accepted full Draft 2020-12 implementation with format assertions plus
trusted domain validation; this tooling makes no standard conformance claim.
