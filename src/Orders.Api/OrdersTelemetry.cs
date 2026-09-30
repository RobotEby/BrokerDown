using System.Diagnostics.Metrics;

namespace Orders.Api;

public static class OrdersTelemetry
{
    public static readonly Meter Meter = new("ChaosLab.Orders");
    public static readonly Counter<long> Accepted = Meter.CreateCounter<long>("chaoslab.orders.accepted");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("chaoslab.order.duration", "s");
}
