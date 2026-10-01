using Shared.Contracts;

namespace Payments.Api.Application;

public record ChargeRequest(Guid OrderId, decimal Amount)
{
    public void Validate()
    {
        if (OrderId == Guid.Empty || !Money.IsValid(Amount))
            throw new ArgumentException("Charge requires an order id and a valid monetary amount");
    }
}
public record ChargeResult(bool Success, PaymentGateway Gateway, string? FailureReason);

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct);
}
