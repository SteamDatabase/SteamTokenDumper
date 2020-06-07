using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamTokenDumper
{
    internal class Requester
    {
        private const int ItemsPerRequest = 200;
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
        private readonly Payload payload;
        private readonly SteamApps steamApps;

        public Requester(Payload payload, SteamApps steamApps)
        {
            this.payload = payload;
            this.steamApps = steamApps;
        }

        public async Task ProcessLicenseList(SteamApps.LicenseListCallback licenseList)
        {
            var packages = new List<SteamApps.PICSRequest>();
            var nonZeroTokens = 0;

            foreach (var license in licenseList.LicenseList)
            {
                packages.Add(new SteamApps.PICSRequest
                {
                    ID = license.PackageID,
                    AccessToken = license.AccessToken,
                    Public = false
                });

                if (license.AccessToken == 0)
                {
                    continue;
                }

                payload.Subs[license.PackageID.ToString()] = license.AccessToken.ToString();
                nonZeroTokens++;
            }

            Console.WriteLine();
            Console.WriteLine($"You have {packages.Count} licenses ({nonZeroTokens} of them have a token)");
            Console.WriteLine();

            try
            {
                var apps = await RequestPackageInfo(packages);
                await Request(apps);
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                await Console.Error.WriteLineAsync(e.ToString());
                Console.ResetColor();
            }
        }

        private async Task<HashSet<uint>> RequestPackageInfo(List<SteamApps.PICSRequest> subInfoRequests)
        {
            var apps = new HashSet<uint>();

            foreach (var chunk in subInfoRequests.Split(ItemsPerRequest))
            {
                var infoTask = steamApps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), chunk);
                infoTask.Timeout = Timeout;
                var info = await infoTask;

                if (info.Results == null)
                {
                    continue;
                }

                foreach (var result in info.Results)
                {
                    foreach (var package in result.Packages.Values)
                    {
                        foreach (var appid in package.KeyValues["appids"].Children)
                        {
                            apps.Add(appid.AsUnsignedInteger());
                        }
                    }
                }

                ConsoleRewriteLine($"You own {apps.Count} apps");
            }

            Console.WriteLine();
            Console.WriteLine();

            return apps;
        }

        private async Task Request(HashSet<uint> apps)
        {
            var appInfoRequests = new List<SteamApps.PICSRequest>();
            var tokensCount = 0;
            var tokensDeniedCount = 0;
            var tokensNonZeroCount = 0;

            foreach (var chunk in apps.Split(ItemsPerRequest))
            {
                var tokensTask = steamApps.PICSGetAccessTokens(chunk, Enumerable.Empty<uint>());
                tokensTask.Timeout = Timeout;
                var tokens = await tokensTask;

                tokensCount += tokens.AppTokens.Count;
                tokensDeniedCount += tokens.AppTokensDenied.Count;
                tokensNonZeroCount += tokens.AppTokens.Count(x => x.Value > 0);

                ConsoleRewriteLine($"App tokens granted: {tokensCount} - Denied: {tokensDeniedCount} - Non-zero: {tokensNonZeroCount}");

                foreach (var (key, value) in tokens.AppTokens)
                {
                    if (value > 0)
                    {
                        payload.Apps[key.ToString()] = value.ToString();
                    }

                    appInfoRequests.Add(new SteamApps.PICSRequest
                    {
                        ID = key,
                        AccessToken = value,
                        Public = false
                    });
                }
            }

            Console.WriteLine();

            var depotKeys = new Dictionary<EResult, int>();

            if (appInfoRequests.Count > 0)
            {
                Console.WriteLine();

                var loops = 0;
                var total = (-1L + appInfoRequests.Count + ItemsPerRequest) / ItemsPerRequest;

                foreach (var chunk in appInfoRequests.Split(ItemsPerRequest))
                {
                    ConsoleRewriteLine($"App info request {++loops} of {total} - {payload.Depots.Count} depot keys - Waiting for appinfo...");

                    var appJob = steamApps.PICSGetProductInfo(chunk, Enumerable.Empty<SteamApps.PICSRequest>());
                    appJob.Timeout = Timeout;
                    var appInfo = await appJob;

                    if (appInfo.Results == null)
                    {
                        continue;
                    }

                    var currentTasks = new List<Task<SteamApps.DepotKeyCallback>>();

                    foreach (var app in chunk)
                    {
                        var job = steamApps.GetDepotDecryptionKey(app.ID, app.ID);
                        job.Timeout = Timeout;
                        currentTasks.Add(job.ToTask());
                    }

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

                                if (!uint.TryParse(depot.Name, out var depotid))
                                {
                                    continue;
                                }

                                var job = steamApps.GetDepotDecryptionKey(depotid, app.ID);
                                job.Timeout = Timeout;
                                currentTasks.Add(job.ToTask());
                            }
                        }
                    }

                    ConsoleRewriteLine($"App info request {loops} of {total} - {payload.Depots.Count} depot keys - Waiting for {currentTasks.Count} tasks...");

                    await Task.WhenAll(currentTasks);

                    foreach (var task in currentTasks)
                    {
                        depotKeys.TryGetValue(task.Result.Result, out var currentCount);
                        depotKeys[task.Result.Result] = currentCount + 1;

                        if (task.Result.Result == EResult.OK)
                        {
                            payload.Depots[task.Result.DepotID.ToString()] = BitConverter.ToString(task.Result.DepotKey).Replace("-", "");
                        }
                    }
                }

                appInfoRequests.Clear();
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Sub tokens: {payload.Subs.Count}");
            Console.WriteLine($"App tokens: {payload.Apps.Count}");
            Console.WriteLine($"Depot keys: {payload.Depots.Count} ({string.Join(" - ", depotKeys.Select(x => x.Key + "=" + x.Value))})");
            Console.ResetColor();
        }

        private static void ConsoleRewriteLine(string text)
        {
            Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r{text}");
        }
    }
}
