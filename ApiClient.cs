using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal sealed class ApiClient : IDisposable
{
    public const uint Version = 1679580840; // 2023-03-24

    public const string Token = "@STEAMDB_BUILD_TOKEN@";
    private const string Endpoint = "https://tokendumper.steamdb.info";
    private HttpClient HttpClient = new();

    public ApiClient()
    {
#pragma warning disable CA5386 // Avoid hardcoding SecurityProtocolType value
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;
#pragma warning restore CA5386 // Avoid hardcoding SecurityProtocolType value
        HttpClient.DefaultRequestVersion = HttpVersion.Version30;
        HttpClient.Timeout = TimeSpan.FromMinutes(10);
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"{nameof(SteamTokenDumper)} v{Version}");
    }

    public void Dispose()
    {
        HttpClient?.Dispose();
        HttpClient = null;
    }

    public async Task<bool> SendTokens(Payload payload, Configuration config)
    {
        if (config.DumpPayload)
        {
            Console.WriteLine();

            var payloadDump = new PayloadDump(payload);
            var file = Path.Combine(Program.AppPath, "SteamTokenDumper.payload.json");

            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(payloadDump, new PayloadDumpJsonContext(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }).PayloadDump);
                await File.WriteAllBytesAsync(file, json);

                Console.WriteLine($"Written payload dump to '{Path.GetFileName(file)}'. Modifying this file will not do anything.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to write payload dump: {e}");
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

        var postData = JsonSerializer.Serialize(payload, PayloadJsonContext.Default.Payload);

        try
        {
            using var content = new StringContent(postData, Encoding.UTF8, "application/json");
            var result = await HttpClient.PostAsync($"{Endpoint}/submit", content);
            var output = await result.Content.ReadAsStringAsync();
            output = output.Trim();

            Console.ForegroundColor = result.IsSuccessStatusCode ? ConsoleColor.Blue : ConsoleColor.Red;
            Console.WriteLine(output);
            Console.ResetColor();
            Console.WriteLine();

            try
            {
                output = $"Dump submitted on {DateTime.Now}\nSteamID used: {payload.SteamID}\n\n{output}\n\n---\n\n".Replace("\r", "", StringComparison.Ordinal);

                if (OperatingSystem.IsWindows())
                {
                    output = output.Replace("\n", "\r\n", StringComparison.Ordinal);
                }

                await File.AppendAllTextAsync(Path.Combine(Program.AppPath, "SteamTokenDumper.result.log"), output);
            }
            catch (Exception)
            {
                // don't care
            }

            result.EnsureSuccessStatusCode();

            Console.WriteLine();
            Console.WriteLine();

            return true;
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Whoops, failed to submit tokens to SteamDB: {e}");
            Console.ResetColor();
        }

        return false;
    }

    public async Task<bool> IsUpToDate()
    {
        try
        {
            var result = await HttpClient.GetAsync($"{Endpoint}/version");

            result.EnsureSuccessStatusCode();

            var version = await result.Content.ReadAsStringAsync();

            if (!version.StartsWith("version=", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Failed to get version.");
            }

            var versionInt = uint.Parse(version[8..], CultureInfo.InvariantCulture);

            if (versionInt != Version)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                await Console.Error.WriteLineAsync("[!] There is a new version of token dumper available.");
                await Console.Error.WriteLineAsync("[!] Please download the new version.");
                await Console.Error.WriteLineAsync();
                Console.ResetColor();

                return false;
            }
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"[!] Update check failed: {e}");
            await Console.Error.WriteLineAsync("[!] Your submission will most likely fail.");
            await Console.Error.WriteLineAsync();
            Console.ResetColor();

            return false;
        }

        return true;
    }

    public async Task<ImmutableHashSet<uint>> GetBackendKnownDepotIds()
    {
        try
        {
            var result = await HttpClient.GetAsync($"{Endpoint}/knowndepots.csv");

            result.EnsureSuccessStatusCode();

            using var reader = new StreamReader(await result.Content.ReadAsStreamAsync());

            var count = await reader.ReadLineAsync();
            var countInt = int.Parse(count, CultureInfo.InvariantCulture);
            var list = new HashSet<uint>(countInt);

            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                list.Add(uint.Parse(line, CultureInfo.InvariantCulture));
            }

            return list.ToImmutableHashSet();
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"[!] Failed to get list of depots to skip: {e}");
            await Console.Error.WriteLineAsync();
            Console.ResetColor();
        }

        return new HashSet<uint>().ToImmutableHashSet();
    }
}
