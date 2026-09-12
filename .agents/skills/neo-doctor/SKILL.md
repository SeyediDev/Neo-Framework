---
name: neo-doctor
description: Diagnose Neo Framework application startup, dependency injection and missing telemetry using source evidence, Neo Doctor findings and focused runtime checks. Use when a Neo application fails to resolve services or its TelemetryAttribute instrumentation does not behave as expected.
---

# Diagnose a Neo application

Start with the reported symptom and the consuming application's actual source/package revision. Keep the public spelling `ITelementryBehaviour` and `Telementry`. The bundled fingerprint identifies reference code; it does not prove the application uses that version.

When available, use `neo_diagnose_project` with `NEO_PROJECT_ROOT` configured to one consuming application. Set `configurationSection` to the JSON section actually passed to the Neo exporter setup (default `TelemetryOptions`, empty string for root). If the root covers multiple unrelated apps, narrow it before using absence findings. Without MCP, inspect the same registration, attribute and configuration paths directly.

Read [diagnostics.md](references/diagnostics.md) to interpret rule codes and coverage limits. Treat every result as a candidate. Follow its file locations and active call path before editing. An unrecognized custom registration extension is not proof of a missing service. A direct registration may intentionally use a manual wrapper. An `incomplete` scan or an empty findings list cannot establish runtime health.

For DI failures, trace the actual service constructor graph and resolve the affected interface inside a scope. Reuse the application's requester implementation, registration extensions and lifetimes; do not insert a dummy user or change a lifetime merely to silence a finding.

For missing telemetry, distinguish proxy registration, use of the resolved interface, invocation, listener subscription, and export. Check the actual Neo revision: older implementations disabled the proxy. Preserve return values, cancellation and exceptions; avoid wrapping a method twice. Use `neo_get_telemetry_recipe` or the `neo-telemetry` references for the instrumentation details when available.

Make the smallest source change justified by the symptom. For an authorized fix, verify the failing behavior before and after with a focused test: service resolution, observed span/status, or original exception. Static analysis does not require running the project; select runtime checks appropriate to the application's external dependencies and the user's scope.

Explain the cause, evidence, fix and verification in the user's language. Label anything still unverified. Keep tokens, connection strings, request payloads and configuration secrets out of diagnostic output; ask for the relevant exception and redacted context rather than a full settings dump.
