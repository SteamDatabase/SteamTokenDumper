using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamTokenDumper
{
    internal sealed class PayloadDump
    {
        [JsonPropertyName("Apps")]
        public Dictionary<string, string> Apps { get; }

        [JsonPropertyName("Packages")]
        public Dictionary<string, string> Packages { get; }

        [JsonPropertyName("Depots")]
        public Dictionary<string, string> Depots { get; }

        public PayloadDump(Payload payload)
        {
            Apps = payload.Apps;
            Packages = payload.Subs;
            Depots = payload.Depots;
        }
    }
}
