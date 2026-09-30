# Observability

OpenTelemetry 1.18.0 exports cumulative metrics every 5s over OTLP/HTTP directly to Prometheus 3.5.0. Compose enables its native OTLP receiver. No Collector, Grafana or trace backend is installed. See [OTel exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/) and [Prometheus OTLP support](https://prometheus.io/docs/guides/opentelemetry/).

Telemetry__MetricsEndpoint is http://prometheus:9090/api/v1/otlp/v1/metrics in Compose. The receiver promotes service.name to service_name. IDs appear in structured logs only; bounded labels include status, gateway, event and fault.

| Prometheus metric | Meaning |
|---|---|
| chaoslab_orders_accepted_total | Orders accepted after persistence |
| chaoslab_order_duration_seconds_* | End-to-end order completion histogram, by terminal status |
| chaoslab_payments_completed_total | Payment consumer results, by gateway/status |
| chaoslab_resilience_events_total | retry, timeout, circuit_open/half_open/closed, fallback |
| chaoslab_messaging_errors_total | Consumer faults by message type |
| chaoslab_service_ready / chaoslab_service_heartbeat | Readiness and last export time |
| chaoslab_chaos_active | Local target effect active |
| chaoslab_chaos_experiments_total / chaoslab_chaos_duration_seconds_* | Experiment transitions and target duration |
| http_server_request_duration_seconds_* | ASP.NET request latency/count by route/status |

Example PromQL at http://localhost:9090:

```promql
sum(increase(chaoslab_order_duration_seconds_count[1m]))
histogram_quantile(0.95, sum by (le) (rate(chaoslab_order_duration_seconds_bucket[1m])))
sum(increase(chaoslab_order_duration_seconds_count{status="payment_failed"}[1m]))
sum by (gateway, event) (increase(chaoslab_resilience_events_total[5m]))
sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"4..|5.."}[1m]))
time() - chaoslab_service_heartbeat
```

The worker requires fresh ready signals for Orders and Payments. It checks a one-minute histogram/count window, at least ten completed orders to start, failure ratio ≤5%, p95 ≤10s. Missing, stale or invalid telemetry fails closed. Gateway failure/fallback counters are diagnostic and do not themselves count as failed orders. Consumer faults conservatively contribute to the error guard.

After a restart, allow fresh exports and a traffic window before expecting a safe assessment. Prometheus increase/rate extrapolate sampled data; metrics are not financial accounting. SQL constraints and the durable ledger provide the charge guarantee.
