namespace Chaos.Worker;

public sealed class ChaosOptions
{
    public bool Enabled { get; set; }
    public int CooldownSeconds { get; set; } = 60;
    public int PollSeconds { get; set; } = 5;
    public int WaitSeconds { get; set; } = 60;
    public int AcknowledgementSeconds { get; set; } = 10;
    public int MinimumOrders { get; set; } = 10;
    public double MaximumFailureRatio { get; set; } = 0.05;
    public double MaximumP95Seconds { get; set; } = 10;
}
