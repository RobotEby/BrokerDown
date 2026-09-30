# Testing strategy

| Category | Scope |
|---|---|
| Unit | Polly retry/decline/timeout/cancellation/circuit, target TTL/abort/Production, worker safety/ACK/cooldown/kill switch |
| Integration | SQL persistence, consumers with EF Inbox/Outbox, endpoints, broker delivery, duplicates/concurrency |
| Chaos | Real broker stop/start and real Payments latency/unavailability via RabbitMQ |

Consumer harnesses use real SQL and the same business middleware as production, with an in-memory transport and a result probe. End-to-end tests use real RabbitMQ. Every test gets isolated database(s); broker tests also get a unique vhost. Collection fixtures share only container processes. Polling uses observable conditions and deadlines, not fixed settling sleeps.

The atomicity test fails specifically while saving a Payment and checks no committed payment, result message or outgoing outbox row. Persistence tests also preserve a charge across consumer rollback and replay, check concurrent distinct MessageIds and gateway reconstruction.

The broker test keeps the same API hosts alive through the outage, fixes the host port for the container lifetime, asserts 202 and pending outbox, waits for actual broker readiness, and requires Paid plus one payment/charge. Readiness and convergence share a 120s budget after broker readiness. It does not increase the original timeout.

```bash
dotnet restore ChaosLab.NET.slnx
dotnet build ChaosLab.NET.slnx --no-restore -c Release
dotnet test ChaosLab.NET.slnx --no-build -c Release --logger trx --results-directory artifacts
dotnet test ChaosLab.NET.slnx --no-build -c Release --filter Category=Unit
dotnet test tests/ChaosLab.IntegrationTests --no-build -c Release --filter 'Category!=Chaos'
dotnet test tests/ChaosLab.IntegrationTests --no-build -c Release --filter Category=Chaos
dotnet test tests/ChaosLab.IntegrationTests --no-build -c Release --filter FullyQualifiedName~Order_PaymentsApiStartsLate
python3 scripts/demo.py --scenario all --output artifacts/demo.json
```

The full demo checks the real Prometheus → worker → RabbitMQ → Payments → fallback → recovery path. All categories run in CI; nothing is skipped to make a build green. CI uploads TRX and Compose logs even on failure. See [measured results](10-validation.md).
