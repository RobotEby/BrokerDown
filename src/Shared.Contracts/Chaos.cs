using System.Text.Json.Serialization;

namespace Shared.Contracts;

public enum ChaosFault { Latency, Unavailable }
public record ChaosStart(Guid ExperimentId, ChaosFault Fault, DateTimeOffset ExpiresAt, int LatencyMilliseconds = 2000);
public record ChaosAbort(Guid? ExperimentId, bool KillSwitch = false);

// Zero is not a valid notification: an omitted status must never activate chaos.
[JsonConverter(typeof(WireEnumConverter<ChaosExperimentStatus>))]
public enum ChaosExperimentStatus
{
    Unknown = 0,
    [JsonStringEnumMemberName("started")] Started,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("aborted")] Aborted,
    [JsonStringEnumMemberName("rejected")] Rejected
}

public record ChaosExperimentChanged(Guid ExperimentId, [property: JsonRequired] ChaosExperimentStatus Status,
    DateTimeOffset OccurredAt, string? Reason = null);
