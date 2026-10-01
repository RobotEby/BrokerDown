using Orders.Api.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Orders.Api;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args.Where(a => a is not "--deploy-topology").ToArray());
var cfg = builder.Configuration;
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddLabTelemetry(cfg, "orders-api");
cfg.ApplyDeployTopologyFlag(args);

builder.Services.AddDbContext<OrdersDb>(o => o.UseSqlServer(cfg.GetConnectionString("Db")));

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumeObserver<MessageFaultTelemetry>();
    x.AddConsumer<PaymentProcessedConsumer, PaymentProcessedConsumerDefinition>();

    x.AddEntityFrameworkOutbox<OrdersDb>(o =>
    {
        o.UseSqlServer();
        // Publish only writes an outbox row, committed together with SaveChangesAsync
        o.UseBusOutbox();
    });

    x.AddLabRabbitMq(cfg);
});

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck<OrdersDb>>("database");
await using var app = builder.Build();
if (await app.DeployTopologyIfRequestedAsync(args)) return;
await Database.InitializeAsync<OrdersDb>(app.Services, app.Lifetime.ApplicationStopping);

app.MapGet("/health", () => Results.Ok("ok"));
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = 503 } });

app.MapPost("/orders", async (CreateOrderRequest req, OrdersDb db, IPublishEndpoint bus, CancellationToken ct) =>
{
    if (req.CustomerId == Guid.Empty || !Money.IsValid(req.Amount))
        return Results.BadRequest(new { error = "customerId must be nonempty and amount positive, within decimal(18,2), with at most two decimal places" });

    var order = new Order
    {
        Id = Guid.NewGuid(),
        CustomerId = req.CustomerId,
        Amount = req.Amount,
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.Orders.Add(order);
    System.Diagnostics.Activity.Current?.SetTag("order.id", order.Id.ToString());
    await bus.Publish(new OrderCreated(order.Id, order.CustomerId, order.Amount, order.CreatedAt),
        context => context.CorrelationId = order.Id, ct);
    await db.SaveChangesAsync(ct);
    OrdersTelemetry.Accepted.Add(1);

    return Results.Accepted($"/orders/{order.Id}", new { order.Id, Status = order.Status.ToString() });
});

app.MapGet("/orders/{id:guid}", async (Guid id, OrdersDb db, CancellationToken ct) =>
    await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) is { } o
        ? Results.Ok(new { o.Id, o.CustomerId, o.Amount, Status = o.Status.ToString(), o.FailureReason, o.CreatedAt })
        : Results.NotFound());

app.Run();

public partial class Program { }
