# Reliability

Orders uses EF Bus Outbox for POST /orders. The business consumer definitions separately enable EF Consumer Outbox/Inbox. Registering outbox tables alone does not enable consumer middleware. The transaction commits business state, inbox state and outgoing messages together; publication happens afterwards. See [MassTransit outbox configuration](https://masstransit.massient.com/configuration/middleware/outbox).

Payment OrderId and SimulatedCharges OrderId are unique in SQL. The inbox suppresses duplicate MessageId within its retention window; durable business keys continue protecting duplicates beyond that window. Competing charge inserts return the committed winner. Reusing a key with another amount is a permanent error. Orders performs one conditional SQL UPDATE where status is Pending.

## Gateway behavior

Each singleton gateway has its own Polly pipeline, outer to inner: Retry → Circuit Breaker → Timeout. Defaults: two retries, exponential jitter from 200ms; timeout 1s per attempt; breaker sample window 30s, four minimum attempts, 50% threshold, 10s break. Callbacks pass Polly's cancellation token to all work, as required by the [timeout strategy](https://www.pollydocs.org/strategies/timeout.html).

Only unavailability and timeout are retried. Those failures and an open circuit can trigger fallback. First query the ledger to reconcile an ambiguous primary completion. Commercial decline is a durable result, not a retryable failure. Original cancellation and permanent validation errors propagate. SQL errors caused by cancellation are normalized to preserve the token semantics.

The ledger uses a separate EF connection and transaction. Its confirmed INSERT **is** the simulated charge; there is no additional financial side effect. Both gateways share it. This guarantee does not extend to independent real providers. Real integration would need provider idempotency, reconciliation of unknown outcomes, and an explicit settlement policy.

Consumer retries are bounded (1s, 3s, 5s) for transient SQL failures and unique-index races. Permanent errors go to the endpoint's _error queue and logs; inspect before intentional replay. SQL unavailability prevents new order persistence; the outbox only decouples broker availability.

Gateway work still holds the consumer transaction open. The lab uses bounded delays and one instance; high-throughput deployment needs separate evaluation. Counters describe observed attempts/transitions and are not an accounting ledger.
