using System.Text.Json.Serialization;

namespace Shared.Contracts;

// These fields were strings in v1. Keep their explicit names and reject numeric JSON.
public sealed class WireEnumConverter<T>() : JsonStringEnumConverter<T>(namingPolicy: null, allowIntegerValues: false)
    where T : struct, Enum;
