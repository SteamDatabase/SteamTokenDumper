using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal sealed class ApiClient : IDisposable
{
    public const uint Version = 1716940800; // 2024-05-29

    public const string Token = "@STEAMDB_BUILD_TOKEN@";
    private const string Endpoint = "https://tokendumper.steamdb.info";
    private HttpClient HttpClient = new();

    public ApiClient()
    {
        var appVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

#pragma warning disable CA5386 // Avoid hardcoding SecurityProtocolType value
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls13;
#pragma warning restore CA5386 // Avoid hardcoding SecurityProtocolType value
        HttpClient.DefaultRequestVersion = HttpVersion.Version30;
        HttpClient.Timeout = TimeSpan.FromMinutes(10);
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"{nameof(SteamTokenDumper)} v{Version} ({appVersion})");
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
            var payloadDump = new PayloadDump(payload);
            var file = Path.Combine(Program.AppPath, "SteamTokenDumper.payload.json");

            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(payloadDump, new PayloadDumpJsonContext(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }).PayloadDump);
                await File.WriteAllBytesAsync(file, json);

                AnsiConsole.WriteLine($"Written payload dump to '{Path.GetFileName(file)}'. Modifying this file will not do anything.");
            }
            catch (Exception e)
            {
                AnsiConsole.Write(
                    new Panel(new Text($"Failed to write payload dump: {e}", new Style(Color.Red)))
                        .BorderColor(Color.Red)
                        .RoundedBorder()
                );
            }

            AnsiConsole.WriteLine();
        }

        if (config.VerifyBeforeSubmit)
        {
            // Read any buffered keys so it doesn't auto submit
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("Press any key to continue submission...");
            Console.ReadKey(true);
        }

        var returnValue = false;

        await AnsiConsole.Status()
            .StartAsync("Submitting tokens to SteamDB...", async ctx =>
            {
                var postData = JsonSerializer.Serialize(payload, PayloadJsonContext.Default.Payload);

                try
                {
                    using var content = new StringContent(postData, Encoding.UTF8, "application/json");
                    var result = await HttpClient.PostAsync($"{Endpoint}/submit", content);
                    var output = await result.Content.ReadAsStringAsync();
                    output = output.Trim();

                    if (!result.IsSuccessStatusCode)
                    {
                        AnsiConsole.Write(
                            new Panel(new Text($"Failed to submit tokens to SteamDB, received status code: {(int)result.StatusCode} ({result.ReasonPhrase})", new Style(Color.Red)))
                                .BorderColor(Color.Red)
                                .RoundedBorder()
                        );
                    }

                    var statusCode = (int)result.StatusCode;

                    if (result.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        output = "You got rate limited, please try again later.";
                    }
                    else if (statusCode < 200 || statusCode >= 500)
                    {
                        output = $"Something went wrong (HTTP {statusCode}).";
                    }

                    AnsiConsole.Write(
                        new Panel(new Text(output, new Style(result.IsSuccessStatusCode ? Color.CadetBlue : Color.Red)))
                            .BorderColor(result.IsSuccessStatusCode ? Color.Blue : Color.Red)
                            .RoundedBorder()
                    );

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

                    returnValue = true;
                }
                catch (Exception e)
                {
                    AnsiConsole.Write(
                        new Panel(new Text($"Failed to submit tokens to SteamDB: {e}", new Style(Color.Red)))
                            .BorderColor(Color.Red)
                            .RoundedBorder()
                    );
                }
            });

        return returnValue;
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
                AnsiConsole.Write(
                    new Panel(new Text("There is a new version of the token dumper available.\nPlease download the new version.", new Style(Color.Green)))
                        .BorderColor(Color.GreenYellow)
                        .RoundedBorder()
                );

                return false;
            }
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Update check failed: {e}\n\nYour submission will most likely fail.", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );

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

            return [.. list];
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to get list of depots to skip: {e}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }

        return [];
    }
}
