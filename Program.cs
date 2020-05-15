using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal static class Program
    {
        private const string SentryHashFile = "SteamTokenDumper.sentryhash.bin";
        private const int ItemsPerRequest = 200;
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

        private static SteamClient steamClient;
        private static CallbackManager manager;

        private static SteamUser steamUser;
        private static SteamApps steamApps;
        private static IDisposable LicenseListCallback;

        private static bool isRunning;

        private static string user;
        private static string pass;
        private static string authCode;
        private static string twoFactorAuth;

        private static readonly ApiClient ApiClient = new ApiClient();
        private static readonly Payload Payload = new Payload();

        public static async Task Main()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("[>] Read https://steamdb.info/tokendumper/ before using this");
            Console.ResetColor();
            Console.WriteLine();

            await ApiClient.CheckVersion();

            Console.Write("Enter your Steam username: ");
            user = ReadUserInput(true);

            if (string.IsNullOrEmpty(user))
            {
                Console.WriteLine("Will login as an anonymous account");
            }
            else
            {
                do
                {
                    Console.Write("Enter your Steam password: ");
                    pass = ReadUserInput();
                }
                while (string.IsNullOrEmpty(pass));
            }

            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);

            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

            LicenseListCallback = manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

            try
            {
                SteamClientData.ReadFromSteamClient(Payload);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                await Console.Error.WriteLineAsync(e.ToString());
                Console.ResetColor();
            }

            Console.WriteLine();

            isRunning = true;

            Console.WriteLine("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }

            ApiClient.Dispose();

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static string ReadUserInput(bool showFirstChar = false)
        {
            var password = string.Empty;
            var info = Console.ReadKey(true);

            while (info.Key != ConsoleKey.Enter && info.Key != ConsoleKey.Tab)
            {
                if (info.Key != ConsoleKey.Backspace && info.KeyChar != 0)
                {
                    if (showFirstChar && password.Length == 0)
                    {
                        Console.Write(info.KeyChar.ToString());
                    }
                    else
                    {
                        Console.Write("*");
                    }

                    password += info.KeyChar;
                }
                else if (info.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password = password[..^1];
                    var pos = Console.CursorLeft;
                    Console.SetCursorPosition(pos - 1, Console.CursorTop);
                    Console.Write(" ");
                    Console.SetCursorPosition(pos - 1, Console.CursorTop);
                }

                info = Console.ReadKey(true);
            }

            Console.WriteLine();

            return password;
        }

        private static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Connected to Steam! Logging in...");

            if (string.IsNullOrEmpty(user))
            {
                steamUser.LogOnAnonymous();
            }
            else
            {
                steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    LoginID = 1337,
                    Username = user,
                    Password = pass,
                    AuthCode = authCode,
                    TwoFactorCode = twoFactorAuth,
                    SentryFileHash = File.Exists(SentryHashFile) ? File.ReadAllBytes(SentryHashFile) : null,
                });
            }
        }

        private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (Payload.SteamID != null)
            {
                isRunning = false;

                Console.WriteLine("Exiting...");

                return;
            }

            Console.WriteLine("Disconnected from Steam, reconnecting...");

            steamClient.Connect();
        }

        private static async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                Console.WriteLine("This account is SteamGuard protected!");

                if (is2FA)
                {
                    Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = Console.ReadLine();
                }
                else
                {
                    Console.Write("Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);
                    authCode = Console.ReadLine();
                }

                return;
            }

            if (callback.Result != EResult.OK)
            {
                Console.ForegroundColor = ConsoleColor.Red;

                if (callback.Result == EResult.InvalidPassword)
                {
                    Console.WriteLine("You have entered an invalid username or password.");
                }
                else if (callback.Result == EResult.TwoFactorCodeMismatch)
                {
                    Console.WriteLine("You have entered an invalid two factor code.");
                }
                else
                {
                    Console.WriteLine($"Unable to logon to Steam: {callback.Result} ({callback.ExtendedResult})");
                }

                Console.ResetColor();

                isRunning = false;

                return;
            }

            user = pass = authCode = twoFactorAuth = null;
            var steamid = callback.ClientSteamID ?? new SteamID(0, EUniverse.Public, EAccountType.Invalid);
            Payload.SteamID = steamid.Render();

            if (steamid.AccountType == EAccountType.AnonUser)
            {
                await ApiClient.SendTokens(Payload);

                steamUser.LogOff();
            }
            else
            {
                Console.WriteLine("Waiting for licenses...");
            }
        }

        private static void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            int fileSize;
            byte[] sentryHash;

            using (var stream = new MemoryStream(callback.BytesToWrite))
            {
                stream.Seek(callback.Offset, SeekOrigin.Begin);
                stream.Write(callback.Data, 0, callback.BytesToWrite);
                stream.Seek(0, SeekOrigin.Begin);

                fileSize = (int)stream.Length;

                using var sha = new SHA1CryptoServiceProvider();
                sentryHash = sha.ComputeHash(stream);
            }

            File.WriteAllBytes(SentryHashFile, sentryHash);

            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash
            });
        }

        private static async void OnLicenseList(SteamApps.LicenseListCallback licenseList)
        {
            LicenseListCallback.Dispose();

            var packages = new List<SteamApps.PICSRequest>();
            var nonZeroTokens = 0;

            foreach (var license in licenseList.LicenseList)
            {
                packages.Add(new SteamApps.PICSRequest
                {
                    ID = license.PackageID,
                    AccessToken = license.AccessToken,
                    Public = false,
                });

                if (license.AccessToken > 0)
                {
                    Payload.Subs[license.PackageID.ToString()] = license.AccessToken.ToString();
                    nonZeroTokens++;
                }
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

            await ApiClient.SendTokens(Payload);

            steamUser.LogOff();
        }

        private static async Task<HashSet<uint>> RequestPackageInfo(List<SteamApps.PICSRequest> subInfoRequests)
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

        private static async Task Request(HashSet<uint> apps)
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
                        Payload.Apps[key.ToString()] = value.ToString();
                    }

                    appInfoRequests.Add(new SteamApps.PICSRequest
                    {
                        ID = key,
                        AccessToken = value,
                        Public = false,
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
                    ConsoleRewriteLine($"App info request {++loops} of {total} - {Payload.Depots.Count} depot keys - Waiting for appinfo...");

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

                    ConsoleRewriteLine($"App info request {loops} of {total} - {Payload.Depots.Count} depot keys - Waiting for {currentTasks.Count} tasks...");

                    await Task.WhenAll(currentTasks);

                    foreach (var task in currentTasks)
                    {
                        depotKeys.TryGetValue(task.Result.Result, out var currentCount);
                        depotKeys[task.Result.Result] = currentCount + 1;

                        if (task.Result.Result == EResult.OK)
                        {
                            Payload.Depots[task.Result.DepotID.ToString()] = BitConverter.ToString(task.Result.DepotKey).Replace("-", "");
                        }
                    }
                }

                appInfoRequests.Clear();
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Sub tokens: {Payload.Subs.Count}");
            Console.WriteLine($"App tokens: {Payload.Apps.Count}");
            Console.WriteLine($"Depot keys: {Payload.Depots.Count} ({string.Join(" - ", depotKeys.Select(x => x.Key + "=" + x.Value))})");
            Console.ResetColor();
        }

        private static void ConsoleRewriteLine(string text)
        {
            Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r{text}");
        }
    }
}
