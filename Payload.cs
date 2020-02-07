using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamTokenDumper
{
    internal sealed class Payload
    {
        [JsonPropertyName("v")]
        public uint Version { get; } = 10;

        [JsonPropertyName("steamid")]
        public ulong SteamID { get; set; }

        [JsonPropertyName("apps")]
        public Dictionary<uint, string> Apps { get; } = new Dictionary<uint, string>();

        [JsonPropertyName("depots")]
        public Dictionary<uint, string> Depots { get; } = new Dictionary<uint, string>();
    }
}
