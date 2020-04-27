using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamTokenDumper
{
    internal sealed class Payload
    {
        [JsonPropertyName("v")]
        public uint Version { get; } = ApiClient.Version;

        [JsonPropertyName("steamid")]
        public string SteamID { get; set; }

        [JsonPropertyName("apps")]
        public Dictionary<string, string> Apps { get; } = new Dictionary<string, string>();

        [JsonPropertyName("subs")]
        public Dictionary<string, string> Subs { get; } = new Dictionary<string, string>();

        [JsonPropertyName("depots")]
        public Dictionary<string, string> Depots { get; } = new Dictionary<string, string>();
    }
}
