# Durable DDD and messaging example

This example connects a Neo domain event to a SQL Server transactional Bus Outbox,
RabbitMQ, a persisted MassTransit saga, and transactional consumer effects.
It uses the repository's MassTransit **8.4.1** packages. Payment and inventory are
simulated business services; there is no real payment provider.

From the Neo repository root, with .NET 10 and Docker Compose:

```powershell
docker compose -f tools/Neo.Companion/samples/DurableMessagingDemo/compose.yaml up -d --wait
dotnet run --project tools/Neo.Companion/samples/DurableMessagingDemo -- --InitializeDatabase=true
```

Stop the smaller MessagingDemo Compose stack first if it occupies RabbitMQ ports.
The API binds to localhost:5089; SQL Server uses localhost:14333. Public credentials
in Compose/appsettings are only for this local development example. SQL Server
Developer is for development/testing. Initialization only permits database names
beginning with `NeoDurableDemo`, uses EnsureCreated, and never drops a database.
Use reviewed EF migrations, normal application permissions and secret configuration
for an existing application. Running this sample does not migrate its existing database.

```powershell
$id = [guid]::NewGuid()
$body = @{ orderId=$id; declinePayment=$true; releaseFailures=1 } | ConvertTo-Json
Invoke-RestMethod http://localhost:5089/orders -Method Post -ContentType application/json -Body $body
Invoke-RestMethod "http://localhost:5089/state/$id"
```

Expected: `Cancelled`, one reservation and one release. A successful payment gives
`Completed`. Three simulated release failures give `ManualReview`; retry with
`POST /orders/{id}/retry-compensation`. An unknown payment result also gives
`ManualReview`, but the retry endpoint rejects compensation until reconciled.
These teaching APIs have no authentication; bind to loopback only.

`POST /orders?rollback=true` stages domain/integration events, then rolls back the
outer transaction. Neither the order nor its outgoing message should remain.
`--DeferDelivery=true` disables the Bus Outbox delivery worker for a restart exercise:
create an order, inspect `/outbox`, stop the process, then restart without that flag.
The committed event is delivered after restart. Saga and participant ledgers survive
as well. A repeated order ID returns 409 and does not publish a new order.

Two processes may share this database and endpoint prefix. Run the second with
`--urls=http://localhost:5090`; SQL pessimistic saga locking, transactional consumer
outbox and business primary keys coordinate their work. Fault-injection attempt
counters alone are process-local; they simulate dependency faults, not business data.

```powershell
dotnet build tools/Neo.Companion/samples/DurableMessagingDemo
python tools/Neo.Companion/scripts/smoke_durable_messaging.py --server-dll tools/Neo.Companion/samples/DurableMessagingDemo/bin/Debug/net10.0/DurableMessagingDemo.dll
```

The smoke needs free API ports 5089/5090 and an isolated demo database/broker. It tests
rollback, publisher restart, two workers, persisted manual review and compensation.
It leaves test rows/error messages for inspection; Compose `down` keeps volumes.

Applications must still define authentication, contract migration/retention, overdue
saga deadlines, reconciliation with real providers, and provider idempotency keys.
Database atomicity does not roll back an HTTP payment call. Inbox deduplication has
a finite window, so the persistent business keys remain necessary.

Framework usage: call `AddNeoSqlServerOutbox<TBusinessContext>()` inside the
`AddNeoRabbitMq` registration callback, then `model.AddNeoMessagingOutbox()` in that
same scoped context's model. Use scoped `IPublishEndpoint` and the same DbContext,
not a separately resolved bus/global publisher. All consumer effects using the helper
must share that context/transaction. The sample also references MessagingDemo's
contracts and state machine; keep both sample folders when copying it.
