using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal class ApiClient : IDisposable
    {
        public const uint Version = 1620055800; // 2021-05-03 15:30:00

        public const string Token = "@STEAMDB_BUILD_TOKEN@";
        private const string Endpoint = "https://steamdb-token-dumper.xpaw.me";
        private HttpClient HttpClient = new();

        public ApiClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;
            HttpClient.Timeout = TimeSpan.FromMinutes(10);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", $"{nameof(SteamTokenDumper)} v{Version}");
        }

        public void Dispose()
        {
            HttpClient?.Dispose();
            HttpClient = null;
        }

        public async Task SendTokens(Payload payload, Configuration config)
        {
            if (config.DumpPayload)
            {
                Console.WriteLine();

                var payloadDump = new PayloadDump(payload);
                var file = Path.Combine(Program.AppPath, "SteamTokenDumper.payload.json");

                try
                {
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(payloadDump, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }));

                    Console.WriteLine($"Written payload dump to '{Path.GetFileName(file)}'. Modifying this file will not do anything.");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed to write payload dump: {e.Message}");
                }
            }

            if (config.VerifyBeforeSubmit)
            {
                Console.WriteLine();

                // Read any buffered keys so it doesn't auto submit
                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }

                Console.WriteLine("Press any key to continue submission...");
                Console.ReadKey(true);
            }

            Console.WriteLine();
            Console.WriteLine("Submitting tokens to SteamDB...");
            Console.WriteLine();

            var postData = JsonSerializer.Serialize(payload);

            try
            {
                var content = new StringContent(postData, Encoding.UTF8, "application/json");
                var result = await HttpClient.PostAsync($"{Endpoint}/submit", content);
                var output = await result.Content.ReadAsStringAsync();
                output = output.Trim();

                Console.ForegroundColor = result.IsSuccessStatusCode ? ConsoleColor.Blue : ConsoleColor.Red;
                Console.WriteLine(output);
                Console.ResetColor();
                Console.WriteLine();
                
                try
                {
                    output = $"Dump submitted on {DateTime.Now}\nSteamID used: {payload.SteamID}\n\n{output}\n".Replace("\r", "");

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        output = output.Replace("\n", "\r\n");
                    }

                    await File.WriteAllTextAsync(Path.Combine(Program.AppPath, "SteamTokenDumper.result.log"), output);
                }
                catch (Exception)
                {
                    // don't care
                }

                result.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Whoops: {0}", e.Message);
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine();
        }

        public async Task CheckVersion()
        {
            try
            {
                var result = await HttpClient.GetAsync($"{Endpoint}/version");

                result.EnsureSuccessStatusCode();

                var version = await result.Content.ReadAsStringAsync();

                if (!version.StartsWith("version="))
                {
                    throw new InvalidDataException("Failed to get version.");
                }

                var versionInt = uint.Parse(version[8..]);

                if (versionInt != Version)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    await Console.Error.WriteLineAsync("[!] There is a new version of token dumper available.");
                    await Console.Error.WriteLineAsync("[!] Please download the new version before continuing.");
                    await Console.Error.WriteLineAsync();
                    Console.ResetColor();
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                await Console.Error.WriteLineAsync($"[!] Update check failed: {e.Message}");
                await Console.Error.WriteLineAsync("[!] Your submission will most likely fail if you continue with the dumping.");
                await Console.Error.WriteLineAsync();
                Console.ResetColor();
            }
        }
    }
}
