using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal class ApiClient : IDisposable
    {
        public const uint Version = 12;
        private const string Endpoint = "https://steamdb-token-dumper.xpaw.me/";
        private HttpClient HttpClient = new HttpClient();

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

        public async Task SendTokens(Payload payload)
        {
            Console.WriteLine();
            Console.WriteLine("Submitting tokens to SteamDB...");
            Console.WriteLine();

            var postData = JsonSerializer.Serialize(payload);

            try
            {
                var content = new StringContent(postData, Encoding.UTF8, "application/json");
                var result = await HttpClient.PostAsync($"{Endpoint}/submit", content);
                var output = await result.Content.ReadAsStringAsync();

                Console.ForegroundColor = result.IsSuccessStatusCode ? ConsoleColor.Blue : ConsoleColor.Red;
                Console.WriteLine(output.Trim());
                Console.ResetColor();
                Console.WriteLine();
                
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
