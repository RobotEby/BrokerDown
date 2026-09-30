# Docker environment

The root Compose file runs six services, binds all published ports to localhost and keeps SQL, RabbitMQ and Prometheus data in named volumes. Runtime images and build SDK are pinned by digest. The applications target net8.0; SDK 10 reads the .slnx solution.

```bash
cp .env.example .env  # first setup only
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 180
docker compose logs -f payments-api
docker compose down  # keeps volumes
```

Do not delete volumes to resolve a migration error. Existing credentials belong to their initialized databases/broker; editing .env does not rotate existing users.

SQL/RabbitMQ readiness precedes Payments. Payments readiness precedes Orders, ensuring the durable subscriber queue exists before publication. RabbitMQ probes run as rabbitmq, avoiding creation of a root-owned Erlang cookie. The hostname remains stable across restarts.

## Separate topology deployment

Before allowing the first publisher on a clean vhost:

```bash
docker compose run --rm --no-deps payments-api --deploy-topology
```

This deploys queues/bindings and exits without consuming messages or initializing SQL. The regular Compose startup already provisions topology by starting Payments first.

## Local execution and troubleshooting

For IDE runs, supply ConnectionStrings__Db and RabbitMq__Password through user-secrets or your shell, plus RabbitMq__Host/Port/VirtualHost as needed. Telemetry__MetricsEndpoint is the full OTLP HTTP URL. Never copy passwords into appsettings.

If the build cannot reach NuGet/Debian over Docker's bridge but host networking works (observed on this WSL environment), Linux users can run:

```bash
BUILD_NETWORK=host docker compose build
docker compose up -d --wait --wait-timeout 180
```

This changes build networking only. The default remains Docker's normal build network. Diagnose port conflicts with docker compose ps; distinguish /health liveness from /health/ready dependency health. The demo restores a stopped broker even on assertion failure.
