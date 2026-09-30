using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;
using System.Net;
using System.Net.Sockets;

namespace ChaosLab.IntegrationTests.Infrastructure;

// Starts the one SQL Server and one RabbitMQ container shared by every test
// in the "Infrastructure" collection, mirroring the single-instance-many-
// databases setup described in docs/05-docker-environment.
public sealed class InfrastructureFixture : IAsyncLifetime
{
    public MsSqlContainer Sql { get; } = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090")
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
        .WithPassword("Str0ng!Passw0rd")
        .Build();

    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management@sha256:e582c0bc7766f3342496d8485efb5a1df782b5ce3886ad017e2eaae442311f69")
        .WithPortBinding(AvailablePort(), 5672)
        .WithUsername("chaos")
        .WithPassword("chaos")
        .Build();

    public Task InitializeAsync() =>
        Task.WhenAll(Sql.StartAsync(), Rabbit.StartAsync());

    public Task DisposeAsync() =>
        Task.WhenAll(Sql.DisposeAsync().AsTask(), Rabbit.DisposeAsync().AsTask());

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task<string> CreateVirtualHostAsync()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        await RabbitCommandAsync("rabbitmqctl", "add_vhost", name);
        await RabbitCommandAsync("rabbitmqctl", "set_permissions", "-p", name, "chaos", ".*", ".*", ".*");
        return name;
    }

    public Task DeleteVirtualHostAsync(string name) => RabbitCommandAsync("rabbitmqctl", "delete_vhost", name);

    public async Task WaitForRabbitAsync() => await Eventually.Until(async () =>
        (await Rabbit.ExecAsync(["gosu", "rabbitmq", "rabbitmq-diagnostics", "-q", "check_running"])).ExitCode == 0,
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), "RabbitMQ fully booted");

    private async Task RabbitCommandAsync(params string[] command)
    {
        var result = await Rabbit.ExecAsync(["gosu", "rabbitmq", .. command]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{command[0]}: {result.Stderr}");
    }
}

[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "Infrastructure";
}
