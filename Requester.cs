using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;
using static SteamKit2.SteamApps;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal sealed class Requester
{
    private const int ItemsPerRequest = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private bool SomeRequestFailed;
    private readonly Payload payload;
    private readonly SteamApps steamApps;
    private readonly KnownDepotIds knownDepotIds;
    private readonly Configuration config;
    private readonly HashSet<uint> skippedPackages = new();
    private readonly HashSet<uint> skippedApps = new();

    public Requester(Payload payload, SteamApps steamApps, KnownDepotIds knownDepotIds, Configuration config)
    {
        this.payload = payload;
        this.steamApps = steamApps;
        this.knownDepotIds = knownDepotIds;
        this.config = config;
    }

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

        if (skippedPackages.Any())
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skipped auto granted packages: {string.Join(", ", skippedPackages)}");
            Console.ResetColor();
        }

        return packages;
    }

    public async Task ProcessPackages(List<PICSRequest> packages)
    {
        Console.WriteLine();
        Console.WriteLine($"You have {packages.Count} licenses ({packages.Count(x => x.AccessToken != 0)} of them have a token)");
        Console.WriteLine();

        try
        {
            var (apps, depots) = await RequestPackageInfo(packages);
            await Request(apps, depots);
        }
        catch (Exception e)
        {
            SomeRequestFailed = true;

            await Console.Error.WriteLineAsync();
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync(e.ToString());
            Console.ResetColor();
        }

        if (SomeRequestFailed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("[!]");
            await Console.Error.WriteLineAsync("[!] Some of the requests to Steam failed, which may have resulted in");
            await Console.Error.WriteLineAsync("[!] some of the tokens or depot keys not being fetched.");
            await Console.Error.WriteLineAsync("[!] You can try running the dumper again later.");
            await Console.Error.WriteLineAsync("[!]");
            await Console.Error.WriteLineAsync();
            Console.ResetColor();
        }
    }

    private async Task<(HashSet<uint> Apps, HashSet<uint> Depots)> RequestPackageInfo(List<PICSRequest> subInfoRequests)
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
                    var infoTask = steamApps.PICSGetProductInfo(Enumerable.Empty<PICSRequest>(), chunk);
                    infoTask.Timeout = Timeout;
                    info = await infoTask;
                    break;
                }
                catch (Exception e)
                {
                    ConsoleRewriteLine($"Package info task failed: {e.GetType()} {e.Message}");

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

            ConsoleRewriteLine($"You own {apps.Count} apps and {depots.Count} depots");
        }

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

        if (skippedApps.Any())
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skipped app ids: {string.Join(", ", skippedApps)}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine();

        return (apps, depots);
    }

    private async Task Request(HashSet<uint> ownedApps, HashSet<uint> ownedDepots)
    {
        var appInfoRequests = new List<PICSRequest>();
        var tokensCount = 0;
        var tokensDeniedCount = 0;
        var tokensNonZeroCount = 0;

        foreach (var chunk in ownedApps.Chunk(ItemsPerRequest))
        {
            PICSTokensCallback tokens = null;

            for (var retry = 3; retry > 0; retry--)
            {
                try
                {
                    var tokensTask = steamApps.PICSGetAccessTokens(chunk, Enumerable.Empty<uint>());
                    tokensTask.Timeout = Timeout;
                    tokens = await tokensTask;
                    break;
                }
                catch (Exception e)
                {
                    ConsoleRewriteLine($"App token task failed: {e.GetType()} {e.Message}");

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

            ConsoleRewriteLine($"App tokens granted: {tokensCount} - Denied: {tokensDeniedCount} - Non-zero: {tokensNonZeroCount}");

            foreach (var (key, value) in tokens.AppTokens)
            {
                if (value > 0)
                {
                    payload.Apps[key.ToString(CultureInfo.InvariantCulture)] = value.ToString(CultureInfo.InvariantCulture);
                }

                appInfoRequests.Add(new PICSRequest(key, value));
            }
        }

        Console.WriteLine();

        if (appInfoRequests.Count > 0)
        {
            Console.WriteLine();

            var loops = 0;
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

            foreach (var chunk in appInfoRequests.Chunk(ItemsPerRequest))
            {
                ConsoleRewriteLine($"App info request {++loops} of {total} - {payload.Depots.Count} depot keys - waiting for appinfo...");

                AsyncJobMultiple<PICSProductInfoCallback>.ResultSet appInfo = null;

                for (var retry = 3; retry > 0; retry--)
                {
                    try
                    {
                        var appJob = steamApps.PICSGetProductInfo(chunk, Enumerable.Empty<PICSRequest>());
                        appJob.Timeout = Timeout;
                        appInfo = await appJob;
                        break;
                    }
                    catch (Exception e)
                    {
                        ConsoleRewriteLine($"App info task failed: {e.GetType()} {e.Message}");

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

                            depotsToRequest.Add((depotid, app.ID));
                        }
                    }
                }

                if (depotsToRequest.Count > 0)
                {
                    var sentKeyRequests = 0;

                    foreach (var (depotid, appid) in depotsToRequest)
                    {
                        var job = steamApps.GetDepotDecryptionKey(depotid, appid);
                        job.Timeout = Timeout;
                        allKeyRequests.Add(job);
                        await Task.Delay(500);

                        sentKeyRequests++;

                        if (depotKeysRequested++ % 15 == 0)
                        {
                            await CheckFinishedDepotKeyRequests();

                            ConsoleRewriteLine($"App info request {loops} of {total} - {payload.Depots.Count} depot keys ({depotKeysRequested} requested, {depotKeysFailed} failed, {allKeyRequests.Count} waiting, {depotsToRequest.Count - sentKeyRequests} to send)");
                        }
                    }
                }

                if (!Program.IsConnected)
                {
                    await Program.ReconnectEvent.Task;
                }
            }

            if (allKeyRequests.Count > 0)
            {
                ConsoleRewriteLine($"{total} app info requests done - {payload.Depots.Count} depot keys ({depotKeysRequested} requested, {depotKeysFailed} failed, {allKeyRequests.Count} waiting) - this might take a while...");

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

            ConsoleRewriteLine($"{total} app info requests done - {payload.Depots.Count} depot keys ({depotKeysRequested} requested, {depotKeysFailed} failed)");
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Sub tokens: {payload.Subs.Count}");
        Console.WriteLine($"App tokens: {payload.Apps.Count}");
        Console.WriteLine($"Depot keys: {payload.Depots.Count}");
        Console.ResetColor();
    }

    private static async Task AwaitReconnectIfDisconnected()
    {
        if (Program.IsConnected)
        {
            await Task.Delay(200 + Random.Shared.Next(1001));
            return;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        await Console.Error.WriteLineAsync("[!] Disconnected from Steam while requesting, will continue after logging in again.");
        Console.ResetColor();

        await Program.ReconnectEvent.Task;
    }

    private static void ConsoleRewriteLine(string text)
    {
        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r{text}");
    }
}
