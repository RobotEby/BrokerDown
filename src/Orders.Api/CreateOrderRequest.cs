namespace Orders.Api;

public record CreateOrderRequest(Guid CustomerId, decimal Amount);

