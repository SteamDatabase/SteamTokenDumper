using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamTokenDumper
{
    internal static class Program
    {
        private const string SENTRYHASHFILE = "SteamTokenDumper.sentryhash.bin";

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

        private static readonly Payload Payload = new Payload();

        public static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Console.ResetColor();
            Console.WriteLine(" ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  This program will login into your Steam account to get tokens");
            Console.WriteLine("  all depot keys you have access to, and then submit to SteamDB.");
            Console.WriteLine(" ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  All tokens and keys are GLOBAL, and are not unique to your account,");
            Console.WriteLine("  SteamDB bot simply can't get them because it doesn't own these games.");
            Console.WriteLine(" ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Doing this allows SteamDB to track hidden apps and file lists.");
            Console.WriteLine(" ");
            Console.ResetColor();

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
                while (pass.Length < 6);
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

            isRunning = true;

            Console.WriteLine("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }

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
                    password = password.Substring(0, password.Length - 1);
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
                    SentryFileHash = File.Exists(SENTRYHASHFILE) ? File.ReadAllBytes(SENTRYHASHFILE) : null,
                });
            }
        }

        private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (Payload.SteamID > 0)
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
                Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

                isRunning = false;

                return;
            }

            user = pass = authCode = twoFactorAuth = null;
            Payload.SteamID = callback.ClientSteamID.ConvertToUInt64();

            if (callback.ClientSteamID.AccountType == EAccountType.AnonUser)
            {
                await TryDoTheThing(new HashSet<uint> { 17906 });
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

            File.WriteAllBytes(SENTRYHASHFILE, sentryHash);

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

            var packages = licenseList.LicenseList.Select(x => x.PackageID);

            await TryDoTheThing(new HashSet<uint>(packages));
        }

        private static async Task TryDoTheThing(HashSet<uint> packages)
        {
            try
            {
                await DoTheThing(packages);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(e);
                Console.ResetColor();
            }

            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonNonStringKeyDictionaryConverterFactory());

            await SendTokens(JsonSerializer.Serialize(Payload, options));

            steamUser.LogOff();
        }

        private static async Task DoTheThing(HashSet<uint> packages)
        {
            Console.WriteLine();
            Console.WriteLine($"You have {packages.Count} licenses");

            // TODO: packages.Split?
            var timeout = TimeSpan.FromMinutes(1);
            var infoTask = steamApps.PICSGetProductInfo(Enumerable.Empty<uint>(), packages);
            infoTask.Timeout = timeout;
            var info = await infoTask;
            var apps = new HashSet<uint>();
            var depots = new Dictionary<uint, bool>();

            foreach (var result in info.Results)
            {
                foreach (var package in result.Packages.Values)
                {
                    foreach (var appid in package.KeyValues["appids"].Children)
                    {
                        apps.Add(appid.AsUnsignedInteger());
                    }

                    foreach (var depotid in package.KeyValues["depotids"].Children)
                    {
                        depots[depotid.AsUnsignedInteger()] = false;
                    }
                }
            }

            Console.WriteLine($"You own {apps.Count} apps");

            // TODO: apps.Split?
            var tokensTask = steamApps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());
            tokensTask.Timeout = timeout;
            var tokens = await tokensTask;
            var nonZero = tokens.AppTokens.Count(x => x.Value > 0);
            var appInfoRequests = new List<SteamApps.PICSRequest>();

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Tokens granted: {tokens.AppTokens.Count} - Denied: {tokens.AppTokensDenied.Count} - Non-zero: {nonZero}");
            Console.ResetColor();

            foreach (var token in tokens.AppTokens)
            {
                if (token.Value > 0)
                {
                    Payload.Apps.Add(token.Key, token.Value.ToString());
                }

                appInfoRequests.Add(new SteamApps.PICSRequest
                {
                    ID = token.Key,
                    AccessToken = token.Value,
                    Public = false,
                });
            }


            var tasks = new List<Task<SteamApps.DepotKeyCallback>>();
            var currentTasks = new List<Task<SteamApps.DepotKeyCallback>>();

            if (appInfoRequests.Count > 0)
            {
                Console.WriteLine();

                const int APPS_PER_REQUEST = 200;
                var loops = 0;
                var total = (-1L + appInfoRequests.Count + APPS_PER_REQUEST) / APPS_PER_REQUEST;

                foreach (var chunk in appInfoRequests.Split(APPS_PER_REQUEST))
                {
                    Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
                    Console.Write($"\rApp info request {++loops} of {total} - {tasks.Count} depot keys requested...");

                    var appJob = steamApps.PICSGetProductInfo(chunk, Enumerable.Empty<SteamApps.PICSRequest>());
                    appJob.Timeout = timeout;
                    var appInfo = await appJob;

                    foreach (var result in appInfo.Results)
                    {
                        foreach (var app in result.Apps.Values)
                        {
                            if (!depots.TryGetValue(app.ID, out var depotTried) || !depotTried)
                            {
                                depots[app.ID] = true;

                                var appKeyJob = steamApps.GetDepotDecryptionKey(app.ID, app.ID);
                                appKeyJob.Timeout = timeout;

                                tasks.Add(appKeyJob.ToTask());
                                currentTasks.Add(appKeyJob.ToTask());
                            }

                            if (app.KeyValues["depots"] != null)
                            {
                                foreach (var depot in app.KeyValues["depots"].Children)
                                {
                                    var depotfromapp = depot["depotfromapp"].AsUnsignedInteger();

                                    // common redistributables and steam sdk
                                    if (depotfromapp == 1007 || depotfromapp == 228980)
                                    {
                                        continue;
                                    }

                                    if (uint.TryParse(depot.Name, out var depotid) && depots.TryGetValue(depotid, out depotTried) && !depotTried)
                                    {
                                        depots[depotid] = true;

                                        var job = steamApps.GetDepotDecryptionKey(depotid, app.ID);
                                        job.Timeout = timeout;

                                        tasks.Add(job.ToTask());
                                        currentTasks.Add(job.ToTask());
                                    }
                                }
                            }
                        }
                    }

                    Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
                    Console.Write($"\rApp info request {loops} of {total} - Waiting for {currentTasks.Count} tasks to finish...");
                    await Task.WhenAll(currentTasks);
                    currentTasks.Clear();
                }
            }

            if (tasks.Count > 0)
            {
                Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
                Console.Write($"\rWaiting for {tasks.Count} tasks to finish...                       ");

                await Task.WhenAll(tasks);

                var depotKeys = new Dictionary<EResult, int>();

                foreach (var task in tasks)
                {
                    depotKeys.TryGetValue(task.Result.Result, out var currentCount);
                    depotKeys[task.Result.Result] = currentCount + 1;

                    if (task.Result.Result == EResult.OK)
                    {
                        Payload.Depots.Add(task.Result.DepotID, BitConverter.ToString(task.Result.DepotKey).Replace("-", ""));
                    }
                }

                Console.WriteLine();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Depot keys: {0}", string.Join(" - ", depotKeys.Select(x => x.Key + "=" + x.Value)));
                Console.ResetColor();
            }
        }

        private static async Task SendTokens(string postData)
        {
            Console.WriteLine();
            Console.WriteLine("Submitting tokens to SteamDB...");

            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(10);
                    httpClient.DefaultRequestHeaders.Add("User-Agent", $"{nameof(SteamTokenDumper)} v{Payload.Version}");
                    var content = new StringContent(postData, Encoding.UTF8, "application/json");
                    var result = await httpClient.PostAsync("https://steamdb.info/api/SubmitToken/", content);

                    Console.WriteLine(await result.Content.ReadAsStringAsync());

                    result.EnsureSuccessStatusCode();
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Whoops: {0}", e.Message);
                Console.Error.WriteLine("Submission failed, written data to dumper.json file");
                Console.ResetColor();

                File.WriteAllText("dumper.json", postData);
            }

            Console.WriteLine();
        }
    }
}
