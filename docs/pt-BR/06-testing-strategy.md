# Estratégia de testes

| Categoria | Escopo |
|---|---|
| Unit | Retry/recusa/timeout/cancelamento/circuito; TTL/abort/Production; segurança/confirmação/cooldown/kill switch |
| Integration | SQL, consumidores EF Inbox/Outbox, endpoints, broker, duplicação/concorrência, migração legada |
| Chaos | Stop/start real do broker e latência/indisponibilidade em Payments via RabbitMQ |

Harnesses usam SQL real e o middleware de produção, com transporte em memória e consumidor de resultados. Testes ponta a ponta usam RabbitMQ real. Cada teste tem banco(s) isolado(s); testes com broker usam vhost próprio. Fixtures compartilham somente processos de containers. Polling usa condições observáveis e prazos.

O teste de atomicidade falha especificamente ao salvar Payment e verifica ausência de pagamento, resultado publicado e mensagem Outbox confirmada. Outros testes preservam a cobrança após rollback/reentrega e verificam MessageIds distintos concorrentes e reconstrução de gateways.

O teste RabbitMQ mantém as mesmas APIs durante a queda, fixa a porta por todo o ciclo do container, verifica 202/Outbox pendente, aguarda readiness real e exige Paid com um pagamento e uma cobrança. Readiness das APIs e convergência compartilham limite de 120s após o broker ficar pronto. O timeout original não foi aumentado.

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

A demonstração completa verifica Prometheus → worker → RabbitMQ → Payments → fallback → recuperação. Todas as categorias executam na CI, sem testes ignorados para deixar o build verde. A CI publica TRX e logs do Compose mesmo em falha. Veja [resultados medidos](10-validation.md).
