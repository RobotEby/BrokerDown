using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Payments.Api.Gateways;

public record ChargeRequest(Guid OrderId, decimal Amount);
public record ChargeResult(bool Success, string Gateway, string? FailureReason);

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct);
}

public class SimulatedPaymentGateway(ChargeLedger ledger, IConfiguration cfg, string name,
    Func<CancellationToken, Task>? injectFault = null) : IPaymentGateway
{
    public async Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct)
    {
        try
        {
            if (await ledger.FindAsync(req, ct) is { } existing) return existing;
            if (injectFault is not null) await injectFault(ct);
            await Task.Delay(TimeSpan.FromMilliseconds(cfg.GetValue("Gateway:LatencyMilliseconds", 100)), ct);
            var failureRate = name == "primary" ? cfg.GetValue<double>("Gateway:FailureRate") : 0;
            var result = Random.Shared.NextDouble() < failureRate
                ? new ChargeResult(false, name, "Declined by simulated gateway")
                : new ChargeResult(true, name, null);
            return await ledger.RecordAsync(req, result, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is SqlException or DbUpdateException)
        {
            // SqlClient can report cancellation as a provider error. Preserve Polly's token semantics.
            throw new OperationCanceledException("Simulated gateway database operation cancelled", ex, ct);
        }
    }
}

public sealed class SimulatedPrimaryGateway(ChargeLedger ledger, IConfiguration cfg,
    Func<CancellationToken, Task>? injectFault = null) : SimulatedPaymentGateway(ledger, cfg, "primary", injectFault);

public sealed class SimulatedFallbackGateway(ChargeLedger ledger, IConfiguration cfg)
    : SimulatedPaymentGateway(ledger, cfg, "fallback");

public sealed class GatewayUnavailableException(string message = "Simulated gateway unavailable") : Exception(message);
