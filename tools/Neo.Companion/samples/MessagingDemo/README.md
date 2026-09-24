# RabbitMQ learning sample

Start with [the Persian walkthrough](../../docs/MESSAGING.fa.md), including the comparison with Hangfire.
Then try [the Saga/Compensation exercises](../../docs/SAGA.fa.md): success, declined payment,
failed inventory release and manual compensation retry. State is in-memory; use one worker only.

From the Neo repository root:

```sh
docker compose -f tools/Neo.Companion/samples/MessagingDemo/compose.yaml up -d --wait
dotnet run --project tools/Neo.Companion/samples/MessagingDemo
```

API: http://127.0.0.1:5087. RabbitMQ management: http://127.0.0.1:15672.
Both Compose and appsettings contain public, local-only demo credentials.
The API publishes an `OrderSubmitted` event to two consumer queues. Flags deliberately demonstrate
transient retry and permanent faults. No actual order is stored and no email/payment is performed.
Duplicate suppression is per-process memory only; restarting loses observations. This is not an inbox/outbox implementation.

`Program.cs` registers Neo's real RabbitMQ helper. `OrderConsumers.cs` shows per-consumer definitions.
Use separate `--Role=worker` and `--Role=publisher` processes with different `--urls` ports as documented.
Run the worker first to establish subscriptions. `/observations` is on the worker, `/orders` on the publisher.

With the broker running, build and verify the real transport:

```sh
dotnet build tools/Neo.Companion/samples/MessagingDemo/MessagingDemo.csproj
python tools/Neo.Companion/scripts/smoke_messaging.py --server-dll tools/Neo.Companion/samples/MessagingDemo/bin/Debug/net10.0/MessagingDemo.dll
```

Stop an existing demo process on 5087 before running the smoke test. Use an isolated demo broker/vhost;
the test publishes deliberately failing messages and leaves them in the error queue for inspection.
