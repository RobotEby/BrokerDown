namespace Shared.Contracts;

public record OrderCreated(Guid OrderId, Guid CustomerId, decimal Amount, DateTimeOffset CreatedAt);

public record PaymentProcessed(
    Guid OrderId, bool Success, string Gateway, string? FailureReason, DateTimeOffset ProcessedAt);
