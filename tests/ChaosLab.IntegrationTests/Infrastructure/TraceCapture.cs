using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChaosLab.IntegrationTests.Infrastructure;

public sealed class TraceCapture : IDisposable
{
    private readonly ActivityListener _listener;
    public ConcurrentQueue<Activity> Completed { get; } = new();

    public TraceCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "ChaosLab" or "MassTransit" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Completed.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();
}
