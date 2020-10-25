using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper
{
    internal static class Program
    {
        private static SteamClient steamClient;
        private static CallbackManager manager;

        private static SteamUser steamUser;
        private static IDisposable LicenseListCallback;

        private static bool isRunning;

        private static string user;
        private static string pass;
        private static string authCode;
        private static string twoFactorAuth;

        private static readonly ApiClient ApiClient = new ApiClient();
        private static readonly Payload Payload = new Payload();
        private static string SentryHashFile;
        public static string AppPath { get; private set; }

        public static async Task Main()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("[>] Read https://steamdb.info/tokendumper/ before using this");
            Console.ResetColor();
            Console.WriteLine();

            AppPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            SentryHashFile = Path.Combine(AppPath, "SteamTokenDumper.sentryhash.bin");

            await ApiClient.CheckVersion();

            Console.Write("Enter your Steam username: ");
            user = ReadUserInput(true);

            if (string.IsNullOrEmpty(user))
            {
                Console.Write("Doing an anonymous dump. ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("We recommend logging in for a thorough dump.");
                Console.ResetColor();

                var random = new Random();
                Payload.SteamID = new SteamID((uint)random.Next(), EUniverse.Public, EAccountType.AnonUser).Render();
                
                await ApiClient.SendTokens(Payload);
            }
            else
            {
                do
                {
                    Console.Write("Enter your Steam password: ");
                    pass = ReadUserInput();
                }
                while (string.IsNullOrEmpty(pass));

                await InitializeSteamKit();
            }

            ApiClient.Dispose();

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static async Task InitializeSteamKit()
        {
            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);

            steamUser = steamClient.GetHandler<SteamUser>();

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
                manager.RunWaitAllCallbacks(TimeSpan.FromSeconds(5));
            }
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

            byte[] sentryFileHash = null;

            try
            {
                if (File.Exists(SentryHashFile))
                {
                    sentryFileHash = File.ReadAllBytes(SentryHashFile);
                }
            }
            catch
            {
                // If for whatever reason we can't read the sentry
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                LoginID = 1337,
                Username = user,
                Password = pass,
                AuthCode = authCode,
                TwoFactorCode = twoFactorAuth,
                SentryFileHash = sentryFileHash,
            });
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
            var isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            var is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

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

            try
            {
                File.WriteAllBytes(SentryHashFile, sentryHash);
            }
            catch
            {
                return;
            }

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

            var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>());
            await requester.ProcessLicenseList(licenseList);

            await ApiClient.SendTokens(Payload);

            steamUser.LogOff();
        }
    }
}
