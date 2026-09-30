using System.Diagnostics;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Polls an async condition instead of sleeping a fixed amount, so tests are
// fast on the happy path and tolerant of real infrastructure timing.
public static class Eventually
{
    public static async Task Until(Func<Task<bool>> condition, TimeSpan? timeout = null,
        TimeSpan? interval = null, string? description = null, CancellationToken ct = default, Func<string>? diagnostic = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        try
        {
            while (true)
            {
                if (await condition().WaitAsync(deadline.Token)) return;
                await Task.Delay(interval ?? TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{description ?? "Condition"} was not met after {watch.Elapsed} (limit {limit}). Last state: {diagnostic?.Invoke() ?? "condition returned false"}");
        }
    }
}
