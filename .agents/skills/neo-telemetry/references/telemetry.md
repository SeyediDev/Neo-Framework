# Neo telemetry workflow

Current source names are intentionally preserved: `ITelementryBehaviour`, `ITelementryObject`, `TelementryBehaviour`, `TelementryObject`, `AddNeoOpenTelementry` and `TelemetryAttribute`.

## Automatic service instrumentation

Put `[Telemetry("catalog", "lookup", ActivityKind.Internal)]` on the method of an interface or its implementation. Register `AddScopedWithTelemetry<IProductLookup, ProductLookup>()` and resolve the interface from DI. Calling `new ProductLookup()` or calling the concrete type bypasses the interface proxy. Calls from one method directly to another on the same target also bypass it. Implementation metadata takes precedence over interface metadata when both are present.

The repaired invocation engine preserves `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, synchronous results and void. Generic methods, nullable/empty requests and a CancellationToken at any argument position are supported. An async operation returns its awaitable without blocking the caller. Async streams/custom awaitables are not enumerated by this instrumentation; their later work is outside the measured call boundary.

Attribute arguments must be compile-time constants. Use a literal such as "domain.service" where a static property's value cannot be an attribute argument.

## Explicit instrumentation

`HandleRequestResponse<TRequest,TResponse>(next, request, component, serviceName, activityKind, extraTags, cancellationToken)` wraps a result-returning asynchronous operation. `HandleRequest` wraps Task operations without a result. Caller name/line are optional caller-info parameters. Reserved tag keys keep their generated values when an extra tag has the same key.

Time is measured by a running stopwatch. Completion without an exception is success, including a null result. Exceptions and cancellation propagate; cancellation currently contributes to the error status/failure metric. Per-call state is isolated across async and nested operations. A scoped instance may therefore serve overlapping calls without cross-contaminating completion tags.

## Registration and export

`AddNeoDomainServices(configuration)` registers the domain telemetry services. The application supplies IRequesterUser and appropriate TelemetryOptions. `AddNeoOpenTelementry(configurationSection)` binds those options and subscribes its tracing/metrics providers to ApplicationName. When passing `configuration.GetSection("Telemetry")`, keep all telemetry option keys in that section.

A listener/exporter must subscribe to the ActivitySource for spans to be created. Meters use the same application name. An OTLP endpoint is optional: local examples can use ActivityListener/MeterListener or console export. External dashboards receive data only after exporter and destination configuration.

Metrics include request.total, request.success, request.failure, request.duration (ms) and request.inflights. Request/response payloads and user/correlation tags are logged by the framework implementation. Use small non-sensitive request/response shapes; the companion recipe does not implement payload redaction. User/correlation identifiers can also increase metric cardinality in this implementation.

## Source and examples

The canonical source files are under `src/Neo.Domain/Features/Telementry` and `src/Neo.Infrastructure/Features/Telementry/DependencyInjection.cs`. The working sample is `tools/Neo.Companion/samples/TelemetryDemo`; use `manual` or `attribute` as its mode. MCP's `neo_get_telemetry_recipe` returns the sample and its exact source-contract fingerprint.

Earlier source revisions disabled the proxy registration, mixed async delegates with incompatible generic signatures, used shared completion state and did not start the timer. Do not apply the repaired behavior claims to an older package without checking its source.
