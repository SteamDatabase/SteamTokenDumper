using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamTokens
{
    internal static class Program
    {
        private const int APPS_PER_REQUEST = 10_000;

        private static SteamClient steamClient;
        private static CallbackManager manager;

        private static SteamUser steamUser;
        private static SteamApps steamApps;

        private static bool isDisconnecting;
        private static bool isRunning;
        private static bool isDumpingDepotKeys;

        private static string user;
        private static string pass;
        private static string authCode;
        private static string twoFactorAuth;

        public static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("This program will login into your Steam account using SteamKit,");
            Console.WriteLine("request PICS access tokens for all available apps on Steam,");
            Console.WriteLine("and then submit all received tokens to SteamDB.");
            Console.WriteLine(" ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All tokens and keys are GLOBAL, and are not unique to your account,");
            Console.WriteLine("SteamDB bot simply can't get them because it doesn't own these games");
            Console.WriteLine("Using this software will help SteamDB, thanks!");
            Console.WriteLine(" ");
            Console.ResetColor();

            Console.Write("Should we dump depot keys? This is much slower. Type 'no' to skip: ");
            isDumpingDepotKeys = !Console.ReadLine().StartsWith("no");

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
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

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

            Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);

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
            if (isDisconnecting)
            {
                isRunning = false;

                Console.WriteLine("Exiting...");

                return;
            }

            Console.WriteLine("Disconnected from Steam, reconnecting in 5 seconds...");

            Thread.Sleep(TimeSpan.FromSeconds(5));

            steamClient.Connect();
        }

        private static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            if (isDisconnecting)
            {
                isRunning = false;

                Console.WriteLine("Exiting...");

                return;
            }

            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
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

            await RequestTokens().ConfigureAwait(false);
        }

        private static async Task RequestTokens()
        {
            var empty = Enumerable.Empty<uint>().ToList();
            var tokensString = new List<string>();
            var depotString = new List<string>();
            var alreadyTriedDepots = new HashSet<uint>();
            var appsToRequest = GetLastKnownAppID();

            for (uint i = 1; i <= appsToRequest; i += APPS_PER_REQUEST)
            {
                var apps = new List<uint>();
                SteamApps.PICSTokensCallback callback;

                for (var a = i; a < i + APPS_PER_REQUEST; a++)
                {
                    apps.Add(a);
                }

                try
                {
                    callback = await steamApps.PICSGetAccessTokens(apps, empty);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    continue;
                }

                Console.WriteLine($"Range {i}-{i+APPS_PER_REQUEST-1} - Tokens granted: {callback.AppTokens.Count} - Tokens denied: {callback.AppTokensDenied.Count}");

                Console.ForegroundColor = ConsoleColor.Blue;

                var appInfoRequests = new List<SteamApps.PICSRequest>();

                foreach (var token in callback.AppTokens)
                {
                    if (token.Value > 0)
                    {
                        Console.WriteLine("App: {0} - Token: {1}", token.Key, token.Value);

                        tokensString.Add($"\"{token.Key}\":\"{token.Value}\"");
                    }

                    appInfoRequests.Add(new SteamApps.PICSRequest
                    {
                        ID = token.Key,
                        AccessToken = token.Value,
                        Public = false,
                    });
                }

                Console.ResetColor();

                if (!isDumpingDepotKeys)
                {
                    continue;
                }

                try
                {
                    var tasks = new List<Task<SteamApps.DepotKeyCallback>>();

                    if (appInfoRequests.Count > 0)
                    {
                        Console.WriteLine($"Requesting app info for {appInfoRequests.Count} apps");

                        var appInfo = await steamApps.PICSGetProductInfo(appInfoRequests, Enumerable.Empty<SteamApps.PICSRequest>());

                        foreach (var result in appInfo.Results)
                        {
                            foreach (var app in result.Apps.Values)
                            {
                                alreadyTriedDepots.Add(app.ID);
                                tasks.Add(steamApps.GetDepotDecryptionKey(app.ID, app.ID).ToTask());

                                if (app.KeyValues["depots"] != null)
                                {
                                    foreach (var depot in app.KeyValues["depots"].Children)
                                    {
                                        var depotfromapp = depot["depotfromapp"].AsUnsignedInteger();

                                        if (uint.TryParse(depot.Name, out var depotid) && !alreadyTriedDepots.Contains(depotid) && depotfromapp != 1007 && depotfromapp != 228980)
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
                        Console.WriteLine($"Requesting depot keys for {tasks.Count} depots");

                        await Task.WhenAll(tasks);

                        var depotKeysCount = 0;

                        foreach (var task in tasks)
                        {
                            if (task.Result.Result == EResult.OK)
                            {
                                depotKeysCount++;

                                depotString.Add($"\"{task.Result.DepotID}\":\"{BitConverter.ToString(task.Result.DepotKey).Replace("-", "")}\"");
                            }
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Found {depotKeysCount} depot keys");
                        Console.ResetColor();
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                }
            }

            Console.WriteLine($"{tokensString.Count} non-zero tokens granted.");
            Console.WriteLine($"{depotString.Count} depot keys found.");

            if (tokensString.Count > 0)
            {
                SendTokens("{\"steamid\":\"" + steamUser.SteamID.ConvertToUInt64() + "\",\"apps\":{" + string.Join(",", tokensString) + "},\"depots\":{" + string.Join(",", depotString) + "}}");
            }

            isDisconnecting = true;

            steamUser.LogOff();
        }

        private static void SendTokens(string postData)
        {
            Console.WriteLine(" ");
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write("Would you like to submit these tokens to SteamDB? Type 'yes' to submit: ");
            Console.ResetColor();

            if (!Console.ReadLine().Equals("yes"))
            {
                return;
            }

            Console.WriteLine("Submitting tokens to SteamDB...");

            try
            {
                var rqst = (HttpWebRequest)WebRequest.Create("https://steamdb.info/api/SubmitToken/");

                rqst.Method = "POST";
                rqst.ContentType = "application/json";
                rqst.UserAgent = "SteamTokenDumper";
                rqst.KeepAlive = false;

                byte[] byteData = Encoding.UTF8.GetBytes(postData);
                rqst.ContentLength = byteData.Length;

                using (var postStream = rqst.GetRequestStream())
                {
                    postStream.Write(byteData, 0, byteData.Length);
                    postStream.Close();
                }

                string content;

                using (var webResponse = rqst.GetResponse())
                {
                    using (var responseStream = new StreamReader(webResponse.GetResponseStream()))
                    {
                        content = responseStream.ReadToEnd();
                        responseStream.Close();
                    }
                }

                Console.WriteLine("Submitted, thanks!");
                Console.WriteLine(content);
            }
            catch (Exception e)
            {
                Console.WriteLine("Whoops: {0}", e.Message);
            }
        }

        private static int GetLastKnownAppID()
        {
            try
            {
                var rqst = (HttpWebRequest)WebRequest.Create("https://steamdb.info/api/SubmitToken/?getLastAppId");
                rqst.UserAgent = "SteamTokenDumper";
                rqst.KeepAlive = false;

                string content;

                using (var webResponse = rqst.GetResponse())
                using (var responseStream = new StreamReader(webResponse.GetResponseStream()))
                {
                    content = responseStream.ReadToEnd();
                    responseStream.Close();
                }

                var appsToRequest = int.Parse(content);

                Console.WriteLine($"Last appid to request is {appsToRequest}");

                return appsToRequest;
            }
            catch (Exception e)
            {
                Console.WriteLine("Whoops: {0}", e.Message);
            }

            return 1_200_000;
        }
    }
}
