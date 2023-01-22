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
    public string SteamID { get; set; }

    [JsonPropertyName("apps")]
    public Dictionary<string, string> Apps { get; } = new();

    [JsonPropertyName("subs")]
    public Dictionary<string, string> Subs { get; } = new();

    [JsonPropertyName("depots")]
    public Dictionary<string, string> Depots { get; } = new();
}

[JsonSerializable(typeof(Payload))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext
{
}
