using Shared.Contracts;

namespace Payments.Api.Domain;

public enum PaymentStatus { Approved, Declined }

public class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public PaymentGateway Gateway { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

