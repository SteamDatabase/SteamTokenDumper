using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using SteamKit2;

namespace SteamTokens
{
    class Program
    {
        static SteamClient steamClient;
        static CallbackManager manager;

        static SteamUser steamUser;
        static SteamApps steamApps;

        static bool isDisconnecting;
        static bool isRunning;

        static string user, pass;
        static string authCode, twoFactorAuth;

        static void Main()
        {
            Console.WriteLine("This program will login into your Steam account using SteamKit,");
            Console.WriteLine("request PICS access tokens for all available apps on Steam,");
            Console.WriteLine("and then submit all received tokens to SteamDB.");
            Console.WriteLine(" ");
            Console.WriteLine("All tokens are GLOBAL, and are not unique to your account,");
            Console.WriteLine("SteamDB bot simply can't get them because it doesn't own these games");
            Console.WriteLine("Using this software will help SteamDB, thanks!");
            Console.WriteLine(" ");
            Console.WriteLine("A sentry (SteamGuard) file will be saved locally, but your");
            Console.WriteLine("username and password will not be stored. This software uses");
            Console.WriteLine("SteamKit2 to perform actions on the Steam network.");
            Console.WriteLine(" ");

            Console.Write("Enter your Steam username: ");
            user = Console.ReadLine();
            Console.Write("Enter your Steam password: ");
            pass = ReadPassword();

            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);

            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();

            new Callback<SteamClient.ConnectedCallback>(OnConnected, manager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, manager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, manager);
            new Callback<SteamUser.LoggedOffCallback>(OnLoggedOff, manager);
            new Callback<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth, manager);
            new Callback<SteamApps.PICSChangesCallback>(OnPICSChanges, manager);
            new Callback<SteamApps.PICSTokensCallback>(OnPICSTokens, manager);

            isRunning = true;

            Console.WriteLine("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        // some shitty function from google
        static string ReadPassword()
        {
            string password = "";
            ConsoleKeyInfo info = Console.ReadKey(true);
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
                        int pos = Console.CursorLeft;
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

        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to connect to Steam: {0}", callback.Result);

                isRunning = false;

                return;
            }

            Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);

            byte[] sentryHash = null;
            if (File.Exists("sentry_" + user + ".bin"))
            {
                byte[] sentryFile = File.ReadAllBytes("sentry_" + user + ".bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = user,
                    Password = pass,
                    AuthCode = authCode,
                    TwoFactorCode = twoFactorAuth,
                    SentryFileHash = sentryHash,
                });
        }

        static void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Console.WriteLine("Updating sentryfile so that you don't need to authenticate with SteamGuard next time.");

            byte[] sentryHash = CryptoHelper.SHAHash(callback.Data);

            File.WriteAllBytes("sentry_" + user + ".bin", callback.Data);

            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
                {
                    JobID = callback.JobID,

                    FileName = callback.FileName,

                    BytesWritten = callback.BytesToWrite,
                    FileSize = callback.Data.Length,
                    Offset = callback.Offset,

                    Result = EResult.OK,
                    LastError = 0,

                    OneTimePassword = callback.OneTimePassword,

                    SentryFileHash = sentryHash,
                });
        }

        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (isDisconnecting)
            {
                isRunning = false;

                Console.WriteLine("Exiting...");

                return;
            }

            Console.WriteLine("Disconnected from Steam, reconnecting in 5...");

            Thread.Sleep(TimeSpan.FromSeconds(5));

            steamClient.Connect();
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
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

            Console.WriteLine("Successfully logged on, requesting changelist #1");

            steamApps.PICSGetChangesSince(1, true, false);
        }

        static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        static void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            Console.WriteLine("Got {0} apps", callback.AppChanges.Count);

            steamApps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>());
        }

        static void OnPICSTokens(SteamApps.PICSTokensCallback callback)
        {
            var tokens = callback.AppTokens.Where(app => app.Value > 0).ToList();
            string postData = "steamid=" + steamUser.SteamID.ConvertToUInt64() + "&";

            Console.WriteLine(" ");
            Console.WriteLine("Tokens granted: {0} ({1} non-zero tokens) - Tokens denied: {2}", callback.AppTokens.Count, tokens.Count(), callback.AppTokensDenied.Count);

            foreach (var token in tokens)
            {
                Console.WriteLine("App: {0} - Token: {1}", token.Key, token.Value);

                postData += "apps[]=" + token.Key + "_" + token.Value + "&";
            }

            if (tokens.Any())
            {
                Console.WriteLine(" ");
                Console.Write("Would you like to submit these tokens to SteamDB? Type 'yes' to submit: ");

                if (Console.ReadLine().Equals("yes"))
                {
                    Console.WriteLine("Submitting tokens to SteamDB...");

                    try
                    {
                        var rqst = (HttpWebRequest)WebRequest.Create("https://steamdb.info/api/SubmitToken/");

                        rqst.Method = "POST";
                        rqst.ContentType = "application/x-www-form-urlencoded";
                        rqst.UserAgent = "SteamTokenDumper";
                        rqst.KeepAlive = false;

                        byte[] byteData = Encoding.UTF8.GetBytes(postData);
                        rqst.ContentLength = byteData.Length;

                        using (var postStream = rqst.GetRequestStream())
                        {
                            postStream.Write(byteData, 0, byteData.Length);
                            postStream.Close();
                        }

                        using (var webResponse = rqst.GetResponse())
                        {
                            using (var responseStream = new StreamReader(webResponse.GetResponseStream()))
                            {
                                responseStream.ReadToEnd();
                                responseStream.Close();
                            }
                        }

                        Console.WriteLine("Submitted, thanks!");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Whoops: {0}", e.Message);
                    }
                }
            }

            isDisconnecting = true;

            steamUser.LogOff();
            steamClient.Disconnect();
        }
    }
}
