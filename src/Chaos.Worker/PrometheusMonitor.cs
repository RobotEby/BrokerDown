using Chaos.Worker.Domain;
using System.Globalization;
using System.Text.Json;

namespace Chaos.Worker;

public sealed class PrometheusMonitor(HttpClient http, ChaosSafetyPolicy safety, TimeProvider clock)
{
    public const string ReadyQuery = "count(count by (service_name) ((chaoslab_service_ready{service_name=~\"orders-api|payments-api\"} == 1) and (time() - chaoslab_service_heartbeat < 15))) or vector(0)";
    public const string CompletedQuery = "sum(increase(chaoslab_order_duration_seconds_count[1m])) or vector(0)";
    public const string FailedQuery = "(sum(increase(chaoslab_order_duration_seconds_count{status=\"payment_failed\"}[1m])) or vector(0)) + (sum(increase(chaoslab_messaging_errors_total[1m])) or vector(0))";
    public const string LatencyQuery = "histogram_quantile(0.95, sum by (le) (rate(chaoslab_order_duration_seconds_bucket[1m])))";

    public async Task<MetricsAssessment> EvaluateAsync(CancellationToken ct)
    {
        try
        {
            var values = await Task.WhenAll(QueryAsync(ReadyQuery, ct), QueryAsync(CompletedQuery, ct),
                QueryAsync(FailedQuery, ct), QueryAsync(LatencyQuery, ct));
            return safety.Assess(values[0], values[1], values[2], values[3], clock.GetUtcNow());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(false, false, "Prometheus unavailable or invalid response: " + ex.Message, 0, 0, null, clock.GetUtcNow());
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/-/ready", ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<double?> QueryAsync(string query, CancellationToken ct)
    {
        using var response = await http.GetAsync("/api/v1/query?query=" + Uri.EscapeDataString(query), ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "success") throw new InvalidOperationException("Prometheus query failed");
        var results = root.GetProperty("data").GetProperty("result");
        if (results.GetArrayLength() != 1) return null;
        var sample = results[0].GetProperty("value");
        if (sample.GetArrayLength() != 2) return null;
        var text = sample[1].GetString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    }
}
