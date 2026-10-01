using System.Text.Json.Serialization;

namespace Shared.Contracts;

[JsonConverter(typeof(WireEnumConverter<PaymentGateway>))]
public enum PaymentGateway
{
    Unknown = 0,
    [JsonStringEnumMemberName("primary")] Primary,
    [JsonStringEnumMemberName("fallback")] Fallback
}

public static class PaymentGatewayNames
{
    public static string ToWireName(this PaymentGateway gateway) => gateway switch
    {
        PaymentGateway.Primary => "primary",
        PaymentGateway.Fallback => "fallback",
        _ => throw new ArgumentException("Unknown payment gateway", nameof(gateway))
    };

    public static PaymentGateway Parse(string value) => value switch
    {
        "primary" => PaymentGateway.Primary,
        "fallback" => PaymentGateway.Fallback,
        _ => throw new ArgumentException("Unknown payment gateway", nameof(value))
    };
}
