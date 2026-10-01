namespace Shared.Contracts;

public record OrderCreated(Guid OrderId, Guid CustomerId, decimal Amount, DateTimeOffset CreatedAt);

public record PaymentProcessed(
    Guid OrderId, bool Success, [property: System.Text.Json.Serialization.JsonRequired] PaymentGateway Gateway, string? FailureReason, DateTimeOffset ProcessedAt);
