# Relatório de validação

## 1. Estado final

Núcleo implementado. Este documento não registra contagens de build ou de testes; execute os comandos de [Executar novamente](#7-executar-novamente) ou consulte a execução da CI para os resultados atuais. Os testes de integração incluem casos da categoria Chaos.

O laboratório roda como seis serviços do Compose. O fluxo Prometheus → worker → RabbitMQ → falha → fallback → recuperação é exercitado por `scripts/demo.py` e pelos testes de integração.

Ambiente: as aplicações usam net8.0. global.json aceita versões compatíveis do SDK 10.

## 2. Problemas encontrados e corrigidos

| Causa | Impacto | Correção |
|---|---|---|
| Docker mudava a porta efêmera do RabbitMQ após reiniciar | APIs existentes continuavam usando a porta antiga | Porta disponível fixada pelo ciclo do container, com asserção de estabilidade |
| Middleware EF dos consumidores ausente | PaymentProcessed podia sair antes da falha no commit | Definições habilitam Outbox/Inbox; rollback verifica entrega real do resultado |
| Topologia compartilhada nos testes | Payments tardio passava por resíduo de outro teste | Vhost por teste e implantação explícita da topologia |
| Efeito do gateway fora da transação do consumidor | Reentrega após rollback podia cobrar novamente | SimulatedCharges independente e durável por OrderId; consulta antes do fallback |
| Atualizações terminais concorrentes | Resultado tardio podia sobrescrever o pedido | UPDATE condicionado a Pending |
| Cancelamento SqlClient exposto como DbUpdateException | Polly não reconhecia timeout; pedido ia para _error | Normalização com o token recebido; regressão distingue timeout de cancelamento original |
| Cookie RabbitMQ pertencente a root | Inicialização falhava com eacces | Sondas como rabbitmq; permissão existente corrigida e volume novo verificado |
| Tag de SDK inexistente; bridge sem acesso aos feeds | Build Docker falhava | Digest de SDK disponível e opção documentada BUILD_NETWORK=host neste WSL |
| Demo acessava Request.method em POST inferido | HTTP 409 esperado do kill switch causava exceção no script | get_method() e asserção pelo código HTTP |

A falha de cancelamento SQL foi mantida nos logs e reprocessada deliberadamente após a correção: recuperou com um pagamento e uma cobrança. Depois, a demonstração completa passou.

## 3. Arquivos principais

- src/Orders.Api/Program.cs e PaymentProcessedConsumer.cs: validação, readiness, métricas e transição terminal atômica.
- Definições dos consumidores e harnesses: Inbox/Outbox transacional e retries SQL limitados.
- src/Payments.Api/Gateways/: registro independente e pipelines Polly.
- src/Payments.Api/Migrations/ e PaymentsDatabase.cs: banco novo.
- src/Payments.Api/Chaos/, src/Chaos.Worker/ e Shared.Contracts/Chaos.cs: comandos, confirmação, TTL, controle e avaliação de segurança.
- src/Shared.Infrastructure/: inicialização, saúde, métricas e diagnóstico de consumo.
- docker-compose.yml, Dockerfile, infra/prometheus.yml e .env.example: ambiente executável com volumes persistentes.
- tests/, scripts/demo.py, .github/workflows/ci.yml, READMEs e docs/: verificações executáveis e instruções reproduzíveis.

## 4. Arquitetura final e limites

Orders confirma pedido + Bus Outbox e retorna 202. RabbitMQ entrega a Payments com EF Inbox/Outbox. Principal/contingencial compartilham registro de cobrança confirmado independentemente. Payments confirma o resultado; Orders aplica a primeira transição terminal. OpenTelemetry exporta diretamente ao Prometheus; o worker executa no máximo um experimento explicitamente solicitado, após métricas seguras e confirmação do alvo.

A entrega de mensagens é pelo menos uma vez, com idempotência durável de negócio. O registro simulado compartilhado é essencial à garantia demonstrada de fallback. Provedores reais independentes, coordenação distribuída e capacidade de produção não foram comprovados. Experimentos têm estado no processo; o TTL local protege durante perda de comunicação.

## 5. Validação da queda do RabbitMQ

Os testes de integração param e reiniciam o broker enquanto as mesmas APIs Orders e Payments continuam em execução. Eles verificam porta fixa, resposta 202 durante a queda, Outbox pendente, reconexão, status Paid e exatamente um pagamento e uma cobrança. O limite de 120s inclui a readiness das APIs após o broker ficar pronto. Tempos não são registrados aqui porque variam por máquina.

## 6. Validações executadas

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

| Cenário do demo final | Resultado |
|---|---|
| Normal | Pedidos pagos pelo gateway principal |
| Indisponibilidade | Fallback em todos os pedidos; aborto confirmado |
| Latência | Fallback em todos os pedidos; expiração TTL |
| Queda RabbitMQ | 202/Pending → Paid quando o broker volta |
| Execução completa, incluindo aquecimento/recuperação | Pedidos, pagamentos e cobranças únicos; kill switch rejeita nova execução |

As evidências locais ficam em artifacts/ (ignorado pelo Git): build.log, payments-unit.trx, chaos-unit.trx, verified_*.trx, rabbit-1/2/3.trx, late-payments.trx, test-summary.json, demo.json, demo-final.log, prometheus-evidence.json, restart-before/after.log, database-final.log e compose.log. A CI publica TRX/logs diagnósticos e evidências Compose. Não foi observada uma execução remota do GitHub Actions nesta sessão.

## 7. Executar novamente

Siga o [README](../../README.pt-BR.md) para ambiente novo. Preserve um .env existente. Esta máquina exigiu a opção documentada de rede do host no build. O demo termina com kill switch; reinicie payments-api e chaos-worker para limpar o bloqueio, respeitando o cooldown inicial. Nenhum experimento começa automaticamente.

## 8. Pendências

**Bloqueador:** nenhum conhecido para o laboratório local. Execução remota da CI não é apresentada como concluída.

**Melhoria futura:** Grafana/traces, reconciliação com provedores reais, coordenação de múltiplas instâncias, avaliação de vazão e componentes opcionais do [roadmap](08-roadmap.md).
