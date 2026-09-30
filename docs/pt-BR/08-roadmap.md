# Roadmap

O núcleo atual implementa Orders, Payments, gateways simulados resilientes, idempotência durável, EF Inbox/Outbox, OTLP direto ao Prometheus e Chaos.Worker por solicitação.

Evoluções opcionais, sem dependência para esta demonstração:

- Dashboards Grafana e backend de traces.
- Gateway real com idempotência e reconciliação de resultados desconhecidos.
- Coordenação durável e distribuída de experimentos para múltiplas instâncias.
- Avaliação de vazão e redução do tempo de transações dos consumidores.
- Falhas SQL controladas no nível da aplicação.
- YARP, Keycloak/JWT, Catalog, Redis e gRPC quando houver um cenário concreto.

O laboratório não comprova prontidão de produção nem liquidação exatamente uma vez entre provedores independentes. O [relatório de validação](10-validation.md) separa medições de limites de escopo.
