using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamTokenDumper
{
    internal static class Program
    {
        private const int APPS_PER_REQUEST = 10_000;

        private static SteamClient steamClient;
        private static CallbackManager manager;

        private static SteamUser steamUser;
        private static SteamApps steamApps;

        private static bool isRunning;
        private static bool isDumpingDepotKeys;

        private static string user;
        private static string pass;
        private static string authCode;
        private static string twoFactorAuth;

        private static readonly Payload Payload = new Payload();

        public static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("This program will login into your Steam account using SteamKit,");
            Console.WriteLine("request all available apps on Steam,");
            Console.WriteLine("and then submit all received tokens to SteamDB.");
            Console.WriteLine(" ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All tokens and keys are GLOBAL, and are not unique to your account,");
            Console.WriteLine("SteamDB bot simply can't get them because it doesn't own these games");
            Console.WriteLine("Using this software will help SteamDB, thanks!");
            Console.WriteLine(" ");
            Console.ResetColor();

            Console.Write("Should we dump depot keys? This is slow. Type 'no' to skip: ");
            isDumpingDepotKeys = Console.ReadLine() != "no";

            Console.Write("Enter your Steam username: ");
            user = Console.ReadLine();

            if (string.IsNullOrEmpty(user))
            {
                Console.WriteLine("Will login as an anonymous account");
            }
            else
            {
                Console.Write("Enter your Steam password: ");
                pass = ReadPassword();
            }

            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);

            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

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

        // some shitty function from google
        private static string ReadPassword()
        {
            var password = "";
            var info = Console.ReadKey(true);

            while (info.Key != ConsoleKey.Enter)
            {
                if (info.Key != ConsoleKey.Backspace)
                {
                    Console.Write("*");
                    password += info.KeyChar;
                }
                else if (info.Key == ConsoleKey.Backspace)
                {
                    if (!string.IsNullOrEmpty(password))
                    {
                        password = password.Substring(0, password.Length - 1);
                        var pos = Console.CursorLeft;
                        Console.SetCursorPosition(pos - 1, Console.CursorTop);
                        Console.Write(" ");
                        Console.SetCursorPosition(pos - 1, Console.CursorTop);
                    }
                }

                info = Console.ReadKey(true);
            }

            Console.WriteLine();

            return password;
        }

        private static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to connect to Steam: {0}", callback.Result);

                isRunning = false;

                return;
            }

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

            Payload.SteamID = callback.ClientSteamID.ConvertToUInt64();

            await RequestTokens().ConfigureAwait(false);
        }

        private static async Task RequestTokens()
        {
            var alreadyTriedDepots = new HashSet<uint>();
            var appsToRequest = await GetLastKnownAppID();

            for (var i = 0; i <= appsToRequest; i += APPS_PER_REQUEST)
            {
                Console.WriteLine();
                Console.WriteLine($"Processing range {i + 1}...{i + APPS_PER_REQUEST}");

                SteamApps.PICSTokensCallback callback;

                try
                {
                    callback = await steamApps.PICSGetAccessTokens(Enumerable.Range(i + 1, APPS_PER_REQUEST).Select(appid => (uint)appid), Enumerable.Empty<uint>());
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    continue;
                }

                var nonZero = callback.AppTokens.Count(x => x.Value > 0);

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"Tokens granted: {callback.AppTokens.Count} - Denied: {callback.AppTokensDenied.Count} - Non-zero: {nonZero}");
                Console.ResetColor();

                var appInfoRequests = new List<SteamApps.PICSRequest>();

                foreach (var token in callback.AppTokens)
                {
                    if (token.Value > 0)
                    {
                        Payload.Apps.Add(token.Key, token.Value);
                    }

                    appInfoRequests.Add(new SteamApps.PICSRequest
                    {
                        ID = token.Key,
                        AccessToken = token.Value,
                        Public = false,
                    });
                }

                if (!isDumpingDepotKeys)
                {
                    continue;
                }

                try
                {
                    var tasks = new List<Task<SteamApps.DepotKeyCallback>>();

                    if (appInfoRequests.Count > 0)
                    {
                        var appInfo = await steamApps.PICSGetProductInfo(appInfoRequests, Enumerable.Empty<SteamApps.PICSRequest>());

                        foreach (var result in appInfo.Results)
                        {
                            foreach (var app in result.Apps.Values)
                            {
                                if (!alreadyTriedDepots.Contains(app.ID))
                                {
                                    alreadyTriedDepots.Add(app.ID);
                                    tasks.Add(steamApps.GetDepotDecryptionKey(app.ID, app.ID).ToTask());
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

                                        if (uint.TryParse(depot.Name, out var depotid) && !alreadyTriedDepots.Contains(depotid))
                                        {
                                            alreadyTriedDepots.Add(depotid);
                                            tasks.Add(steamApps.GetDepotDecryptionKey(depotid, app.ID).ToTask());
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (tasks.Count > 0)
                    {
                        await Task.WhenAll(tasks);

                        var depotKeysDenied = 0;
                        var depotKeysGranted = 0;

                        foreach (var task in tasks)
                        {
                            if (task.Result.Result == EResult.OK)
                            {
                                depotKeysGranted++;

                                Payload.Depots.Add(task.Result.DepotID, BitConverter.ToString(task.Result.DepotKey).Replace("-", ""));
                            }
                            else
                            {
                                depotKeysDenied++;
                            }
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Depot keys granted: {depotKeysGranted} - Denied: {depotKeysDenied}");
                        Console.ResetColor();
                    }
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(e);
                    Console.ResetColor();
                }
            }

            Console.WriteLine($"{Payload.Apps.Count} non-zero tokens");
            Console.WriteLine($"{Payload.Depots.Count} depot keys");

            await SendTokens(JsonConvert.SerializeObject(Payload));

            steamUser.LogOff();
        }

        private static async Task SendTokens(string postData)
        {
            Console.WriteLine(" ");
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write("Would you like to submit these tokens to SteamDB? Type 'yes' to submit: ");
            Console.ResetColor();

            if (Console.ReadLine() != "yes")
            {
                return;
            }

            Console.WriteLine("Submitting tokens to SteamDB...");

            try
            {
                using (var httpClient = new HttpClient())
                {
                    var content = new StringContent(postData, Encoding.UTF8, "application/json");
                    var result = await httpClient.PostAsync("https://steamdb.info/api/SubmitToken/", content);

                    Console.WriteLine(await result.Content.ReadAsStringAsync());
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Whoops: {0}", e.Message);
            }
        }

        private static async Task<int> GetLastKnownAppID()
        {
            using (var httpClient = new HttpClient())
            {
                var result = await httpClient.GetAsync("https://steamdb.info/api/SubmitToken/?getLastAppId");
                var content = await result.Content.ReadAsStringAsync();
                var appsToRequest = int.Parse(content);

                Console.WriteLine($"Last appid to request is {appsToRequest}");

                return appsToRequest;
            }
        }
    }
}
