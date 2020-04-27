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
        public const uint Version = 10;

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

            var postData = JsonSerializer.Serialize(payload);

            try
            {
                var content = new StringContent(postData, Encoding.UTF8, "application/json");
                var result = await HttpClient.PostAsync("https://steamdb.info/api/SubmitToken/", content);

                Console.WriteLine(await result.Content.ReadAsStringAsync());

                result.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Whoops: {0}", e.Message);
                Console.ResetColor();
            }

            Console.WriteLine();
        }

        public async Task CheckVersion()
        {
            try
            {
                var result = await HttpClient.GetAsync("https://steamdb.info/api/SubmitToken/?versioncheck");

                result.EnsureSuccessStatusCode();

                var version = await result.Content.ReadAsStringAsync();

                if (!version.StartsWith("version="))
                {
                    throw new InvalidDataException("Failed to get version from steamdb.info");
                }

                var versionInt = uint.Parse(version[8..]);

                if (versionInt != Version)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Error.WriteLine("[!] There is a new version of token dumper available.");
                    Console.Error.WriteLine("[!] Please download the new version before dumping.");
                    Console.Error.WriteLine();
                    Console.ResetColor();
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"[!] Update check failed: {e.Message}");
                Console.Error.WriteLine("[!] Your submission may fail if you continue with the dumping.");
                Console.Error.WriteLine();
                Console.ResetColor();
            }
        }
    }
}
