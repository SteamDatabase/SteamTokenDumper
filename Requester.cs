using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal class Requester
    {
        private const int ItemsPerRequest = 200;
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
        private readonly Payload payload;
        private readonly SteamApps steamApps;
        private readonly Configuration config;
        private readonly HashSet<uint> skippedPackages = new();
        private readonly HashSet<uint> skippedApps = new();

        public Requester(Payload payload, SteamApps steamApps, Configuration config)
        {
            this.payload = payload;
            this.steamApps = steamApps;
            this.config = config;
        }

        public List<SteamApps.PICSRequest> ProcessLicenseList(SteamApps.LicenseListCallback licenseList)
        {
            var packages = new List<SteamApps.PICSRequest>();

            foreach (var license in licenseList.LicenseList)
            {
                packages.Add(new SteamApps.PICSRequest
                {
                    ID = license.PackageID,
                    AccessToken = license.AccessToken,
                    Public = false
                });

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

                payload.Subs[license.PackageID.ToString()] = license.AccessToken.ToString();
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

        public async Task ProcessPackages(List<SteamApps.PICSRequest> packages)
        {
            Console.WriteLine();
            Console.WriteLine($"You have {packages.Count} licenses ({packages.Count(x => x.AccessToken != 0)} of them have a token)");
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
                    }
                }

                ConsoleRewriteLine($"You own {apps.Count} apps");
            }

            foreach (var appid in config.SkipApps)
            {
                if (payload.Apps.Remove(appid.ToString()))
                {
                    skippedApps.Add(appid);
                }
            }

            // Remove all apps that may have been received from other packages
            foreach (var appid in skippedApps)
            {
                payload.Apps.Remove(appid.ToString());
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

                                var dlcappid = depot["dlcappid"].AsUnsignedInteger();

                                if (skippedApps.Contains(dlcappid))
                                {
                                    continue;
                                }

                                if (payload.Depots.ContainsKey(depot.Name!))
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
            Console.WriteLine($"Depot keys: {payload.Depots.Count}");
            Console.ResetColor();
        }

        private static void ConsoleRewriteLine(string text)
        {
            Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r{text}");
        }
    }
}
