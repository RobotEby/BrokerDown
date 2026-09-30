# Visão geral

ChaosLab.NET demonstra pedidos assíncronos, consistência eventual, pagamentos simulados resilientes e caos solicitado pelo operador. O cliente espera apenas a confirmação do pedido e do evento Outbox em OrdersDb.

O núcleo executável contém Orders.Api, Payments.Api, Chaos.Worker, RabbitMQ, SQL Server e Prometheus. Shared.Contracts contém eventos e validação monetária; Shared.Infrastructure contém inicialização, saúde e telemetria.

A garantia demonstrada é uma cobrança simulada durável por OrderId, inclusive após rollback do consumidor, entregas concorrentes e reconstrução dos gateways. Isso não significa entrega de mensagens exatamente uma vez nem liquidação financeira real.

Comece pelo [README](../../README.pt-BR.md), depois [arquitetura](02-architecture.md), [confiabilidade](04-reliability.md), [Docker](05-docker-environment.md), [métricas](09-observability.md) e [evidências](10-validation.md).
