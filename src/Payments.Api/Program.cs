using MassTransit;
using Microsoft.EntityFrameworkCore;
using Payments.Api;
using Payments.Api.Gateways;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddDbContext<PaymentsDb>(o => o.UseSqlServer(cfg.GetConnectionString("Db")));
builder.Services.AddSingleton<IPaymentGateway, SimulatedPrimaryGateway>();

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<OrderCreatedConsumer>();

    x.AddEntityFrameworkOutbox<PaymentsDb>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
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

app.MapGet("/payments/{orderId:guid}", async (Guid orderId, PaymentsDb db, CancellationToken ct) =>
    await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, ct) is { } p
        ? Results.Ok(new { p.OrderId, p.Amount, Status = p.Status.ToString(), p.Gateway, p.FailureReason, p.CreatedAt })
        : Results.NotFound());

app.Run();

static async Task InitDbAsync(IServiceProvider sp)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDb>();
    for (var i = 1; ; i++)
    {
        try { await db.Database.EnsureCreatedAsync(); return; }
        catch when (i < 15) { await Task.Delay(TimeSpan.FromSeconds(3)); }
    }
}

public partial class Program { }