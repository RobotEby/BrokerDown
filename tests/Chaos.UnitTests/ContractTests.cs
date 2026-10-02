using System.Text.Json;
using System.Text.Json.Nodes;
using Chaos.Worker.Domain;
using MassTransit;
using MassTransit.Serialization;
using Shared.Contracts;
using Shouldly;
using Xunit;

namespace Chaos.UnitTests;

[Trait("Category", "Unit")]
public sealed class ContractTests
{
    private static readonly JsonSerializerOptions Bus = SystemTextJsonMessageSerializer.Options;
    private const string Id = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    [Fact]
    public void HistoricalEventsRoundTripWithTheSameValuesAndIdentities()
    {
        var payment = $$"""{"orderId":"{{Id}}","success":true,"gateway":"primary","failureReason":null,"processedAt":"2026-09-28T12:00:00+00:00"}""";
        var parsed = JsonSerializer.Deserialize<PaymentProcessed>(payment, Bus)!;
        parsed.Gateway.ShouldBe(PaymentGateway.Primary);
        JsonNode.DeepEquals(JsonNode.Parse(payment), JsonNode.Parse(JsonSerializer.Serialize(parsed, Bus))).ShouldBeTrue();
        var notification = $$"""{"experimentId":"{{Id}}","status":"started","occurredAt":"2026-09-28T12:00:00+00:00","reason":null}""";
        var changed = JsonSerializer.Deserialize<ChaosExperimentChanged>(notification, Bus)!;
        changed.Status.ShouldBe(ChaosExperimentStatus.Started);
        JsonNode.DeepEquals(JsonNode.Parse(notification), JsonNode.Parse(JsonSerializer.Serialize(changed, Bus))).ShouldBeTrue();
        MessageUrn.ForTypeString<OrderCreated>().ShouldBe("urn:message:Shared.Contracts:OrderCreated");
        MessageUrn.ForTypeString<PaymentProcessed>().ShouldBe("urn:message:Shared.Contracts:PaymentProcessed");
        MessageUrn.ForTypeString<ChaosStart>().ShouldBe("urn:message:Shared.Contracts:ChaosStart");
        MessageUrn.ForTypeString<ChaosAbort>().ShouldBe("urn:message:Shared.Contracts:ChaosAbort");
        MessageUrn.ForTypeString<ChaosExperimentChanged>().ShouldBe("urn:message:Shared.Contracts:ChaosExperimentChanged");
    }

    [Fact]
    public void WorkerStatesUseHistoricalStrings_AndChaosFaultRemainsNumericOnTheBus()
    {
        JsonSerializer.Serialize(ExperimentStatus.AbortRequested, Bus).ShouldBe("\"abort_requested\"");
        JsonSerializer.Serialize(ExperimentStatus.Waiting, Bus).ShouldBe("\"waiting\"");
        var start = new ChaosStart(Guid.Parse(Id), ChaosFault.Unavailable, DateTimeOffset.UtcNow.AddSeconds(30));
        JsonNode.Parse(JsonSerializer.Serialize(start, Bus))!["fault"]!.GetValue<int>().ShouldBe(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("99")]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"typo\"")]
    public void InvalidWireStatesAreNotCoercedIntoAnActiveState(string status)
    {
        var body = $$"""{"experimentId":"{{Id}}","status":{{status}},"occurredAt":"2026-09-28T12:00:00Z"}""";
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ChaosExperimentChanged>(body, Bus));
    }

    [Fact]
    public void MissingRequiredFieldsCannotUseEnumDefaults()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ChaosExperimentChanged>("{}", Bus));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<PaymentProcessed>("{}", Bus));
    }
}
