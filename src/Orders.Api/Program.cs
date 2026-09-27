using MassTransit;
using Microsoft.EntityFrameworkCore;
using Orders.Api;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddDbContext<OrdersDb>(o => o.UseSqlServer(cfg.GetConnectionString("Db")));

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<PaymentProcessedConsumer>();

    x.AddEntityFrameworkOutbox<OrdersDb>(o =>
    {
        o.UseSqlServer();
        // Publish only writes an outbox row, committed together with SaveChangesAsync
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((ctx, bus) =>
    {
        bus.Host(cfg["RabbitMq:Host"], cfg.GetValue<ushort>("RabbitMq:Port", 5672), "/", h =>
        {
            h.Username(cfg["RabbitMq:User"]!);
            h.Password(cfg["RabbitMq:Password"]!);
        });
        bus.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();
await InitDbAsync(app.Services);

app.MapGet("/health", () => Results.Ok("ok"));

app.MapPost("/orders", async (CreateOrderRequest req, OrdersDb db, IPublishEndpoint bus, CancellationToken ct) =>
{
    if (req.Amount <= 0) return Results.BadRequest(new { error = "amount deve ser maior que zero" });

    var order = new Order
    {
        Id = Guid.NewGuid(),
        CustomerId = req.CustomerId,
        Amount = req.Amount,
        Status = OrderStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.Orders.Add(order);
    await bus.Publish(new OrderCreated(order.Id, order.CustomerId, order.Amount, order.CreatedAt), ct);
    await db.SaveChangesAsync(ct);

    return Results.Accepted($"/orders/{order.Id}", new { order.Id, Status = order.Status.ToString() });
});

app.MapGet("/orders/{id:guid}", async (Guid id, OrdersDb db, CancellationToken ct) =>
    await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) is { } o
        ? Results.Ok(new { o.Id, o.CustomerId, o.Amount, Status = o.Status.ToString(), o.FailureReason, o.CreatedAt })
        : Results.NotFound());

app.Run();

static async Task InitDbAsync(IServiceProvider sp)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
    for (var i = 1; ; i++)
    {
        try { await db.Database.EnsureCreatedAsync(); return; }
        catch when (i < 15) { await Task.Delay(TimeSpan.FromSeconds(3)); }
    }
}

public partial class Program { }