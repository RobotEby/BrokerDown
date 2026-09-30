# Observabilidade

OpenTelemetry 1.18.0 exporta métricas cumulativas a cada 5s por OTLP/HTTP diretamente ao Prometheus 3.5.0. Compose habilita seu receiver nativo. Não há Collector, Grafana ou backend de traces. Referências: [exporters OTel](https://opentelemetry.io/docs/languages/dotnet/exporters/) e [OTLP no Prometheus](https://prometheus.io/docs/guides/opentelemetry/).

Telemetry__MetricsEndpoint aponta para http://prometheus:9090/api/v1/otlp/v1/metrics. O receiver promove service.name para service_name. IDs ficam somente nos logs; labels limitados incluem status, gateway, event e fault.

| Métrica Prometheus | Significado |
|---|---|
| chaoslab_orders_accepted_total | Pedidos aceitos após persistência |
| chaoslab_order_duration_seconds_* | Histograma ponta a ponta por estado terminal |
| chaoslab_payments_completed_total | Resultados por gateway/status |
| chaoslab_resilience_events_total | retry, timeout, circuit_open/half_open/closed, fallback |
| chaoslab_messaging_errors_total | Falhas de consumo por tipo de mensagem |
| chaoslab_service_ready / chaoslab_service_heartbeat | Readiness e horário da última exportação |
| chaoslab_chaos_active | Efeito local ativo |
| chaoslab_chaos_experiments_total / chaoslab_chaos_duration_seconds_* | Transições e duração no alvo |
| http_server_request_duration_seconds_* | Latência/contagem HTTP por rota/status |

Consultas em http://localhost:9090:

```promql
sum(increase(chaoslab_order_duration_seconds_count[1m]))
histogram_quantile(0.95, sum by (le) (rate(chaoslab_order_duration_seconds_bucket[1m])))
sum(increase(chaoslab_order_duration_seconds_count{status="payment_failed"}[1m]))
sum by (gateway, event) (increase(chaoslab_resilience_events_total[5m]))
sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"4..|5.."}[1m]))
time() - chaoslab_service_heartbeat
```

Worker exige sinais recentes e saudáveis de Orders e Payments. Usa janela de um minuto, mínimo dez pedidos concluídos para iniciar, falhas ≤5% e p95 ≤10s. Telemetria ausente, antiga ou inválida bloqueia experimentos. Falhas do gateway/fallback não contam por si como falha final. Falhas de consumidores contribuem conservadoramente para a proteção.

Após reiniciar, aguarde novas exportações e tráfego antes de esperar avaliação segura. increase/rate extrapolam amostras; métricas não são contabilidade financeira. A garantia de cobrança vem das restrições SQL e do registro durável.
