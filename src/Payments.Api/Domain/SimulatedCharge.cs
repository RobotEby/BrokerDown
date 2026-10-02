using Shared.Contracts;

namespace Payments.Api.Domain;

public sealed class SimulatedCharge
{
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool Success { get; set; }
    public PaymentGateway Gateway { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

}
