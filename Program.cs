﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using QRCoder;
using Spectre.Console;
using SteamKit2;
using SteamKit2.Authentication;

#pragma warning disable CA1031 // Do not catch general exception types
[assembly: CLSCompliant(false)]
namespace SteamTokenDumper;

internal static class Program
{
    private static byte[] EncryptionKey;

    private static SteamClient steamClient;
    private static CallbackManager manager;

    private static SteamUser steamUser;
    private static IDisposable LicenseListCallback;

    private static bool isRunning;
    private static bool isExiting;

    private static string pass;
    private static SavedCredentials savedCredentials = new();
    private static AuthSession authSession;
    private static int reconnectCount;

    private static readonly Configuration Configuration = new();
    private static readonly ApiClient ApiClient = new();
    private static readonly Payload Payload = new();
    private static KnownDepotIds KnownDepotIds;
    private static string RememberCredentialsFile;
    public static string AppPath { get; private set; }

    public static bool IsConnected => steamClient.IsConnected;
    public static TaskCompletionSource<bool> ReconnectEvent { get; private set; } = new();

    public static async Task Main()
    {
        WindowsDisableConsoleQuickEdit.Disable();

        Console.Title = "SteamDB Token Dumper";

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        // This is not secure.
        EncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(nameof(SteamTokenDumper), SteamClientData.GetMachineGuid())));

        AppPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
        RememberCredentialsFile = Path.Combine(AppPath, "SteamTokenDumper.credentials.bin");
        KnownDepotIds = new();

        try
        {
            var sentryHashFile = Path.Combine(AppPath, "SteamTokenDumper.sentryhash.bin");

            if (File.Exists(sentryHashFile))
            {
                AnsiConsole.WriteLine("Deleting stored credentials from previous token dumper version.");

                File.Delete(sentryHashFile);
                File.Delete(RememberCredentialsFile);
            }
        }
        catch
        {
            // don't care
        }
        
        AnsiConsole.Write(new FigletText("SteamDB").Color(Color.BlueViolet));

        AnsiConsole.Write(
            new Panel(new Markup("Read [link]https://steamdb.info/tokendumper/[/] before using this\n\nTake a look at the '[u]SteamTokenDumper.config.ini[/]' file for possible options.", new Style(Color.Blue)))
                .BorderColor(Color.BlueViolet)
                .RoundedBorder()
        );
        
        AnsiConsole.Write(
            new Panel(new Text("If you are in a closed or limited beta, have a non disclosure agreement,\nor otherwise do not want to leak private information, do not use this program.", new Style(Color.Yellow)))
                .BorderColor(Color.GreenYellow)
                .RoundedBorder()
        );

        AnsiConsole.WriteLine();

        await ReadConfiguration();

        AnsiConsole.WriteLine();

        if (Configuration.UserConsentBeforeRun && !AnsiConsole.Confirm("Are you sure you want to continue?", false))
        {
            AnsiConsole.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        if (!await ApiClient.IsUpToDate())
        {
            AnsiConsole.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        await KnownDepotIds.Load(ApiClient);

        if (!Configuration.SkipAutoGrant)
        {
            AnsiConsole.WriteLine();

            try
            {
                SteamClientData.ReadFromSteamClient(Payload, KnownDepotIds);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                await Console.Error.WriteLineAsync(e.ToString());
                Console.ResetColor();
            }
        }

        Console.WriteLine();

        if (savedCredentials.RefreshToken != null)
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
            Console.WriteLine();
            Console.WriteLine("Enter \"qr\" into the username field if you would like to scan a QR code with your Steam mobile app.");
            Console.ResetColor();
            Console.WriteLine();

            savedCredentials.Username = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam username:")
            {
                AllowEmpty = true
            });

            if (string.IsNullOrEmpty(savedCredentials.Username))
            {
                Console.WriteLine("Doing an anonymous dump.");

                Payload.SteamID = new SteamID((uint)Random.Shared.Next(), EUniverse.Public, EAccountType.AnonUser).Render();

                await ApiClient.SendTokens(Payload, Configuration);
            }
            else if (savedCredentials.Username != "anonymous" && savedCredentials.Username != "qr")
            {
                pass = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam password:")
                {
                    IsSecret = true
                });

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

    private static async Task ReadConfiguration()
    {
        try
        {
            await Configuration.Load();
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Failed to read config: {e.Message}");
            Console.ResetColor();
        }

        try
        {
            await ReadCredentials();
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Failed to read stored credentials: {e}");
            Console.ResetColor();
        }
    }

    private static async Task ReadCredentials()
    {
        if (!Configuration.RememberLogin)
        {
            return;
        }

        if (!File.Exists(RememberCredentialsFile))
        {
            return;
        }

        var encryptedBytes = await File.ReadAllBytesAsync(RememberCredentialsFile);
        var decryptedData = CryptoHelper.SymmetricDecrypt(encryptedBytes, EncryptionKey);

        savedCredentials = JsonSerializer.Deserialize(decryptedData, SavedCredentialsJsonContext.Default.SavedCredentials);

        if (savedCredentials.Version != SavedCredentials.CurrentVersion)
        {
            savedCredentials = new();
            throw new InvalidDataException($"Got incorrect saved credentials version.");
        }
    }

    private static async Task SaveCredentials()
    {
        if (!Configuration.RememberLogin)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(savedCredentials, new SavedCredentialsJsonContext(new JsonSerializerOptions
            {
                WriteIndented = true,
            }).SavedCredentials);

            var encryptedData = CryptoHelper.SymmetricEncrypt(json, EncryptionKey);

            await File.WriteAllBytesAsync(RememberCredentialsFile, encryptedData);
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Failed to read config: {e}");
            Console.ResetColor();
        }
    }

    private static void ReadCredentialsAgain()
    {
        Console.WriteLine();
        Console.WriteLine("Enter \"qr\" into the username field if you would like to scan a QR code with your Steam mobile app.");
        Console.WriteLine();

        savedCredentials.Username = AnsiConsole.Ask<string>("Enter your Steam username:");

        if (savedCredentials.Username == "anonymous" || savedCredentials.Username == "qr")
        {
            return;
        }

        pass = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam password:")
        {
            IsSecret = true
        });
    }

    private static void InitializeSteamKit()
    {
        DebugLog.AddListener(new SteamKitLogger());
        DebugLog.Enabled = Configuration.Debug;

        steamClient = new SteamClient("Dumper");
        manager = new CallbackManager(steamClient);

        steamUser = steamClient.GetHandler<SteamUser>();

        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        LicenseListCallback = manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

        isRunning = true;

        Console.WriteLine("Connecting to Steam...");

        steamClient.Connect();

        while (isRunning)
        {
            manager.RunWaitAllCallbacks(TimeSpan.FromSeconds(2));
        }
    }

    private static async void OnConnected(SteamClient.ConnectedCallback callback)
    {
        Console.WriteLine("Connected to Steam!");

        if (savedCredentials.Username == "anonymous")
        {
            steamUser.LogOnAnonymous();
            return;
        }

        if (authSession == null)
        {
            if (savedCredentials.Username == "qr")
            {
                var qrAuthSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                {
                    DeviceFriendlyName = nameof(SteamTokenDumper),
                });

                qrAuthSession.ChallengeURLChanged = () => DrawQRCode(qrAuthSession, true);

                DrawQRCode(qrAuthSession);

                authSession = qrAuthSession;
            }
            else if (savedCredentials.RefreshToken == null)
            {
                authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Password = pass,
                    Username = savedCredentials.Username,
                    IsPersistentSession = Configuration.RememberLogin,
                    DeviceFriendlyName = nameof(SteamTokenDumper),
                    Authenticator = new UserConsoleAuthenticator(),
                });
            }
        }

        if (authSession != null)
        {
            try
            {
                var pollResponse = await authSession.PollingWaitForResultAsync();

                savedCredentials.Username = pollResponse.AccountName;
                savedCredentials.RefreshToken = pollResponse.RefreshToken;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Previous authentication polling was cancelled.");
                return;
            }

            await SaveCredentials();
        }

        authSession = null;
        pass = null; // Password should not be needed

        Console.WriteLine("Logging in...");

        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            LoginID = 0x44_55_4D_50, // "DUMP"
            Username = savedCredentials.Username,
            Password = savedCredentials.RefreshToken == null ? pass : null,
            AccessToken = savedCredentials.RefreshToken,
            ShouldRememberPassword = Configuration.RememberLogin,
        });
    }

    private static void DrawQRCode(QrAuthSession authSession, bool rewrite = false)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
        using var qrCode = new AsciiQRCode(qrCodeData);
        var qrCodeAsAsciiArt = qrCode.GetLineByLineGraphic(1, drawQuietZones: false);

        Console.WriteLine();

        if (rewrite)
        {
            Console.WriteLine("QR code was refreshed, ignore the previous one.");
        }
        else
        {
            Console.WriteLine("Use the Steam Mobile App to sign in via QR code:");
        }

        var emptyLine = new string(' ', qrCodeAsAsciiArt[0].Length + 8);

        Console.BackgroundColor = ConsoleColor.White;
        Console.ForegroundColor = ConsoleColor.Black;
        Console.Write(emptyLine);
        Console.ResetColor();
        Console.WriteLine();

        foreach (var line in qrCodeAsAsciiArt)
        {
            Console.BackgroundColor = ConsoleColor.White;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Write("    ");
            Console.Write(line);
            Console.Write("    ");
            Console.ResetColor();
            Console.WriteLine();
        }

        Console.BackgroundColor = ConsoleColor.White;
        Console.ForegroundColor = ConsoleColor.Black;
        Console.Write(emptyLine);
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        Console.WriteLine(); // When disconnected after ConsoleRewriteLine

        if (isExiting)
        {
            isRunning = false;

            Console.WriteLine("Disconnected from Steam, exiting...");

            return;
        }

        var sleep = (1 << reconnectCount) * 1000;
        reconnectCount++;

        if (sleep < 5_000)
        {
            sleep = 5_000;
        }
        else if (sleep > 120_000)
        {
            sleep = 120_000;
        }

        sleep += Random.Shared.Next(1001);

        Console.WriteLine($"Disconnected from Steam, reconnecting in {sleep / 1000} seconds...");

        Thread.Sleep(sleep);

        steamClient.Connect();
    }

    private static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            if (callback.Result is EResult.ServiceUnavailable or EResult.TryAnotherCM)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Steam is currently having issues ({callback.Result})...");
                Console.ResetColor();
            }
            else if (callback.Result == EResult.InvalidPassword
            || callback.Result == EResult.InvalidSignature
            || callback.Result == EResult.AccessDenied
            || callback.Result == EResult.Expired
            || callback.Result == EResult.Revoked)
            {
                reconnectCount = 0;

                Console.ForegroundColor = ConsoleColor.Red;

                if (savedCredentials.RefreshToken != null)
                {
                    savedCredentials.RefreshToken = null;

                    Console.WriteLine($"Stored credentials are invalid. ({callback.Result})");

                    Task.Run(SaveCredentials);
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
                isExiting = true;
            }

            return;
        }

        reconnectCount = 0;

        if (LicenseListCallback == null)
        {
            Console.WriteLine("Logged on, continuing...");

            ReconnectEvent.SetResult(true);
            ReconnectEvent = new();

            return;
        }

        Task.Run(RenewRefreshTokenIfRequired);

        var steamid = callback.ClientSteamID ?? new SteamID(0, EUniverse.Public, EAccountType.Invalid);
        Payload.SteamID = steamid.Render();

        if (steamid.AccountType == EAccountType.AnonUser)
        {
            isExiting = true; // No reconnect support for anonymous accounts

            Console.WriteLine("Logged on, requesting package for anonymous users...");

            const uint ANONYMOUS_PACKAGE = 17906;

            var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>(), KnownDepotIds, Configuration);

            Task.Run(async () =>
            {
                var tokenJob = await steamClient.GetHandler<SteamApps>().PICSGetAccessTokens(null, ANONYMOUS_PACKAGE);
                tokenJob.PackageTokens.TryGetValue(ANONYMOUS_PACKAGE, out var token);

                await DoRequest(requester,
                [
                    new(ANONYMOUS_PACKAGE, token)
                ]);
            });
        }
        else
        {
            Console.WriteLine("Logged on, waiting for licenses...");
        }
    }

    private static async Task RenewRefreshTokenIfRequired()
    {
        if (!Configuration.RememberLogin || savedCredentials.RefreshToken == null)
        {
            return;
        }

        try
        {
            var token = new JsonWebToken(savedCredentials.RefreshToken);

            Console.WriteLine($"Refresh token is valid to {token.ValidTo:yyyy-MM-dd HH:mm:ss}");

            if (DateTime.UtcNow.Add(TimeSpan.FromDays(30)) >= token.ValidTo)
            {
                var newToken = await steamClient.Authentication.GenerateAccessTokenForAppAsync(steamClient.SteamID, savedCredentials.RefreshToken, allowRenewal: true);

                if (!string.IsNullOrEmpty(newToken.RefreshToken))
                {
                    Console.WriteLine("Renewed the refresh token");

                    savedCredentials.RefreshToken = newToken.RefreshToken;

                    await SaveCredentials();
                }
            }
        }
        catch (Exception)
        {
            //
        }
    }

    private static void OnLicenseList(SteamApps.LicenseListCallback licenseList)
    {
        LicenseListCallback.Dispose();
        LicenseListCallback = null;

        var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>(), KnownDepotIds, Configuration);
        var packages = requester.ProcessLicenseList(licenseList);

        Task.Factory.StartNew(
            async () => await DoRequest(requester, packages),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private static async Task DoRequest(Requester requester, List<SteamApps.PICSRequest> packages)
    {
        await requester.ProcessPackages(packages);

        var success = await ApiClient.SendTokens(Payload, Configuration);

        if (success)
        {
            await KnownDepotIds.SaveKnownDepotIds();
        }

        isExiting = true;

        steamUser.LogOff();
    }
}
