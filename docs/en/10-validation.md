# Validation report

## 1. Final state

The core is implemented. This document does not record build or test counts; run the commands in [Run again](#7-run-again) or check the CI run for current results. The integration tests include Chaos-category cases.

The lab runs as six Compose services. The Prometheus → worker → RabbitMQ → injected fault → fallback → recovery flow is exercised by `scripts/demo.py` and by the integration tests.

Environment: all applications target net8.0. SDK 10 feature bands are accepted by global.json.

## 2. Problems found and corrected

| Cause | Impact | Correction |
|---|---|---|
| Docker reassigned an ephemeral RabbitMQ port on restart | Existing API hosts kept connecting to the old port | Bind an explicit available port for the container lifetime and assert it stays unchanged |
| Consumer EF middleware missing | PaymentProcessed could escape before a failed commit | Consumer definitions enable EF Outbox/Inbox; rollback assertions observe actual result delivery |
| Shared test topology | Late Payments passed only when another test had provisioned its queue | Isolated vhosts and explicit topology deployment before publication |
| Gateway effect outside consumer transaction | Replay after rollback could charge again | Independent durable SimulatedCharges ledger keyed by OrderId; reconcile before fallback |
| Concurrent terminal updates | A late result could overwrite order state | Conditional SQL update only while Pending |
| SqlClient cancellation surfaced as DbUpdateException | Polly did not recognize its timeout; an order entered the error queue | Normalize provider cancellation with the original token; regression tests distinguish timeout from caller cancellation |
| Root-owned RabbitMQ cookie | Broker startup failed with eacces | Run probes as rabbitmq; existing ownership corrected; fresh-volume startup verified |
| Nonexistent SDK image tag; bridge network could not reach package feeds | Docker build failed | Pin a published SDK digest; documented BUILD_NETWORK=host workaround for this WSL host |
| Demo used Request.method on an inferred POST | Expected kill-switch HTTP 409 caused a script exception | Use get_method() and assert the numeric HTTP status |

The SQL-cancellation failure was retained in logs, deliberately replayed after the fix, and recovered with one payment and one charge. The final complete demo subsequently passed.

## 3. Principal files

- src/Orders.Api/Program.cs and PaymentProcessedConsumer.cs: input validation, readiness, metrics, atomic terminal transition.
- Business consumer definitions and integration harnesses: transactional inbox/outbox and bounded SQL retries.
- src/Payments.Api/Gateways/: independent charge ledger and per-gateway Polly pipelines.
- src/Payments.Api/Migrations/ and PaymentsDatabase.cs: fresh schema.
- src/Payments.Api/Chaos/, src/Chaos.Worker/, Shared.Contracts/Chaos.cs: command/ACK/TTL/control and metric safety gate.
- src/Shared.Infrastructure/: startup, health, metrics and message diagnostics.
- docker-compose.yml, Dockerfile, infra/prometheus.yml, .env.example: runnable environment with persistent volumes.
- tests/, scripts/demo.py, .github/workflows/ci.yml, README files and docs/: executable checks and reproducible guidance.

## 4. Final architecture and boundaries

Orders persists order + Bus Outbox and returns 202. RabbitMQ delivers to Payments with EF Inbox/Outbox. Primary/fallback gateways share an independently committed charge ledger. Payments commits its result, then Orders applies the first terminal transition. OpenTelemetry exports directly to Prometheus; the worker executes at most one explicitly requested experiment after safety checks and target acknowledgement.

This is at-least-once messaging with durable business idempotency. Shared simulated storage is essential to the demonstrated fallback guarantee. Independent real providers, distributed worker coordination and production throughput are not proven by these tests. Experiment state is process-local; local target TTL is the final protection during communication loss.

## 5. RabbitMQ outage validation

The integration tests stop and restart the broker while the same Orders and Payments hosts keep running. They verify a fixed port, a 202 response during the outage, a pending outbox row, reconnection, the Paid status, and exactly one payment and one charge. The 120s budget includes API readiness after the broker becomes ready. Timings are not recorded here because they vary by machine.

## 6. Executed validation

```bash
dotnet tool restore
dotnet restore ChaosLab.NET.slnx
dotnet build ChaosLab.NET.slnx --no-restore -c Release
dotnet test ChaosLab.NET.slnx --no-build -c Release --logger 'trx;LogFilePrefix=verified' --results-directory artifacts
dotnet test tests/Chaos.UnitTests -c Release --logger 'trx;LogFileName=chaos-unit.trx' --results-directory artifacts
dotnet test tests/ChaosLab.IntegrationTests --no-build -c Release --filter FullyQualifiedName~Order_PaymentsApiStartsLate
dotnet test tests/ChaosLab.IntegrationTests --no-build -c Release --filter FullyQualifiedName~Order_BrokerUnavailable_StillAcceptedAndDeliveredOnceBrokerReturns
docker compose config --quiet
BUILD_NETWORK=host docker compose build
docker compose run --rm --no-deps payments-api --deploy-topology
docker compose up -d --wait --wait-timeout 180
python3 scripts/demo.py --scenario all --output artifacts/demo.json
```

| Final demo scenario | Outcome |
|---|---|
| Normal | Orders are paid through the primary gateway |
| Unavailable | Fallback for every order; acknowledged abort |
| Latency | Fallback for every order; local TTL expiry |
| RabbitMQ outage | 202/Pending, then Paid once the broker returns |
| Full run including warmup/recovery traffic | Unique orders, payments and charges; kill switch rejects further execution |

Local evidence is in the ignored artifacts/ directory: build.log, payments-unit.trx, chaos-unit.trx, verified_*.trx, rabbit-1/2/3.trx, late-payments.trx, test-summary.json, demo.json, demo-final.log, prometheus-evidence.json, restart-before/after.log, database-final.log and compose.log. CI is configured to upload TRX/diagnostic logs and Compose evidence. A remote GitHub Actions run has not been observed in this session.

## 7. Run again

Follow the [README](../../README.md) for a fresh environment. Preserve an existing .env. This machine required the documented host-network build option. The demo ends with kill switch; restart payments-api and chaos-worker to reset it, then respect the startup cooldown. No experiment starts automatically.

## 8. Remaining scope

**Blockers:** none known for the local lab. Remote CI execution is not claimed as completed.

**Future improvements:** Grafana/traces, real-provider reconciliation, multi-instance coordination, throughput work, and the optional components in the [roadmap](08-roadmap.md).
