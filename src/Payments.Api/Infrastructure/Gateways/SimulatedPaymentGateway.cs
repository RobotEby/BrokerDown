using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Application;
using Shared.Contracts;

namespace Payments.Api.Infrastructure.Gateways;

public class SimulatedPaymentGateway(ChargeLedger ledger, IConfiguration cfg, PaymentGateway name,
    Func<CancellationToken, Task>? injectFault = null) : IPaymentGateway
{
    public async Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct)
    {
        try
        {
            if (await ledger.FindAsync(req, ct) is { } existing) return existing;
            if (injectFault is not null) await injectFault(ct);
            await Task.Delay(TimeSpan.FromMilliseconds(cfg.GetValue("Gateway:LatencyMilliseconds", 100)), ct);
            var failureRate = name == PaymentGateway.Primary ? cfg.GetValue<double>("Gateway:FailureRate") : 0;
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
    Func<CancellationToken, Task>? injectFault = null) : SimulatedPaymentGateway(ledger, cfg, PaymentGateway.Primary, injectFault);

public sealed class SimulatedFallbackGateway(ChargeLedger ledger, IConfiguration cfg)
    : SimulatedPaymentGateway(ledger, cfg, PaymentGateway.Fallback);

public sealed class GatewayUnavailableException(string message = "Simulated gateway unavailable") : Exception(message);
