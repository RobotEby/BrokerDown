using System.Diagnostics.Metrics;

namespace Shared.Infrastructure;

public static class ChaosTelemetry
{
    public static readonly Meter Meter = new("ChaosLab.Chaos");
    public static readonly Counter<long> Experiments = Meter.CreateCounter<long>("chaoslab.chaos.experiments");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("chaoslab.chaos.duration", "s");
}
