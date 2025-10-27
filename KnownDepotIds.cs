using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;

namespace SteamTokenDumper;

internal sealed class KnownDepotIds
{
    public readonly HashSet<uint> PreviouslySent = [];
    public ImmutableHashSet<uint> Server = [];
    private readonly string KnownDepotIdsPath = Path.Combine(Application.AppPath, "SteamTokenDumper.depots.txt");

    public async Task Load(ApiClient apiClient)
    {
        await LoadKnownDepotIds();

        Server = await apiClient.GetBackendKnownDepotIds();

        AnsiConsole.WriteLine($"Got {Server.Count} depot ids from the backend to skip.");
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

                PreviouslySent.Add(uint.Parse(line, CultureInfo.InvariantCulture));
            }

            AnsiConsole.WriteLine($"You have sent {PreviouslySent.Count} depot keys before, they will be skipped.");
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to load known depot ids: {e.Message}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
    }

    public async Task SaveKnownDepotIds()
    {
        if (PreviouslySent.Count == 0)
        {
            return;
        }

        try
        {
            var data = new StringBuilder();

            data.AppendLine("; This file stores depot ids which you have already sent keys for,");
            data.AppendLine("; so they will not be requested again. Do not modify this file.");
            data.AppendLine("");

            foreach (var depotId in PreviouslySent.OrderBy(x => x))
            {
                data.AppendLine(depotId.ToString(CultureInfo.InvariantCulture));
            }

            await File.WriteAllTextAsync(KnownDepotIdsPath, data.ToString());
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to save known depot ids: {e.Message}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
    }
}
