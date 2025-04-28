using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamTokenDumper;

internal sealed class StoreUserData
{
    [JsonPropertyName("rgOwnedPackages")]
    public List<uint> OwnedPackages { get; set; } = [];
}

[JsonSerializable(typeof(StoreUserData))]
internal sealed partial class StoreUserDataJsonContext : JsonSerializerContext
{
}
