# Neo telemetry: ITelementryBehaviour and TelemetryAttribute

This guide matches the contract fingerprint in manifest.json, including the telemetry runtime repairs.

ITelementryBehaviour is the explicit wrapper around one operation. It starts timing, starts an Activity, executes the business delegate, finalizes metrics and rethrows the original business exception. Successful null responses are successful operations. Cancellation propagates and currently records Error.

TelemetryAttribute describes component, serviceName and ActivityKind for one method. It does not run by itself. AddScopedWithTelemetry<IService, Implementation>() registers an interface proxy that reads the implementation/interface attribute and delegates to ITelementryBehaviour. Resolve IService through DI. Concrete calls and self-invocation bypass interface interception.

Use attribute mode for service-wide declarative instrumentation and manual mode for a specific boundary. The runnable TelemetryDemo contains both registrations. Do not double-wrap the same operation unless distinct nested spans are intended.

The proxy preserves Task, Task<T>, ValueTask, ValueTask<T>, synchronous results and void. CancellationToken is located by its actual parameter type/position; nullable and parameterless calls are allowed. An implementation method's attribute takes precedence when both locations are annotated. Async calls do not block while waiting for business work. Deferred enumeration of IAsyncEnumerable is not measured.

Register IRequesterUser for the application, options with a stable ApplicationName, ITelementryObject and ITelementryBehaviour. The demo uses synthetic requester data and NullLogger so payload logging does not clutter its output. Production applications should use their existing requester and logging setup.

AddNeoOpenTelementry reads the configuration object/section supplied to it, binds TelemetryOptions and subscribes tracing and metrics to ApplicationName. A listener or exporter is needed to receive custom spans. The sample's ActivityListener and MeterListener run locally without a collector/server. An external OTLP destination is optional and separately configured.

Metrics: request.total, request.success, request.failure, request.duration in milliseconds, request.inflights. The stopwatch now starts before the operation. Logical async context keeps completion names and tags separate across concurrent and nested calls. TelementryObject implements IDisposable so DI can release its ActivitySource and Meter.

Framework logging includes request and response objects. Use non-sensitive data and consider the cardinality of client.user.id/client.correlation.id metric tags. This recipe is not a trace collection or production-log search service: the MCP provides developer guidance and code.

Old source revisions had AddScopedWithTelemetry disabled and incorrect Task<T>/DispatchProxy behavior. Source hashes, not the broad version number, identify the repaired snapshot.
