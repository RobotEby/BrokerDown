# Ambiente Docker

Compose executa seis serviços, publica portas em localhost e mantém dados SQL, RabbitMQ e Prometheus em volumes nomeados. Imagens e SDK de build têm digest fixado. Aplicações usam net8.0; SDK 10 lê a solução .slnx.

```bash
cp .env.example .env  # somente na primeira execução
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 180
docker compose logs -f payments-api
docker compose down  # preserva volumes
```

Não apague volumes para resolver migrations. Credenciais existentes pertencem ao banco/broker inicializado; editar .env não altera esses usuários.

Readiness de SQL/RabbitMQ precede Payments; readiness de Payments precede Orders, garantindo fila consumidora antes da publicação. As sondas RabbitMQ rodam como rabbitmq para evitar cookie Erlang criado por root. O hostname fica estável após reinícios.

## Implantação separada da topologia

Antes de liberar o primeiro publicador em um vhost limpo:

```bash
docker compose run --rm --no-deps payments-api --deploy-topology
```

O comando provisiona filas/bindings e termina sem consumir mensagens ou inicializar SQL. Compose já provisiona ao iniciar Payments primeiro.

## Execução local e diagnóstico

Para IDE, forneça ConnectionStrings__Db e RabbitMq__Password via user-secrets ou shell, e RabbitMq__Host/Port/VirtualHost conforme necessário. Telemetry__MetricsEndpoint recebe a URL HTTP OTLP completa. Nunca copie senhas para appsettings.

Se a bridge Docker não alcançar NuGet/Debian, mas a rede do host funcionar (observado neste WSL), usuários Linux podem executar:

```bash
BUILD_NETWORK=host docker compose build
docker compose up -d --wait --wait-timeout 180
```

Isso muda apenas a rede de build. O padrão continua sendo a rede normal do Docker. Diagnostique portas com docker compose ps e diferencie /health de /health/ready. O script de demonstração restaura o broker mesmo se uma asserção falhar.
