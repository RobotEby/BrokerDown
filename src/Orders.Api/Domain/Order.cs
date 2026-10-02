namespace Orders.Api.Domain;

public enum OrderStatus { Pending, Paid, PaymentFailed }

public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public OrderStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
