using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal sealed class KnownDepotIds 
{
    public readonly HashSet<uint> List = new();
    private readonly string KnownDepotIdsPath = Path.Combine(Program.AppPath, "SteamTokenDumper.depots.txt");

    public async Task Load(ApiClient apiClient)
    {
        await LoadKnownDepotIds();

        var list = await apiClient.GetBackendKnownDepotIds();

        List.UnionWith(list);

        Console.WriteLine($"Got {list.Count} depot ids from the backend to skip.");
    }

    private async Task LoadKnownDepotIds()
    {
        if (!File.Exists(KnownDepotIdsPath))
        {
            return;
        }

        try
        {
            await foreach (var line in File.ReadLinesAsync(KnownDepotIdsPath))
            {
                if (line.Length == 0 || line[0] == ';')
                {
                    continue;
                }

                List.Add(uint.Parse(line, CultureInfo.InvariantCulture));
            }

            Console.WriteLine($"You have sent {List.Count} depot keys before, they will be skipped.");
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"[!] Failed to load known depot ids: {e.Message}");
            Console.ResetColor();
        }
    }

    public async Task SaveKnownDepotIds()
    {
        try
        {
            var data = new StringBuilder();

            data.AppendLine("; This file stores depot ids which you have already sent keys for,");
            data.AppendLine("; so they will not be requested again. Do not modify this file.");
            data.AppendLine("");

            foreach (var depotId in List.OrderBy(x => x))
            {
                data.AppendLine(depotId.ToString(CultureInfo.InvariantCulture));
            }

            await File.WriteAllTextAsync(KnownDepotIdsPath, data.ToString());
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"[!] Failed to save known depot ids: {e.Message}");
            Console.ResetColor();
        }
    }
}
