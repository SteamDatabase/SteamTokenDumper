using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ValveKeyValue;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal static class SteamClientData
    {
        public static void ReadFromSteamClient(Payload payload)
        {
            Console.WriteLine();
            Console.WriteLine("Trying to read tokens from Steam client files");

            var steamLocation = GetSteamPath();

            if (steamLocation == default)
            {
                Console.WriteLine("Did not find Steam client.");
                return;
            }

            Console.WriteLine($"Found Steam at {steamLocation}");

            try
            {
                ReadAppInfo(payload, Path.Join(steamLocation, "appcache", "appinfo.vdf"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to parse appinfo: {e}");
            }

            try
            {
                ReadPackageInfo(payload, Path.Join(steamLocation, "appcache", "packageinfo.vdf"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to parse packageinfo: {e}");
            }

            try
            {
                ReadDepotKeys(payload, Path.Join(steamLocation, "config", "config.vdf"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Failed to parse config: {e}");
            }
        }

        private static void ReadAppInfo(Payload payload, string filename)
        {
            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);
            var magic = reader.ReadUInt32();

            if (magic != 0x07_56_44_27)
            {
                throw new InvalidDataException($"Unknown appinfo.vdf magic: {magic}");
            }

            reader.ReadUInt32(); // universe

            var deserializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

            do
            {
                var appid = reader.ReadUInt32();

                if (appid == 0)
                {
                    break;
                }

                fs.Position += 4 + 4 + 4;

                var token = reader.ReadUInt64();

                if (token > 0)
                {
                    payload.Apps[appid.ToString(CultureInfo.InvariantCulture)] = token.ToString(CultureInfo.InvariantCulture);
                }

                fs.Position += 20 + 4;

                deserializer.Deserialize(fs);
            } while (true);

            Console.WriteLine($"Got {payload.Apps.Count} app tokens from appinfo.vdf");
        }

        private static void ReadPackageInfo(Payload payload, string filename)
        {
            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);
            var magic = reader.ReadUInt32();

            if (magic == 0x06_56_55_27)
            {
                Console.WriteLine("Old Steam client has no package tokens in packageinfo.vdf, skipping");
                return;
            }

            if (magic != 0x06_56_55_28)
            {
                throw new InvalidDataException($"Unknown packageinfo.vdf magic: {magic}");
            }

            reader.ReadUInt32(); // universe

            var deserializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

            do
            {
                var subid = reader.ReadUInt32();

                if (subid == 0xFFFFFFFF)
                {
                    break;
                }

                fs.Position += 20 + 4;

                var token = reader.ReadUInt64();

                if (token > 0)
                {
                    payload.Subs[subid.ToString(CultureInfo.InvariantCulture)] = token.ToString(CultureInfo.InvariantCulture);
                }

                deserializer.Deserialize(fs);
            } while (true);

            Console.WriteLine($"Got {payload.Subs.Count} package tokens from packageinfo.vdf");
        }

        private static void ReadDepotKeys(Payload payload, string filename)
        {
            KVObject data;

            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                data = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(fs);
            }

            // For some inexplicable reason these keys can have different capilizations
            var depots = data.Children
                ?.FirstOrDefault(k => k.Name.Equals("software", StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault(k => k.Name.Equals("valve", StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault(k => k.Name.Equals("steam", StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault(k => k.Name.Equals("depots", StringComparison.OrdinalIgnoreCase));

            if (depots == null)
            {
                throw new InvalidDataException("Failed to find depots section in config.vdf");
            }

            foreach (var depot in depots)
            {
                var depotKey = depot["DecryptionKey"];

                if (depotKey != null)
                {
                    payload.Depots[depot.Name] = depotKey.ToString(CultureInfo.InvariantCulture).ToUpperInvariant();
                }
            }

            Console.WriteLine($"Got {payload.Depots.Count} depot keys from config.vdf");
        }

        private static string GetSteamPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Valve\\Steam") ??
                          RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                              .OpenSubKey("SOFTWARE\\Valve\\Steam");

                if (key?.GetValue("SteamPath") is string steamPath)
                {
                    return steamPath;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var paths = new[] { ".steam", ".steam/steam", ".steam/root", ".local/share/Steam" };

                return paths
                    .Select(path => Path.Join(home, path))
                    .FirstOrDefault(steamPath => Directory.Exists(Path.Join(steamPath, "appcache")));
            }

            return default;
        }
    }
}
