using System.Text.Json.Serialization;
using Shared.Contracts;

namespace Chaos.Worker.Domain;

[JsonConverter(typeof(WireEnumConverter<ExperimentStatus>))]
public enum ExperimentStatus
{
    Unknown = 0,
    [JsonStringEnumMemberName("waiting")] Waiting,
    [JsonStringEnumMemberName("starting")] Starting,
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("abort_requested")] AbortRequested,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("aborted")] Aborted,
    [JsonStringEnumMemberName("rejected")] Rejected
}

public record ExperimentRequest(ChaosFault Fault, int DurationSeconds = 30, int LatencyMilliseconds = 2000);
public record ExperimentRun(Guid ExperimentId, ChaosFault Fault, int DurationSeconds, int LatencyMilliseconds,
    ExperimentStatus Status, DateTimeOffset RequestedAt, DateTimeOffset? DispatchedAt = null,
    DateTimeOffset? ExpiresAt = null, string? Reason = null);
public record ExperimentSnapshot(bool Enabled, bool KillSwitch, DateTimeOffset CooldownUntil,
    ExperimentRun? Current, MetricsAssessment? Metrics);
public record MetricsAssessment(bool Healthy, bool EnoughTraffic, string Reason,
    double Completed, double FailureRatio, double? P95Seconds, DateTimeOffset ObservedAt);

public static class ExperimentValidator
{
    public static void Validate(ExperimentRequest request)
    {
        if (!Enum.IsDefined(request.Fault) || request.DurationSeconds is < 1 or > 60 || request.LatencyMilliseconds is < 1 or > 5000)
            throw new ArgumentException("Fault, duration (1–60s), or latency (1–5000ms) is invalid");
    }

    public static void Validate(ChaosOptions options)
    {
        if (options.PollSeconds < 1 || options.CooldownSeconds < 0 || options.WaitSeconds < 1 || options.AcknowledgementSeconds < 1 ||
            options.MinimumOrders < 1 || !double.IsFinite(options.MaximumFailureRatio) || options.MaximumFailureRatio is < 0 or > 1 ||
            !double.IsFinite(options.MaximumP95Seconds) || options.MaximumP95Seconds <= 0)
            throw new InvalidOperationException("Invalid Chaos safety settings");
    }
}
