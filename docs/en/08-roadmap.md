# Roadmap

The current core implements Orders, Payments, resilient simulated gateways, durable charge idempotency, EF Inbox/Outbox, direct OTLP metrics into Prometheus, and request-driven Chaos.Worker.

Optional future work, not dependencies of this demonstration:

- Grafana dashboards and a trace backend.
- Real gateway integration with provider idempotency and unknown-outcome reconciliation.
- Distributed, durable experiment coordination for multiple target/worker instances.
- Throughput evaluation and reduction of time spent holding consumer transactions.
- SQL fault injection with bounded application-level faults.
- YARP, Keycloak/JWT, Catalog, Redis and gRPC if a concrete scenario requires them.

Avoid claiming production readiness or independent-provider exactly-once settlement from this laboratory. The [validation report](10-validation.md) separates measured results from scope limits.
