# Validation report — 2026-09-30

## 1. Final state

The core is implemented and validated locally. Restore and Release build pass with **zero warnings/errors**. The final test set is **54 passing: 14 Payments unit tests, 8 Worker unit tests, 32 integration tests; none skipped**. Integration includes three Chaos-category cases. The complete integration run took approximately 2m23s of test execution (2m42s including test-host/fixture startup).

All six Compose services were healthy. The real Prometheus → worker → RabbitMQ → injected fault → fallback → recovery flow passed. The final demo verified **162 payments and 162 unique charges**. The final SQL snapshot contains 412 Paid orders, 412 payments and 412 unique charges across all local demonstration runs. No known blockers remain for running and demonstrating this lab.

Environment: Linux x86-64 / WSL2 kernel 6.18.33.2, Docker 29.7.1, Compose 5.3.1, local SDK 10.0.112, pinned Docker SDK 10.0.401. All applications target net8.0. SDK 10 feature bands are accepted by global.json.

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
- src/Payments.Api/Migrations/ and PaymentsDatabase.cs: fresh schema and explicit legacy adoption.
- src/Payments.Api/Chaos/, src/Chaos.Worker/, Shared.Contracts/Chaos.cs: command/ACK/TTL/control and metric safety gate.
- src/Shared.Infrastructure/: startup, health, metrics and message diagnostics.
- docker-compose.yml, Dockerfile, infra/prometheus.yml, .env.example: runnable environment with persistent volumes.
- tests/, scripts/demo.py, .github/workflows/ci.yml, README files and docs/: executable checks and reproducible guidance.

## 4. Final architecture and boundaries

Orders persists order + Bus Outbox and returns 202. RabbitMQ delivers to Payments with EF Inbox/Outbox. Primary/fallback gateways share an independently committed charge ledger. Payments commits its result, then Orders applies the first terminal transition. OpenTelemetry exports directly to Prometheus; the worker executes at most one explicitly requested experiment after safety checks and target acknowledgement.

This is at-least-once messaging with durable business idempotency. Shared simulated storage is essential to the demonstrated fallback guarantee. Independent real providers, distributed worker coordination and production throughput are not proven by these tests. Experiment state is process-local; local target TTL is the final protection during communication loss.

## 5. RabbitMQ measurements

Three consecutive isolated runs retained the same Orders/Payments hosts and verified fixed port, 202 during outage, pending outbox, reconnection, Paid, and exactly one payment/charge:

| Run | Port before/after | Broker ready → Paid | Whole outage/recovery |
|---|---:|---:|---:|
| 1 | 33867 | 6.660s | 21.574s |
| 2 | 34787 | 6.488s | 20.210s |
| 3 | 43539 | 18.098s | 39.084s |

The 120s budget was preserved, including API readiness after broker readiness. A later full-suite run measured 6.045s ready→Paid. The original failure was a changed endpoint, not a need for a 240s timeout.

The final Compose demo measured 16.53s from the restart command to Paid; that measurement includes broker startup and differs from the test's readiness-based measurement.

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
docker compose run --rm --no-deps payments-api --adopt-legacy-database
docker compose run --rm --no-deps payments-api --deploy-topology
docker compose up -d --wait --wait-timeout 180
python3 scripts/demo.py --scenario all --output artifacts/demo.json
```

The isolated broker command was repeated three times. The last Worker malformed-sample check was added after the full-solution run and validated by rerunning all eight Worker unit tests; the 54 total combines the latest validated project suites.

| Final demo scenario | Measured outcome |
|---|---|
| Normal | 20 orders, primary, 3.21s |
| Unavailable | 20/20 fallback; acknowledged abort; 18.67s including recovery |
| Latency | 20/20 fallback; local TTL expiry; 33.76s including recovery |
| RabbitMQ outage | 202/Pending then Paid, 16.53s after restart command |
| Full run including warmup/recovery traffic | 162 unique orders/payments/charges; kill switch rejects further execution |

Additional evidence: fresh SQL migrations, explicit legacy adoption and rejection of unknown schema; legacy test preserves an existing payment; real backup before local adoption; process/SQL/broker restart preserved all 250 rows present at that point; fresh RabbitMQ volume became healthy with rabbitmq-owned cookie; late Payments passed alone in approximately 4s excluding fixture startup. Prometheus queries returned real series without business IDs as labels.

Local evidence is in the ignored artifacts/ directory: build.log, payments-unit.trx, chaos-unit.trx, verified_*.trx, rabbit-1/2/3.trx, late-payments.trx, test-summary.json, demo.json, demo-final.log, prometheus-evidence.json, restart-before/after.log, database-final.log and compose.log. CI is configured to upload TRX/diagnostic logs and Compose evidence. A remote GitHub Actions run has not been observed in this session.

## 7. Run again

Follow the [README](../../README.md) for a fresh environment and the [legacy procedure](05-docker-environment.md#legacy-payments-database) for an old database. Preserve an existing .env. This machine required the documented host-network build option. The demo ends with kill switch; restart payments-api and chaos-worker to reset it, then respect the startup cooldown. No experiment starts automatically.

## 8. Remaining scope

**Blockers:** none known for the local lab. Remote CI execution is not claimed as completed.

**Future improvements:** Grafana/traces, real-provider reconciliation, multi-instance coordination, throughput work, and the optional components in the [roadmap](08-roadmap.md).
