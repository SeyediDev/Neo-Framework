# HR read PDP transport schemas

`hr-read-pdp-request.v1.schema.json` describes the exact 28-field evaluation body accepted by `HrReadPdpWire`. `hr-read-pdp-response.v1.schema.json` describes the complete 33-field body emitted by `HrReadPdpService`. Both use `hr-read-pdp-common.v1.schema.json` for shared definitions and structural route/action and ownership-kind correlation.

These schemas describe the designed transport. They do not authenticate requests, grant delegation, establish current registry/tenant/membership/role/grant authority, admit an allocation or fulfill backend obligations. Unknown root fields are rejected, including caller-supplied flags that claim those outcomes. A nested `Permit` retains the HR3 admission and resource obligations, and `admissionConfirmed` is always `false`.

Runtime checks remain necessary for duplicate JSON properties, strict UTF-8, byte/depth limits, canonical number spelling, positive Int64 history revisions, exact configured tuple equality, cross-field identity and ownership equality, token hash/expiry correlation, current authority, deadline ordering and UTC calendar-month bounds. JSON Schema validates numerical values rather than original number spelling and cannot compare arbitrary sibling values. A shape-valid body is not evidence of authorization.

The two dependency files below are copied byte for byte from `deploy/fanasa-organization-hr/hr-read-profile-v3/candidate/schemas/`. The immutable 80-file HR3 package was read only and was not modified. The response uses the same-directory reference `decision.v3.schema.json`; that decision uses the same-directory reference `limits.v2.schema.json`.

| Unchanged dependency | SHA-256 |
| --- | --- |
| `decision.v3.schema.json` | `a1ee58445cc9d4c76c9370d2696a08de4709c4581ffa360c89029dbb0ddfc029` |
| `limits.v2.schema.json` | `1934b6281a20e144dca7762c4342f131bcde49e6547e018a6ca0c11de9e6981c` |

Keep these dependencies in the same schema directory when packaging the PDP candidate. The repository's bounded `ContractSchemaValidator` resolves local filenames and JSON pointers; it does not retrieve external schemas. When a test output already contains the identical immutable decision and limits schemas, link only the three new PDP schema files into that directory to avoid duplicate output paths.
