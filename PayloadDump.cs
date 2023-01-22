using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SteamTokenDumper;

internal sealed class PayloadDump
{
    [JsonPropertyName("Apps")]
    public ImmutableSortedDictionary<string, string> Apps { get; }

    [JsonPropertyName("Packages")]
    public ImmutableSortedDictionary<string, string> Packages { get; }

    [JsonPropertyName("Depots")]
    public ImmutableSortedDictionary<string, string> Depots { get; }

    public PayloadDump(Payload payload)
    {
        Apps = payload.Apps.ToImmutableSortedDictionary();
        Packages = payload.Subs.ToImmutableSortedDictionary();
        Depots = payload.Depots.ToImmutableSortedDictionary();
    }
}

[JsonSerializable(typeof(PayloadDump))]
internal sealed partial class PayloadDumpJsonContext : JsonSerializerContext
{
}
