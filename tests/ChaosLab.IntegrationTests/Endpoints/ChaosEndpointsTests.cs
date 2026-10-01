extern alias ChaosWorker;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ChaosLab.IntegrationTests.Infrastructure;
using ChaosWorker::Chaos.Worker;
using ChaosWorker::Chaos.Worker.Domain;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ChaosLab.IntegrationTests.Endpoints;

[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ChaosEndpointsTests(InfrastructureFixture infra)
{
    [Fact]
    public async Task EveryAdministrativeRouteRequiresTheKey_AndDeniedRequestsHaveNoEffects()
    {
        var vhost = await infra.CreateVirtualHostAsync();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await using var factory = new ChaosApiFactory(infra, vhost, key);
            using var client = factory.CreateClient();
            var coordinator = factory.Services.GetRequiredService<ChaosCoordinator>();
            var observer = new Publications();
            using var subscription = factory.Services.GetRequiredService<IBus>().ConnectPublishObserver(observer);
            var initial = coordinator.Snapshot();
            foreach (var path in new[] { "/chaos", "/chaos/experiments", "/chaos/abort", "/chaos/kill-switch" })
            {
                foreach (var credential in new string?[] { null, "incorrect-key" })
                {
                    using var request = new HttpRequestMessage(path == "/chaos" ? HttpMethod.Get : HttpMethod.Post, path);
                    if (credential is not null) request.Headers.Add("X-Chaos-Api-Key", credential);
                    if (path == "/chaos/experiments") request.Content = JsonContent.Create(new { fault = "Latency" });
                    using var denied = await client.SendAsync(request);
                    denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                    coordinator.Snapshot().ShouldBe(initial);
                    observer.Messages.ShouldBeEmpty();
                }
            }
            (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await client.GetAsync("/chaos?apiKey=" + key)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("X-Chaos-Api-Key", key);
            (await client.GetAsync("/chaos")).StatusCode.ShouldBe(HttpStatusCode.OK);
            using var created = await client.PostAsJsonAsync("/chaos/experiments", new { fault = "Latency" });
            created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await created.Content.ReadAsStringAsync()).ShouldContain("\"status\":\"waiting\"");
            (await client.PostAsync("/chaos/abort", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            coordinator.Current!.Status.ShouldBe(ExperimentStatus.Aborted);
            (await client.PostAsync("/chaos/kill-switch", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            coordinator.Snapshot().KillSwitch.ShouldBeTrue();
            (await client.PostAsJsonAsync("/chaos/experiments", new { fault = "Unavailable" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
            observer.Messages.Count.ShouldBe(2);
        }
        finally { await infra.DeleteVirtualHostAsync(vhost); }
    }

    [Fact]
    public async Task MissingKeyFailsStartup_AndProductionStillDisablesExperiments()
    {
        await using (var invalid = new ChaosApiFactory(infra, "/", null))
            Should.Throw<InvalidOperationException>(() => invalid.CreateClient()).Message.ShouldContain("Chaos:AdminApiKey");
        var vhost = await infra.CreateVirtualHostAsync();
        try
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var factory = new ChaosApiFactory(infra, vhost, key, "Production");
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Chaos-Api-Key", key);
            (await client.PostAsJsonAsync("/chaos/experiments", new { fault = "Latency" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally { await infra.DeleteVirtualHostAsync(vhost); }
    }

    private sealed class Publications : IPublishObserver
    {
        public ConcurrentQueue<Type> Messages { get; } = new();
        public Task PrePublish<T>(PublishContext<T> context) where T : class => Task.CompletedTask;
        public Task PostPublish<T>(PublishContext<T> context) where T : class { Messages.Enqueue(typeof(T)); return Task.CompletedTask; }
        public Task PublishFault<T>(PublishContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }
}
