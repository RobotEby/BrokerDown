namespace Shared.Contracts;

public enum ChaosFault { Latency, Unavailable }
public record ChaosStart(Guid ExperimentId, ChaosFault Fault, DateTimeOffset ExpiresAt, int LatencyMilliseconds = 2000);
public record ChaosAbort(Guid? ExperimentId, bool KillSwitch = false);
public record ChaosExperimentChanged(Guid ExperimentId, string Status, DateTimeOffset OccurredAt, string? Reason = null);
