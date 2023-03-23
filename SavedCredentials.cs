using System.Text.Json.Serialization;

namespace SteamTokenDumper;

#nullable enable
internal sealed class SavedCredentials
{
    public const uint CurrentVersion = 1679580480; // 2023-03-24

    public uint Version { get; } = CurrentVersion;
    public string? Username { get; set; }
    public string? RefreshToken { get; set; }
}

[JsonSerializable(typeof(SavedCredentials))]
internal sealed partial class SavedCredentialsJsonContext : JsonSerializerContext
{
}
