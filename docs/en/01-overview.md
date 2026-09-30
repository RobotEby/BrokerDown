# Overview

ChaosLab.NET demonstrates asynchronous orders, eventual consistency, resilient simulated payments and operator-requested chaos. The client waits only for OrdersDb to commit the order and its outbox event.

The runnable core consists of Orders.Api, Payments.Api, Chaos.Worker, RabbitMQ, SQL Server and Prometheus. Shared.Contracts holds events and monetary validation; Shared.Infrastructure holds startup, health and telemetry helpers.

The demonstrated guarantee is one durable simulated charge per OrderId, even across consumer rollback, competing deliveries and gateway reconstruction. It is not a claim of exactly-once transport delivery or real financial settlement.

Start with the [README](../../README.md), then [architecture](02-architecture.md), [reliability](04-reliability.md), [Docker](05-docker-environment.md), [metrics](09-observability.md) and [validation evidence](10-validation.md).
