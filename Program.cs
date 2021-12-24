using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2;

#pragma warning disable CA1031 // Do not catch general exception types
[assembly: CLSCompliant(false)]
namespace SteamTokenDumper;

internal static class Program
{
    private static SteamClient steamClient;
    private static CallbackManager manager;

    private static SteamUser steamUser;
    private static IDisposable LicenseListCallback;

    private static bool isRunning;

    private static string user;
    private static string pass;
    private static string loginKey;
    private static string authCode;
    private static string twoFactorAuth;

    private static readonly Configuration Configuration = new();
    private static readonly ApiClient ApiClient = new();
    private static readonly Payload Payload = new();
    private static string SentryHashFile;
    private static string RememberCredentialsFile;
    public static string AppPath { get; private set; }

    public static async Task Main()
    {
        WindowsDisableConsoleQuickEdit.Disable();

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        Console.Title = "Steam token dumper for SteamDB";

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("[>] Read https://steamdb.info/tokendumper/ before using this");
        Console.WriteLine();
        Console.BackgroundColor = ConsoleColor.Yellow;
        Console.ForegroundColor = ConsoleColor.Black;
        Console.WriteLine("[>] If you are in a closed or limited beta, have a non disclosure agreement, ");
        Console.WriteLine("[>] or otherwise do not want to leak private information, do not use this program.");
        Console.ResetColor();
        Console.WriteLine();

        if (!CheckUserContinue())
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        AppPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
        SentryHashFile = Path.Combine(AppPath, "SteamTokenDumper.sentryhash.bin");
        RememberCredentialsFile = Path.Combine(AppPath, "SteamTokenDumper.credentials.bin");

        if (!await ApiClient.IsUpToDate())
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("Take a look at the 'SteamTokenDumper.config.ini' file for possible options.");

        try
        {
            await Configuration.Load();

            if (Configuration.RememberLogin && File.Exists(RememberCredentialsFile))
            {
                var credentials = (await File.ReadAllTextAsync(RememberCredentialsFile)).Split(';', 2);
                user = credentials[0];
                loginKey = credentials[1];
            }
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Failed to read config: {e.Message}");
            Console.ResetColor();
        }

        if (!Configuration.SkipAutoGrant)
        {
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
        }

        Console.WriteLine();

        if (loginKey != null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Logging in using previously remembered login. Delete '{Path.GetFileName(RememberCredentialsFile)}' file if you want it forgotten.");
            Console.ResetColor();
            Console.WriteLine();

            InitializeSteamKit();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Logging in means this program can do a thorough dump,");
            Console.WriteLine("as getting tokens from Steam files only works for installed games.");
            Console.ResetColor();
            Console.WriteLine();

            Console.Write("Enter your Steam username: ");
            user = ReadUserInput(true);

            if (string.IsNullOrEmpty(user))
            {
                Console.Write("Doing an anonymous dump.");

                var random = new Random();
                Payload.SteamID = new SteamID((uint)random.Next(), EUniverse.Public, EAccountType.AnonUser).Render();

                await ApiClient.SendTokens(Payload, Configuration);
            }
            else if (user != "anonymous")
            {
                do
                {
                    Console.Write("Enter your Steam password: ");
                    pass = ReadUserInput();
                } while (string.IsNullOrEmpty(pass));

                InitializeSteamKit();
            }
            else
            {
                InitializeSteamKit();
            }
        }

        // Read any buffered keys so it doesn't auto exit
        while (Console.KeyAvailable)
        {
            Console.ReadKey(true);
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private static bool CheckUserContinue()
    {
        bool accepted;
        bool cancel;

        do
        {
            Console.Write("Are you sure you want to continue? [y/N] ");
            var response = Console.ReadLine();

            cancel = string.IsNullOrEmpty(response) || char.ToUpperInvariant(response[0]) == 'N';
            accepted = !cancel && char.ToUpperInvariant(response[0]) == 'Y';
        }
        while (!cancel && !accepted);

        return accepted;
    }

    private static void ReadCredentialsAgain()
    {
        do
        {
            Console.Write("Enter your Steam username: ");
            user = ReadUserInput(true);
        } while (string.IsNullOrEmpty(user));

        do
        {
            Console.Write("Enter your Steam password: ");
            pass = ReadUserInput();
        } while (string.IsNullOrEmpty(pass));
    }

    private static void InitializeSteamKit()
    {
        DebugLog.AddListener(new SteamKitLogger());
        DebugLog.Enabled = Configuration.Debug;

#if DEBUG
        DebugLog.Enabled = true;
#endif

        steamClient = new SteamClient("Dumper");
        manager = new CallbackManager(steamClient);

        steamUser = steamClient.GetHandler<SteamUser>();

        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

        if (Configuration.RememberLogin)
        {
            manager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
        }

        LicenseListCallback = manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

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

        if (user == "anonymous")
        {
            steamUser.LogOnAnonymous();
            return;
        }

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
            LoginKey = loginKey,
            AuthCode = authCode,
            TwoFactorCode = twoFactorAuth,
            SentryFileHash = sentryFileHash,
            ShouldRememberPassword = Configuration.RememberLogin,
        });
    }

    private static async void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (Payload.SteamID != null)
        {
            isRunning = false;

            Console.WriteLine("Exiting...");

            return;
        }

        Console.WriteLine("Disconnected from Steam, reconnecting in five seconds...");

        await Task.Delay(5000);

        steamClient.Connect();
    }

    private static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        var isSteamGuard = callback.Result == EResult.AccountLogonDenied;
        var is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

        if (isSteamGuard || is2FA)
        {
            Console.WriteLine("This account is SteamGuard protected!");

            if (is2FA)
            {
                Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                twoFactorAuth = Console.ReadLine()?.Trim();
            }
            else
            {
                Console.Write($"Please enter the auth code sent to the email at {callback.EmailDomain}");
                authCode = Console.ReadLine()?.Trim();
            }

            return;
        }

        if (callback.Result != EResult.OK)
        {
            if (callback.Result is EResult.ServiceUnavailable or EResult.TryAnotherCM)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Steam is currently having issues ({callback.Result})...");
                Console.ResetColor();
            }
            else if (callback.Result == EResult.TwoFactorCodeMismatch)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("You have entered an invalid two factor code.");
                Console.ResetColor();

                if (twoFactorAuth != null)
                {
                    Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = Console.ReadLine()?.Trim();
                }
                else
                {
                    Console.Write("Please enter the auth code sent to your email: ");
                    authCode = Console.ReadLine()?.Trim();
                }
            }
            else if (callback.Result == EResult.InvalidPassword)
            {
                Console.ForegroundColor = ConsoleColor.Red;

                if (Configuration.RememberLogin && loginKey != null)
                {
                    loginKey = null;

                    Console.WriteLine("Stored credentials are invalid, credentials file has been deleted.");

                    try
                    {
                        File.Delete(RememberCredentialsFile);
                    }
                    catch
                    {
                        // who cares
                    }
                }
                else
                {
                    Console.WriteLine("You have entered an invalid username or password.");
                }

                Console.ResetColor();

                ReadCredentialsAgain();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Unable to logon to Steam: {callback.Result} ({callback.ExtendedResult})");
                Console.ResetColor();

                isRunning = false;
            }

            return;
        }

        if (!Configuration.RememberLogin)
        {
            user = null;
        }

        pass = null;
        loginKey = null;
        authCode = null;
        twoFactorAuth = null;

        var steamid = callback.ClientSteamID ?? new SteamID(0, EUniverse.Public, EAccountType.Invalid);
        Payload.SteamID = steamid.Render();

        if (steamid.AccountType == EAccountType.AnonUser)
        {
            Console.WriteLine("Logged on, requesting package for anonymous users...");

            const uint ANONYMOUS_PACKAGE = 17906;

            var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>(), Configuration);

            Task.Run(async () =>
            {
                var tokenJob = await steamClient.GetHandler<SteamApps>().PICSGetAccessTokens(null, ANONYMOUS_PACKAGE);
                tokenJob.PackageTokens.TryGetValue(ANONYMOUS_PACKAGE, out var token);

                await requester.ProcessPackages(new List<SteamApps.PICSRequest>
                {
                        new SteamApps.PICSRequest(ANONYMOUS_PACKAGE, token)
                });

                await ApiClient.SendTokens(Payload, Configuration);

                steamUser.LogOff();
            });
        }
        else
        {
            Console.WriteLine("Logged on, waiting for licenses...");
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

            using var sha = SHA1.Create();
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

    private static async void OnLoginKey(SteamUser.LoginKeyCallback callback)
    {
        await File.WriteAllTextAsync(RememberCredentialsFile, $"{user};{callback.LoginKey}");

        user = null;

        steamUser.AcceptNewLoginKey(callback);
    }

    private static void OnLicenseList(SteamApps.LicenseListCallback licenseList)
    {
        LicenseListCallback.Dispose();

        Task.Run(async () =>
        {
            var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>(), Configuration);
            var packages = requester.ProcessLicenseList(licenseList);
            await requester.ProcessPackages(packages);

            await ApiClient.SendTokens(Payload, Configuration);

            steamUser.LogOff();
        });
    }
}
