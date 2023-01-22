using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamTokenDumper;

internal sealed class Configuration
{
    public bool RememberLogin { get; private set; }
    public bool SkipAutoGrant { get; private set; }
    public bool VerifyBeforeSubmit { get; private set; } = true;
    public bool UserConsentBeforeRun { get; private set; } = true;
    public bool DumpPayload { get; private set; }
    public bool Debug { get; private set; }
    public HashSet<uint> SkipApps { get; } = new();

    public async Task Load()
    {
        var path = Path.Combine(Program.AppPath, "SteamTokenDumper.config.ini");
        var lines = await File.ReadAllLinesAsync(path);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] == ';')
            {
                continue;
            }

            var option = line.Split('=', 2, StringSplitOptions.TrimEntries);

            if (option.Length != 2)
            {
                continue;
            }

            switch (option[0])
            {
                case "RememberLogin":
                    RememberLogin = option[1] == "1";
                    break;
                case "SkipAutoGrant":
                    SkipAutoGrant = option[1] == "1";
                    break;
                case "UserConsentBeforeRun":
                    UserConsentBeforeRun = option[1] == "1";
                    break;
                case "VerifyBeforeSubmit":
                    VerifyBeforeSubmit = option[1] == "1";
                    break;
                case "DumpPayload":
                    DumpPayload = option[1] == "1";
                    break;
                case "Debug":
                    Debug = option[1] == "1";
                    break;
                case "SkipAppIds":
                    var ids = option[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    foreach (var id in ids)
                    {
                        if (!uint.TryParse(id, CultureInfo.InvariantCulture, out var appid))
                        {
                            await Console.Error.WriteLineAsync($"Id '{id}' in 'SkipAppIds' is not a positive integer.");
                            continue;
                        }

                        SkipApps.Add(appid);
                    }

                    break;
                default:
                    await Console.Error.WriteLineAsync($"Unknown option '{option[0]}'");
                    break;
            }
        }

        if (RememberLogin)
        {
            Console.WriteLine("Will remember your login.");
        }

        if (SkipAutoGrant)
        {
            Console.WriteLine("Will skip auto granted packages.");
        }

        if (DumpPayload)
        {
            Console.WriteLine("Will dump payload.");
        }

        if (VerifyBeforeSubmit)
        {
            Console.WriteLine("Will ask for confirmation before sending results.");
        }

        if (SkipApps.Any())
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Will skip these appids: {string.Join(", ", SkipApps)}");
            Console.ResetColor();
        }
    }
}
