using System;
using System.IO;
using Microsoft.Win32;
using ValveKeyValue;

namespace SteamTokenDumper
{
    internal static class SteamClientData
    {
        public static void ReadFromSteamClient(Payload payload)
        {
            string steamLocation = null;

            var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Valve\\Steam") ??
                      RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SOFTWARE\\Valve\\Steam");

            if (key != null && key.GetValue("SteamPath") is string steamPath)
            {
                steamLocation = steamPath;
            }

            if (steamLocation == null)
            {
                return;
            }

            ReadAppInfo(payload, Path.Join(steamLocation, "appcache", "appinfo.vdf"));
            ReadPackageInfo(payload, Path.Join(steamLocation, "appcache", "packageinfo.vdf"));
        }

        private static void ReadAppInfo(Payload payload, string filename)
        {
            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            if (reader.ReadUInt32() != 0x07_56_44_27)
            {
                throw new InvalidDataException("Unknown appinfo.vdf magic");
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
                    payload.Apps[appid.ToString()] = token.ToString();
                }

                fs.Position += 20 + 4;

                deserializer.Deserialize(fs);
            } while (true);
        }

        private static void ReadPackageInfo(Payload payload, string filename)
        {
            using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            if (reader.ReadUInt32() != 0x06_56_55_28)
            {
                throw new InvalidDataException("Unknown packageinfo.vdf magic");
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
                    payload.Subs[subid.ToString()] = token.ToString();
                }

                deserializer.Deserialize(fs);
            } while (true);
        }
    }
}
