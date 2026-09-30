namespace Payments.Api.Gateways;

public record ChargeRequest(Guid OrderId, decimal Amount);
public record ChargeResult(bool Success, string Gateway, string? FailureReason);

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct);
}

public class SimulatedPrimaryGateway(IConfiguration cfg) : IPaymentGateway
{
    public async Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(100, 300), ct);

        var failureRate = cfg.GetValue<double>("Gateway:FailureRate");
        return Random.Shared.NextDouble() < failureRate
            ? new ChargeResult(false, "primary", "Recusado pelo gateway simulado")
            : new ChargeResult(true, "primary", null);
    }
}
