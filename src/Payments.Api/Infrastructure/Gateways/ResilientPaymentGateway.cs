using Shared.Contracts;
using Shared.Infrastructure;
using System.Diagnostics;
using Payments.Api.Application;
using Payments.Api.Domain;
using System.Diagnostics.Metrics;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Payments.Api.Infrastructure.Gateways;

public sealed class ResilienceOptions
{
    public int Retries { get; set; } = 2;
    public int RetryDelayMilliseconds { get; set; } = 200;
    public int TimeoutMilliseconds { get; set; } = 1000;
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 4;
    public int SamplingSeconds { get; set; } = 30;
    public int BreakSeconds { get; set; } = 10;
}

public static class PaymentTelemetry
{
    public static readonly Meter Meter = new("ChaosLab.Payments");
    public static readonly Counter<long> Resilience = Meter.CreateCounter<long>("chaoslab.resilience.events");
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("chaoslab.payments.completed");
}

public sealed class ResilientPaymentGateway : IPaymentGateway
{
    private readonly IPaymentGateway _primary;
    private readonly IPaymentGateway _fallback;
    private readonly Func<ChargeRequest, CancellationToken, Task<ChargeResult?>> _findCharge;
    private readonly ResiliencePipeline<ChargeResult> _primaryPipeline;
    private readonly ResiliencePipeline<ChargeResult> _fallbackPipeline;
    private readonly ILogger<ResilientPaymentGateway> _logger;

    public ResilientPaymentGateway(IPaymentGateway primary, IPaymentGateway fallback,
        Func<ChargeRequest, CancellationToken, Task<ChargeResult?>> findCharge, ResilienceOptions options,
        ILogger<ResilientPaymentGateway> logger, TimeProvider? clock = null)
    {
        (_primary, _fallback, _findCharge, _logger) = (primary, fallback, findCharge, logger);
        _primaryPipeline = Build("primary", options, clock ?? TimeProvider.System);
        _fallbackPipeline = Build("fallback", options, clock ?? TimeProvider.System);
    }

    public async Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("payments.charge");
        activity?.SetTag("order.id", req.OrderId.ToString());
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["OrderId"] = req.OrderId });
        try
        {
            var result = await ChargeCoreAsync(req, ct);
            activity?.SetTag("payment.gateway", result.Gateway.ToWireName()).SetTag("payment.approved", result.Success);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activity?.SetTag("payment.cancelled", true);
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }

    private async Task<ChargeResult> ChargeCoreAsync(ChargeRequest req, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        req.Validate();
        try
        {
            return await _primaryPipeline.ExecuteAsync(token => new ValueTask<ChargeResult>(_primary.ChargeAsync(req, token)), ct);
        }
        catch (Exception ex) when (ex is GatewayUnavailableException or TimeoutRejectedException or BrokenCircuitException)
        {
            ct.ThrowIfCancellationRequested();
            // Reconcile an ambiguous timeout before trying a second gateway with the same key.
            using (var reconciliation = Telemetry.Activities.StartActivity("payments.reconcile"))
            {
                var completed = await _findCharge(req, ct);
                reconciliation?.SetTag("payment.reconciled", completed is not null);
                if (completed is not null) return completed;
            }
            Event("primary", "fallback");
            _logger.LogWarning(ex, "Fallback for order {OrderId}", req.OrderId);
            using var fallback = Telemetry.Activities.StartActivity("payments.fallback");
            fallback?.SetTag("payment.gateway", "fallback");
            return await _fallbackPipeline.ExecuteAsync(token => new ValueTask<ChargeResult>(_fallback.ChargeAsync(req, token)), ct);
        }
    }

    private ResiliencePipeline<ChargeResult> Build(string gateway, ResilienceOptions options, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.Retries);
        var builder = new ResiliencePipelineBuilder<ChargeResult> { TimeProvider = clock };
        if (options.Retries > 0) builder.AddRetry(new RetryStrategyOptions<ChargeResult>
            {
                ShouldHandle = new PredicateBuilder<ChargeResult>().Handle<GatewayUnavailableException>().Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = options.Retries, Delay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds),
                BackoffType = DelayBackoffType.Exponential, UseJitter = true,
                OnRetry = _ => { Event(gateway, "retry"); return default; }
            });
        return builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<ChargeResult>
            {
                ShouldHandle = new PredicateBuilder<ChargeResult>().Handle<GatewayUnavailableException>().Handle<TimeoutRejectedException>(),
                FailureRatio = options.FailureRatio, MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.SamplingSeconds), BreakDuration = TimeSpan.FromSeconds(options.BreakSeconds),
                OnOpened = _ => { Event(gateway, "circuit_open"); return default; },
                OnHalfOpened = _ => { Event(gateway, "circuit_half_open"); return default; },
                OnClosed = _ => { Event(gateway, "circuit_closed"); return default; }
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
                OnTimeout = _ => { Event(gateway, "timeout"); return default; }
            }).Build();
    }

    private void Event(string gateway, string name)
    {
        Activity.Current?.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection { { "payment.gateway", gateway } }));
        PaymentTelemetry.Resilience.Add(1, new("gateway", gateway), new("event", name));
        _logger.LogInformation("Gateway {Gateway} resilience event {Event}", gateway, name);
    }
}
