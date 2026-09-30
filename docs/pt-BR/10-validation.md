# Relatório de validação — 30/09/2026

## 1. Estado final

Núcleo implementado e validado localmente. Restore e build Release passaram com **zero warnings/erros**. Conjunto final: **54 testes passando — 14 unitários de Payments, 8 do Worker e 32 de integração; nenhum ignorado**. Integração inclui três casos da categoria Chaos. A execução completa de integração levou aproximadamente 2min23s de testes (2min42s incluindo inicialização de host/fixtures).

Os seis serviços do Compose ficaram saudáveis. O fluxo real Prometheus → worker → RabbitMQ → falha → fallback → recuperação passou. O último demo verificou **162 pagamentos e 162 cobranças únicas**. O snapshot SQL final contém 412 pedidos Paid, 412 pagamentos e 412 cobranças únicas, somando as demonstrações locais. Não há bloqueadores conhecidos para executar e demonstrar o projeto.

Ambiente: Linux x86-64 / WSL2 kernel 6.18.33.2, Docker 29.7.1, Compose 5.3.1, SDK local 10.0.112 e SDK Docker fixado 10.0.401. Aplicações permanecem net8.0. global.json aceita versões compatíveis do SDK 10.

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

## 5. Medições RabbitMQ

Três execuções isoladas consecutivas mantiveram as mesmas APIs e verificaram porta fixa, 202 durante queda, Outbox pendente, reconexão, Paid e exatamente um pagamento/cobrança:

| Execução | Porta antes/depois | Broker pronto → Paid | Queda/recuperação inteira |
|---|---:|---:|---:|
| 1 | 33867 | 6,660s | 21,574s |
| 2 | 34787 | 6,488s | 20,210s |
| 3 | 43539 | 18,098s | 39,084s |

O limite de 120s foi preservado e inclui readiness das APIs após o broker ficar pronto. Uma execução posterior da suíte mediu 6,045s pronto→Paid. A falha original era mudança de endpoint, sem necessidade de aumentar para 240s.

O demo Compose final mediu 16,53s do comando de reinício até Paid; esse valor inclui a inicialização do broker e difere da medição baseada em readiness do teste.

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

O comando isolado RabbitMQ foi repetido três vezes. A última verificação de amostra Prometheus malformada foi adicionada após a execução da solução completa e validada reexecutando os oito unitários do Worker; os 54 testes combinam as últimas suítes verificadas de cada projeto.

| Cenário do demo final | Resultado medido |
|---|---|
| Normal | 20 pedidos, principal, 3,21s |
| Indisponibilidade | 20/20 fallback; aborto confirmado; 18,67s incluindo recuperação |
| Latência | 20/20 fallback; expiração TTL; 33,76s incluindo recuperação |
| Queda RabbitMQ | 202/Pending → Paid, 16,53s após reinício |
| Execução completa, incluindo aquecimento/recuperação | 162 pedidos/pagamentos/cobranças únicos; kill switch rejeita nova execução |

Também foram verificados: migrations em banco novo; reinício de processos/SQL/broker preservando os 250 registros existentes naquele momento; RabbitMQ com volume novo saudável e cookie de rabbitmq; Payments tardio isolado em cerca de 4s, sem contar fixture. Prometheus retornou séries reais, sem IDs de negócio como labels.

As evidências locais ficam em artifacts/ (ignorado pelo Git): build.log, payments-unit.trx, chaos-unit.trx, verified_*.trx, rabbit-1/2/3.trx, late-payments.trx, test-summary.json, demo.json, demo-final.log, prometheus-evidence.json, restart-before/after.log, database-final.log e compose.log. A CI publica TRX/logs diagnósticos e evidências Compose. Não foi observada uma execução remota do GitHub Actions nesta sessão.

## 7. Executar novamente

Siga o [README](../../README.pt-BR.md) para ambiente novo. Preserve um .env existente. Esta máquina exigiu a opção documentada de rede do host no build. O demo termina com kill switch; reinicie payments-api e chaos-worker para limpar o bloqueio, respeitando o cooldown inicial. Nenhum experimento começa automaticamente.

## 8. Pendências

**Bloqueador:** nenhum conhecido para o laboratório local. Execução remota da CI não é apresentada como concluída.

**Melhoria futura:** Grafana/traces, reconciliação com provedores reais, coordenação de múltiplas instâncias, avaliação de vazão e componentes opcionais do [roadmap](08-roadmap.md).
