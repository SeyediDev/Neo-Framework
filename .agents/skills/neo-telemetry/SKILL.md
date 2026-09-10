---
name: neo-telemetry
description: Add, explain or diagnose instrumentation in SeyediDev's Neo Framework using ITelementryBehaviour, TelemetryAttribute, AddScopedWithTelemetry and Neo's tracing/metrics setup. Use for Neo service telemetry requests; not for generic telemetry projects or the Neo blockchain.
---

# Instrument a Neo service

Read the consuming application's actual Neo contracts and registration path before editing. Keep the public spelling `ITelementryBehaviour` and namespace `Neo.Domain.Features.Telementry`; do not rename framework APIs as part of instrumentation.

Use `neo_search_docs` and `neo_get_telemetry_recipe` when the Neo MCP is available. Read [telemetry.md](references/telemetry.md) for the workflow and semantics. Without MCP, inspect the referenced source/package and the application's existing instrumented services. A bundled source fingerprint is evidence for that snapshot, not proof of the project's installed version.

Choose one instrumentation path per service operation:

- Use `[Telemetry(component, serviceName, ActivityKind)]` on an interface or implementation method and `AddScopedWithTelemetry<IService, Service>()` for reusable automatic service interception.
- Use `ITelementryBehaviour.HandleRequestResponse` or `HandleRequest` for explicit boundaries inside application code. Forward the real cancellation token and preserve the operation's return type.

Check that automatic interception is enabled in the project's Neo revision. Older code registered the service directly and had runtime proxy defects. If that implementation is present, report the version mismatch and use an explicit wrapper within the task's scope; do not pretend the attribute works or silently replace the framework.

Register the current IRequesterUser implementation, TelemetryOptions, ITelementryObject and ITelementryBehaviour through the project's established DI path. Attribute registration resolves the implementation from DI and needs an interface service contract. Ensure trace/meter subscriptions match TelemetryOptions.ApplicationName. A console/ActivityListener demo needs no remote collector.

Verify observable behavior: unchanged return value, an expected span name/status, nonzero duration for actual work, original exceptions and cancellation, and isolation of tags in concurrent/nested calls. Test relevant async return shapes. Unannotated methods must keep their normal behavior. Do not prove success merely by finding an attribute or counting generated lines.

Explain the result in the user's language. In learning mode, connect the attribute or wrapper to span creation, metrics, completion and export. Keep application secrets out of request/response objects and tags passed through this telemetry logger. Distinguish instrumentation from exporting and from the developer MCP server.
