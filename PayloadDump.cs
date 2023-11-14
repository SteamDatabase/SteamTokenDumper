using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SteamTokenDumper;

internal sealed class PayloadDump(Payload payload)
{
    [JsonPropertyName("Apps")]
    public ImmutableSortedDictionary<string, string> Apps { get; } = payload.Apps.ToImmutableSortedDictionary();

    [JsonPropertyName("Packages")]
    public ImmutableSortedDictionary<string, string> Packages { get; } = payload.Subs.ToImmutableSortedDictionary();

    [JsonPropertyName("Depots")]
    public ImmutableSortedDictionary<string, string> Depots { get; } = payload.Depots.ToImmutableSortedDictionary();
}

[JsonSerializable(typeof(PayloadDump))]
internal sealed partial class PayloadDumpJsonContext : JsonSerializerContext
{
}
