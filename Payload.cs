using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamTokenDumper;

internal sealed class Payload
{
    [JsonPropertyName("v")]
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty
    public uint Version { get; } = ApiClient.Version;

    [JsonPropertyName("token")]
    // ReSharper disable once ReplaceAutoPropertyWithComputedProperty
    public string Token { get; } = ApiClient.Token;

    [JsonPropertyName("steamid")]
    public string SteamID { get; set; } = string.Empty;

    [JsonPropertyName("apps")]
    public Dictionary<string, string> Apps { get; } = [];

    [JsonPropertyName("subs")]
    public Dictionary<string, string> Subs { get; } = [];

    [JsonPropertyName("depots")]
    public Dictionary<string, string> Depots { get; } = [];
}

[JsonSerializable(typeof(Payload))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext
{
}
