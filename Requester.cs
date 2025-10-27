using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Spectre.Console;
using SteamKit2;
using static SteamKit2.SteamApps;

namespace SteamTokenDumper;

internal sealed class Requester(Payload payload, SteamApps steamApps, KnownDepotIds knownDepotIds, Program app)
{
    private const int ItemsPerRequest = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private bool SomeRequestFailed;
    private readonly HashSet<uint> skippedPackages = [];
    private readonly HashSet<uint> skippedApps = [];

    public List<PICSRequest> ProcessLicenseList(LicenseListCallback licenseList)
    {
        var packages = new List<PICSRequest>(licenseList.LicenseList.Count);
        payload.Subs.EnsureCapacity(packages.Count);

        foreach (var license in licenseList.LicenseList)
        {
            packages.Add(new PICSRequest(license.PackageID, license.AccessToken));

            // Request autogrant packages so we can automatically skip all apps inside of it
            if (app.Configuration.SkipAutoGrant && license.PaymentMethod == EPaymentMethod.AutoGrant)
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

    public async Task ProcessPackages(List<PICSRequest> packages, List<uint> storePackages)
    {
        if (storePackages.Count > 0 && !app.Configuration.SkipAutoGrant) // Just ignore store data if user configured to skip autogrant packages
        {
            var ownedPackages = new HashSet<uint>(packages.Count);
            var onlyStorePackages = new List<uint>();

            foreach (var package in packages)
            {
                ownedPackages.Add(package.ID);
            }

            foreach (var package in storePackages)
            {
                if (!ownedPackages.Contains(package))
                {
                    onlyStorePackages.Add(package);
                }
            }

            if (onlyStorePackages.Count > 0)
            {
                AnsiConsole.MarkupLine($"You own {onlyStorePackages.Count} licenses that the Steam client does not know about.");

                var tokensTask = steamApps.PICSGetAccessTokens([], onlyStorePackages);
                tokensTask.Timeout = Timeout;
                var tokens = await tokensTask;

                foreach (var (key, value) in tokens.PackageTokens)
                {
                    packages.Add(new PICSRequest(key, value));

                    if (value > 0)
                    {
                        payload.Subs[key.ToString(CultureInfo.InvariantCulture)] = value.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Licenses: [green]{packages.Count}[/] - Package tokens: [green]{packages.Count(x => x.AccessToken != 0)}[/]");

        try
        {
            await AnsiConsole.Progress()
                .Columns([
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new IntValueProgressColumn(),
                    new ElapsedTimeColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                ])
                .StartAsync(async ctx =>
                {
                    var progressPackages = ctx.AddTask("Package info", maxValue: packages.Count);
                    var progressTokens = ctx.AddTask("App tokens", autoStart: false, maxValue: 0);
                    var progressApps = ctx.AddTask("App info", autoStart: false, maxValue: 0);
                    var progressDepots = ctx.AddTask("Depot keys", autoStart: false, maxValue: 0);

                    var (apps, depots) = await RequestPackageInfo(progressPackages, progressApps, progressTokens, packages);
                    await Request(progressApps, progressTokens, progressDepots, apps, depots);
                });

            Ansi.Progress(Ansi.ProgressState.Hidden);

            AnsiConsole.MarkupLine($"Sub tokens: [green]{payload.Subs.Count}[/]");
            AnsiConsole.MarkupLine($"App tokens: [green]{payload.Apps.Count}[/]");
            AnsiConsole.MarkupLine($"Depot keys: [green]{payload.Depots.Count}[/]");
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

    private async Task<(HashSet<uint> Apps, HashSet<uint> Depots)> RequestPackageInfo(ProgressTask progress, ProgressTask progressApps, ProgressTask progressTokens, List<PICSRequest> subInfoRequests)
    {
        var apps = new HashSet<uint>();
        var depots = new HashSet<uint>();

        foreach (var chunk in subInfoRequests.Chunk(ItemsPerRequest))
        {
            AsyncJobMultiple<PICSProductInfoCallback>.ResultSet? info = null;

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

                        if (app.Configuration.SkipApps.Contains(appid))
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
            progressApps.MaxValue = apps.Count;
            progressTokens.MaxValue = apps.Count;

            Ansi.Progress(progress);
        }

        progress.StopTask();

        foreach (var appid in app.Configuration.SkipApps)
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
        var appInfoRequests = new List<PICSRequest>(ItemsPerRequest);
        var tokensCount = 0;
        var tokensDeniedCount = 0;
        var tokensNonZeroCount = 0;

        progressTokens.MaxValue = ownedApps.Count;
        progressTokens.StartTask();

        foreach (var chunk in ownedApps.Chunk(ItemsPerRequest))
        {
            PICSTokensCallback? tokens = null;

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

            progress.MaxValue -= tokens.AppTokensDenied.Count;
            progressTokens.Value += chunk.Length;

            Ansi.Progress(progressTokens);

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
            }

            foreach (var chunk in appInfoRequests.AsEnumerable().Reverse().Chunk(ItemsPerRequest))
            {
                AsyncJobMultiple<PICSProductInfoCallback>.ResultSet? appInfo = null;

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

                Ansi.Progress(progress);

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
                            if (depotfromapp is 1007 or 228980)
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

                if (!app.IsConnected)
                {
                    await app.ReconnectEvent.Task;
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
                if (depotKeysFailed > 0)
                {
                    AnsiConsole.MarkupLine($"Depot keys requested: [green]{depotKeysRequested}[/] - Failed: [red]{depotKeysFailed}[/] [gray](failures are expected)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"Depot keys requested: [green]{depotKeysRequested}[/]");
                }
            }
        }

        progress.StopTask();
    }

    public static async Task<List<uint>> GetOwnedFromStore(SteamClient steamClient, string refreshToken, HttpClient httpClient)
    {
        AnsiConsole.WriteLine("Requesting owned licenses from the store...");

        try
        {
            var steamid = steamClient.SteamID;
            ArgumentNullException.ThrowIfNull(steamid);

            var newToken = await steamClient.Authentication.GenerateAccessTokenForAppAsync(steamid, refreshToken, allowRenewal: false);

            ArgumentNullException.ThrowIfNullOrEmpty(newToken.AccessToken);

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/dynamicstore/userdata");

            var cookie = string.Concat(steamid.ConvertToUInt64().ToString(), "||", newToken.AccessToken);
            requestMessage.Headers.Add("Cookie", string.Concat("steamLoginSecure=", WebUtility.UrlEncode(cookie)));

            var response = await httpClient.SendAsync(requestMessage);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync(StoreUserDataJsonContext.Default.StoreUserData);

            ArgumentNullException.ThrowIfNull(data);

            AnsiConsole.WriteLine($"Store says you own {data.OwnedPackages.Count} licenses.");

            return data.OwnedPackages;
        }
        catch (Exception e)
        {
            AnsiConsole.WriteLine($"Failed to get user data from the store: {e.GetType()} {e.Message}");
        }

        return [];
    }

    private async Task AwaitReconnectIfDisconnected()
    {
        if (app.IsConnected)
        {
            await Task.Delay(200 + Random.Shared.Next(1001));
            return;
        }

        AnsiConsole.MarkupLine("[red]Disconnected from Steam while requesting, will continue after logging in again.[/]");

        await app.ReconnectEvent.Task;
    }
}
