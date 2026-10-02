namespace Chaos.Worker.Domain;

public sealed class ChaosSafetyPolicy(ChaosOptions options)
{
    public bool IsEnabled(string environment) => options.Enabled && !string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

    public MetricsAssessment Assess(double? ready, double? completed, double? failed, double? p95, DateTimeOffset observedAt)
    {
        var count = completed ?? 0;
        var ratio = (failed ?? 0) / Math.Max(count, 1);
        var healthy = ready == 2 && Valid(completed) && Valid(failed) && Valid(p95) &&
            double.IsFinite(ratio) && ratio <= options.MaximumFailureRatio && p95 <= options.MaximumP95Seconds;
        var enough = Valid(completed) && count >= options.MinimumOrders;
        return new(healthy, enough, !healthy ? "Readiness, freshness, errors or latency outside safety limits" :
            !enough ? "Waiting for recent completed orders" : "Safe to run", count, ratio, p95, observedAt);
    }

    private static bool Valid(double? value) => value is { } number && double.IsFinite(number) && number >= 0;
}
