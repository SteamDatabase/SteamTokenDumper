using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using SteamKit2;
using static SteamKit2.SteamApps;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal sealed class Requester(Payload payload, SteamApps steamApps, KnownDepotIds knownDepotIds, Configuration config)
{
    private const int ItemsPerRequest = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private bool SomeRequestFailed;
    private readonly HashSet<uint> skippedPackages = [];
    private readonly HashSet<uint> skippedApps = [];

    public List<PICSRequest> ProcessLicenseList(LicenseListCallback licenseList)
    {
        var packages = new List<PICSRequest>();

        foreach (var license in licenseList.LicenseList)
        {
            packages.Add(new PICSRequest(license.PackageID, license.AccessToken));

            // Request autogrant packages so we can automatically skip all apps inside of it
            if (config.SkipAutoGrant && license.PaymentMethod == EPaymentMethod.AutoGrant)
            {
                skippedPackages.Add(license.PackageID);
                continue;
            }

            if (license.AccessToken == 0)
            {
                continue;
            }

            payload.Subs[license.PackageID.ToString(CultureInfo.InvariantCulture)] = license.AccessToken.ToString(CultureInfo.InvariantCulture);
        }

        if (skippedPackages.Count > 0)
        {
            AnsiConsole.MarkupLine($"Skipped auto granted packages: [yellow]{string.Join(", ", skippedPackages.Order())}[/]");
        }

        return packages;
    }

    public async Task ProcessPackages(List<PICSRequest> packages)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Licenses: [green]{packages.Count}[/] [gray]({packages.Count(x => x.AccessToken != 0)} of them have a token)[/]");

        try
        {
            await AnsiConsole.Progress()
                .Columns([
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new IntValueProgressColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                ])
                .StartAsync(async ctx =>
                {
                    var progressPackages = ctx.AddTask("Package info", maxValue: packages.Count);
                    var progressTokens = ctx.AddTask("App tokens", autoStart: false, maxValue: 0);
                    var progressApps = ctx.AddTask("App info", autoStart: false, maxValue: 0);
                    var progressDepots = ctx.AddTask("Depot keys", autoStart: false, maxValue: 0);

                    var (apps, depots) = await RequestPackageInfo(progressPackages, packages);
                    await Request(progressApps, progressTokens, progressDepots, apps, depots);
                });

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Sub tokens: [green]{payload.Subs.Count}[/]");
            AnsiConsole.MarkupLine($"App tokens: [green]{payload.Apps.Count}[/]");
            AnsiConsole.MarkupLine($"Depot keys: [green]{payload.Depots.Count}[/]");
            AnsiConsole.WriteLine();
        }
        catch (Exception e)
        {
            SomeRequestFailed = true;

            AnsiConsole.Write(
                new Panel(new Text(e.ToString(), new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }

        if (SomeRequestFailed)
        {
            AnsiConsole.Write(
                new Panel(new Text("Some of the requests to Steam failed, which may have resulted in some of the tokens or depot keys not being fetched.\nYou can try running the dumper again later.", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
    }

    private async Task<(HashSet<uint> Apps, HashSet<uint> Depots)> RequestPackageInfo(ProgressTask progress, List<PICSRequest> subInfoRequests)
    {
        var apps = new HashSet<uint>();
        var depots = new HashSet<uint>();

        foreach (var chunk in subInfoRequests.Chunk(ItemsPerRequest))
        {
            AsyncJobMultiple<PICSProductInfoCallback>.ResultSet info = null;

            for (var retry = 3; retry > 0; retry--)
            {
                try
                {
                    var infoTask = steamApps.PICSGetProductInfo([], chunk);
                    infoTask.Timeout = Timeout;
                    info = await infoTask;
                    break;
                }
                catch (Exception e)
                {
                    AnsiConsole.WriteLine($"Package info task failed: {e.GetType()} {e.Message}");

                    await AwaitReconnectIfDisconnected();
                }
            }

            if (info == null)
            {
                SomeRequestFailed = true;
                continue;
            }

            if (info.Results == null)
            {
                continue;
            }

            foreach (var result in info.Results)
            {
                foreach (var package in result.Packages.Values)
                {
                    var skipAutoGrant = skippedPackages.Contains(package.ID);

                    foreach (var id in package.KeyValues["appids"].Children)
                    {
                        var appid = id.AsUnsignedInteger();

                        if (skipAutoGrant)
                        {
                            skippedApps.Add(appid);
                            continue;
                        }

                        if (config.SkipApps.Contains(appid))
                        {
                            skippedApps.Add(appid);
                            continue;
                        }

                        apps.Add(appid);
                    }

                    foreach (var id in package.KeyValues["depotids"].Children)
                    {
                        var depotid = id.AsUnsignedInteger();

                        depots.Add(depotid);
                    }
                }
            }

            progress.Value += chunk.Length;
        }

        progress.StopTask();

        foreach (var appid in config.SkipApps)
        {
            if (payload.Apps.Remove(appid.ToString(CultureInfo.InvariantCulture)))
            {
                skippedApps.Add(appid);
            }
        }

        // Remove all apps that may have been received from other packages
        foreach (var appid in skippedApps)
        {
            payload.Apps.Remove(appid.ToString(CultureInfo.InvariantCulture));
        }

        if (skippedApps.Count > 0)
        {
            AnsiConsole.MarkupLine($"Skipped app ids: [yellow]{string.Join(", ", skippedApps.Order())}[/]");
        }

        return (apps, depots);
    }

    private async Task Request(ProgressTask progress, ProgressTask progressTokens, ProgressTask progressDepots, HashSet<uint> ownedApps, HashSet<uint> ownedDepots)
    {
        var appInfoRequests = new List<PICSRequest>();
        var tokensCount = 0;
        var tokensDeniedCount = 0;
        var tokensNonZeroCount = 0;

        progressTokens.MaxValue = ownedApps.Count;
        progressTokens.StartTask();

        foreach (var chunk in ownedApps.Chunk(ItemsPerRequest))
        {
            PICSTokensCallback tokens = null;

            for (var retry = 3; retry > 0; retry--)
            {
                try
                {
                    var tokensTask = steamApps.PICSGetAccessTokens(chunk, []);
                    tokensTask.Timeout = Timeout;
                    tokens = await tokensTask;
                    break;
                }
                catch (Exception e)
                {
                    AnsiConsole.WriteLine($"App token task failed: {e.GetType()} {e.Message}");

                    await AwaitReconnectIfDisconnected();
                }
            }

            if (tokens == null)
            {
                SomeRequestFailed = true;
                continue;
            }

            tokensCount += tokens.AppTokens.Count;
            tokensDeniedCount += tokens.AppTokensDenied.Count;
            tokensNonZeroCount += tokens.AppTokens.Count(x => x.Value > 0);

            progressTokens.Value += chunk.Length;

            foreach (var (key, value) in tokens.AppTokens)
            {
                if (value > 0)
                {
                    payload.Apps[key.ToString(CultureInfo.InvariantCulture)] = value.ToString(CultureInfo.InvariantCulture);
                }

                appInfoRequests.Add(new PICSRequest(key, value));
            }
        }

        progressTokens.StopTask();

        AnsiConsole.MarkupLine($"App tokens granted: [green]{tokensCount}[/] - Denied: [red]{tokensDeniedCount}[/] - Non-zero: [green]{tokensNonZeroCount}[/]");

        if (appInfoRequests.Count > 0)
        {
            progress.MaxValue = appInfoRequests.Count;
            progress.StartTask();

            var total = (-1L + appInfoRequests.Count + ItemsPerRequest) / ItemsPerRequest;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = ItemsPerRequest };
            var alreadySeen = new HashSet<uint>();
            var depotKeysRequested = 0;
            var depotKeysFailed = 0;
            var allKeyRequests = new List<AsyncJob<DepotKeyCallback>>();

            async Task CheckFinishedDepotKeyRequests()
            {
                var completedKeyRequests = allKeyRequests.Where(x => x.ToTask().IsCompleted).ToList();

                foreach (var keyTask in completedKeyRequests)
                {
                    allKeyRequests.Remove(keyTask);

                    try
                    {
                        var result = await keyTask;

                        progressDepots.Value += 1;

                        if (result.Result != EResult.OK)
                        {
                            depotKeysFailed++;
                            alreadySeen.Add(result.DepotID);
                            continue;
                        }

                        payload.Depots[result.DepotID.ToString(CultureInfo.InvariantCulture)] = Convert.ToHexString(result.DepotKey);
                        knownDepotIds.PreviouslySent.Add(result.DepotID);
                    }
                    catch
                    {
                        depotKeysFailed++;
                        SomeRequestFailed = true;
                    }
                }
            };

            foreach (var chunk in appInfoRequests.AsEnumerable().Reverse().Chunk(ItemsPerRequest))
            {
                AsyncJobMultiple<PICSProductInfoCallback>.ResultSet appInfo = null;

                for (var retry = 3; retry > 0; retry--)
                {
                    try
                    {
                        var appJob = steamApps.PICSGetProductInfo(chunk, []);
                        appJob.Timeout = Timeout;
                        appInfo = await appJob;
                        break;
                    }
                    catch (Exception e)
                    {
                        AnsiConsole.WriteLine($"App info task failed: {e.GetType()} {e.Message}");

                        await AwaitReconnectIfDisconnected();
                    }
                }

                if (appInfo == null)
                {
                    SomeRequestFailed = true;
                    continue;
                }

                if (appInfo.Results == null)
                {
                    continue;
                }

                progress.Value += chunk.Length;

                var depotsToRequest = new HashSet<(uint DepotID, uint AppID)>();

                /*
                foreach (var app in chunk)
                {
                    if (!knownDepotIds.Contains(app.ID) && !alreadySeen.Contains(app.ID))
                    {
                        depotsToRequest.Add((app.ID, app.ID));
                    }
                }
                */

                foreach (var result in appInfo.Results)
                {
                    foreach (var app in result.Apps.Values)
                    {
                        foreach (var depot in app.KeyValues["depots"].Children)
                        {
                            var depotfromapp = depot["depotfromapp"].AsUnsignedInteger();

                            // common redistributables and steam sdk
                            if (depotfromapp == 1007 || depotfromapp == 228980)
                            {
                                continue;
                            }

                            if (!uint.TryParse(depot.Name, CultureInfo.InvariantCulture, out var depotid))
                            {
                                continue;
                            }

                            var dlcappid = depot["dlcappid"].AsUnsignedInteger();

                            if (skippedApps.Contains(dlcappid))
                            {
                                continue;
                            }

                            if (!ownedDepots.Contains(depotid) && !ownedApps.Contains(depotid))
                            {
                                continue;
                            }

                            if (knownDepotIds.PreviouslySent.Contains(depotid) || knownDepotIds.Server.Contains(depotid))
                            {
                                continue;
                            }

                            if (alreadySeen.Contains(depotid))
                            {
                                continue;
                            }

                            // Depot key requests timeout, so do not request keys for depots that have no manifests
                            if (depotfromapp == 0 && depot["manifests"].Children.Count == 0 && depot["encryptedmanifests"].Children.Count == 0)
                            {
                                continue;
                            }

                            depotsToRequest.Add((depotid, app.ID));
                        }
                    }
                }

                if (depotsToRequest.Count > 0)
                {
                    progressDepots.MaxValue += depotsToRequest.Count;

                    foreach (var (depotid, appid) in depotsToRequest)
                    {
                        var job = steamApps.GetDepotDecryptionKey(depotid, appid);
                        job.Timeout = Timeout;
                        allKeyRequests.Add(job);
                        await Task.Delay(500);

                        if (depotKeysRequested++ % 15 == 0)
                        {
                            await CheckFinishedDepotKeyRequests();
                        }
                    }
                }

                if (!Program.IsConnected)
                {
                    await Program.ReconnectEvent.Task;
                }

                if (allKeyRequests.Count > 0)
                {
                    await CheckFinishedDepotKeyRequests();
                }
            }

            if (allKeyRequests.Count > 0)
            {
                try
                {
                    await Task.WhenAll(allKeyRequests.Select(x => x.ToTask()));
                }
                catch
                {
                    SomeRequestFailed = true;
                }

                await CheckFinishedDepotKeyRequests();
            }

            if (depotKeysRequested > 0)
            {
                AnsiConsole.MarkupLine($"Depot keys requested: [green]{depotKeysRequested}[/] - Failed: [red]{depotKeysFailed}[/]");
            }
        }

        progress.StopTask();
    }

    private static async Task AwaitReconnectIfDisconnected()
    {
        if (Program.IsConnected)
        {
            await Task.Delay(200 + Random.Shared.Next(1001));
            return;
        }

        AnsiConsole.MarkupLine("[red]Disconnected from Steam while requesting, will continue after logging in again.[/]");

        await Program.ReconnectEvent.Task;
    }
}
