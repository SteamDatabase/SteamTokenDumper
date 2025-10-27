using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using QRCoder;
using Spectre.Console;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamTokenDumper;

internal sealed class Program : IDisposable
{
    private readonly SteamClient steamClient;
    private readonly CallbackManager manager;
    private readonly SteamUser steamUser;

    private bool licenseListReceived;
    private bool isAskingForInput;
    private bool suggestedQrCode;
    private readonly CancellationTokenSource ExitToken = new();

    private string? pass;
    private SavedCredentials savedCredentials = new();
    private AuthSession? authSession;
    private int reconnectCount;

    private readonly ApiClient ApiClient = new();
    private readonly Payload Payload = new();
    private readonly KnownDepotIds KnownDepotIds = new();
    private readonly string RememberCredentialsFile;

    public readonly Configuration Configuration = new();
    public bool IsConnected => steamClient.IsConnected;
    public TaskCompletionSource<bool> ReconnectEvent { get; private set; } = new();

    public Program()
    {
        DebugLog.AddListener(new SteamKitLogger());
        DebugLog.Enabled = Configuration.Debug;

        var config = SteamConfiguration.Create(b => b
            .WithProtocolTypes(ProtocolTypes.WebSocket)
        );

        steamClient = new SteamClient(config, "Dumper");
        manager = new CallbackManager(steamClient);
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

        steamUser = steamClient.GetHandler<SteamUser>()!;

        RememberCredentialsFile = Path.Combine(Application.AppPath, "SteamTokenDumper.credentials.bin");
    }

    public void Dispose()
    {
        ExitToken.Dispose();
        ApiClient.Dispose();
    }

    public async Task RunAsync()
    {
        try
        {
            var sentryHashFile = Path.Combine(Application.AppPath, "SteamTokenDumper.sentryhash.bin");

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
            new Panel(new Text("If you are in a closed or limited beta, have a non-disclosure agreement,\nor otherwise do not want to leak private information, do not use this program.", new Style(Color.Yellow)))
                .BorderColor(Color.GreenYellow)
                .RoundedBorder()
        );

        AnsiConsole.WriteLine();

        await ReadConfiguration();

        AnsiConsole.WriteLine();

        if (Configuration.UserConsentBeforeRun)
        {
            SinkUnreadKeys();

            if (!await AnsiConsole.ConfirmAsync("Are you sure you want to continue?", false))
            {
                AnsiConsole.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }
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
                AnsiConsole.Write(
                    new Panel(new Text($"Failed to read Steam client data: {e}", new Style(Color.Red)))
                        .BorderColor(Color.Red)
                        .RoundedBorder()
                );
            }
        }

        AnsiConsole.WriteLine();

        if (savedCredentials.RefreshToken != null)
        {
            AnsiConsole.Write(
                new Panel(new Markup($"Logging in using previously remembered login.\nDelete '[u]{Markup.Escape(Path.GetFileName(RememberCredentialsFile))}[/]' file if you want it forgotten.", new Style(Color.Green)))
                    .BorderColor(Color.Green)
                    .RoundedBorder()
            );

            AnsiConsole.WriteLine();

            await SteamLoop();
        }
        else
        {
            suggestedQrCode = true;

            AnsiConsole.Write(
                new Panel(new Markup($"Logging in means this program can do a thorough dump, as getting tokens from Steam files only works for installed games.\n\nEnter \"[bold]qr[/]\" into the username field if you would like to scan a QR code with your Steam mobile app.", new Style(Color.Green)))
                    .BorderColor(Color.Green)
                    .RoundedBorder()
            );

            AnsiConsole.WriteLine();

            SinkUnreadKeys();

            savedCredentials.Username = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam username:")
            {
                AllowEmpty = true
            });

            if (string.IsNullOrEmpty(savedCredentials.Username))
            {
                AnsiConsole.WriteLine("Doing an anonymous dump.");

                Payload.SteamID = new SteamID((uint)Random.Shared.Next(), EUniverse.Public, EAccountType.AnonUser).Render();

                await ApiClient.SendTokens(Payload, Configuration);
            }
            else if (savedCredentials.Username is not "anonymous" and not "qr")
            {
                pass = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam password:")
                {
                    IsSecret = true
                });

                await SteamLoop();
            }
            else
            {
                await SteamLoop();
            }
        }

        SinkUnreadKeys();

        AnsiConsole.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private async Task ReadConfiguration()
    {
        try
        {
            await Configuration.Load();
        }
        catch (FileNotFoundException e)
        {
            AnsiConsole.Write(
                new Panel(new Text(e.Message, new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to read config: {e}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }

        try
        {
            await ReadCredentials();
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to read stored credentials: {e}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
    }

    private async Task ReadCredentials()
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
        var decryptedData = CryptoHelper.SymmetricDecrypt(encryptedBytes);

        var parsedSaved = JsonSerializer.Deserialize(decryptedData, SavedCredentialsJsonContext.Default.SavedCredentials);

        if (parsedSaved?.Version != SavedCredentials.CurrentVersion)
        {
            savedCredentials = new();
            throw new InvalidDataException($"Got incorrect saved credentials version.");
        }

        savedCredentials = parsedSaved;
    }

    private async Task SaveCredentials()
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

            var encryptedData = CryptoHelper.SymmetricEncrypt(json);

            await File.WriteAllBytesAsync(RememberCredentialsFile, encryptedData);
        }
        catch (Exception e)
        {
            AnsiConsole.Write(
                new Panel(new Text($"Failed to save credentials: {e}", new Style(Color.Red)))
                    .BorderColor(Color.Red)
                    .RoundedBorder()
            );
        }
    }

    private void ReadCredentialsAgain()
    {
        isAskingForInput = true;

        steamClient.Disconnect();

        if (!suggestedQrCode)
        {
            suggestedQrCode = true;

            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("Enter \"qr\" into the username field if you would like to scan a QR code with your Steam mobile app.");
        }

        AnsiConsole.WriteLine();

        SinkUnreadKeys();

        savedCredentials.Username = AnsiConsole.Ask("Enter your Steam username:", savedCredentials.Username ?? string.Empty);

        if (savedCredentials.Username is not "anonymous" and not "qr")
        {
            pass = AnsiConsole.Prompt(new TextPrompt<string>("Enter your Steam password:")
            {
                IsSecret = true
            });
        }

        isAskingForInput = false;
        steamClient.Connect();
    }

    private async Task SteamLoop()
    {
        AnsiConsole.WriteLine("Connecting to Steam...");

        steamClient.Connect();

        try
        {
            while (!ExitToken.IsCancellationRequested)
            {
                await manager.RunWaitCallbackAsync(ExitToken.Token);
            }
        }
        catch (OperationCanceledException)
        {
            //
        }
    }

    private async void OnConnected(SteamClient.ConnectedCallback callback)
    {
        AnsiConsole.WriteLine("Connected to Steam");

        if (savedCredentials.Username == "anonymous")
        {
            steamUser.LogOnAnonymous();
            return;
        }

        if (authSession == null)
        {
            try
            {
                if (savedCredentials.Username == "qr")
                {
                    var qrAuthSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                    {
                        DeviceFriendlyName = nameof(SteamTokenDumper),
                    });

                    qrAuthSession.ChallengeURLChanged = () => DrawQRCode(qrAuthSession);

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
                        Authenticator = new ConsoleAuthenticator(Configuration.LoginSkipAppConfirmation),
                    });
                }
            }
            catch (AuthenticationException e)
            {
                isAskingForInput = true;

                AnsiConsole.Write(
                    new Panel(new Text(
                        e.Result == EResult.InvalidPassword ?
                            "You have entered an invalid username or password." :
                            $"Something went wrong trying to begin authentication ({e.Result})",
                        new Style(Color.Red)))
                        .BorderColor(Color.Red)
                        .RoundedBorder()
                );

                ReadCredentialsAgain();

                return;
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
                AnsiConsole.WriteLine("Previous authentication polling was cancelled.");
                return;
            }

            await SaveCredentials();
        }

        authSession = null;
        pass = null; // Password should not be needed

        AnsiConsole.WriteLine("Logging in...");

        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            LoginID = 0x44_55_4D_50, // "DUMP"
            Username = savedCredentials.Username,
            AccessToken = savedCredentials.RefreshToken,
            ShouldRememberPassword = Configuration.RememberLogin,
        });
    }

    private static void DrawQRCode(QrAuthSession authSession)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);

        const int QuietZone = 6;
        const int QuietZoneOffset = QuietZone / 2;
        var size = qrCodeData.ModuleMatrix.Count - QuietZone;
        var canvas = new Canvas(size, size)
        {
            Scale = false,
        };

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var module = qrCodeData.ModuleMatrix[y + QuietZoneOffset][x + QuietZoneOffset];

                canvas.SetPixel(x, y, module ? Color.Gold1 : Color.Black);
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(canvas)
        {
            Header = new(" Use the Steam Mobile App to log in via QR code ", Justify.Center)
        }.RoundedBorder());
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (ExitToken.IsCancellationRequested)
        {
            AnsiConsole.WriteLine("Disconnected from Steam, exiting...");

            return;
        }

        if (isAskingForInput || callback.UserInitiated)
        {
            AnsiConsole.WriteLine($"Disconnected from Steam, waiting for user input to finish...");
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

        AnsiConsole.WriteLine($"Disconnected from Steam, reconnecting in {sleep / 1000} seconds...");

        Thread.Sleep(sleep);

        steamClient.Connect();
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            if (callback.Result is EResult.ServiceUnavailable or EResult.TryAnotherCM)
            {
                AnsiConsole.Write(
                    new Panel(new Text($"Steam is currently having issues ({callback.Result})...", new Style(Color.Red)))
                        .BorderColor(Color.Red)
                        .RoundedBorder()
                );
            }
            else if (callback.Result
                is EResult.InvalidPassword
                or EResult.InvalidSignature
                or EResult.AccessDenied
                or EResult.Expired
                or EResult.Revoked)
            {
                isAskingForInput = true;
                reconnectCount = 0;

                if (savedCredentials.RefreshToken != null)
                {
                    savedCredentials.RefreshToken = null;

                    AnsiConsole.Write(
                        new Panel(new Text($"Stored credentials are invalid. ({callback.Result})", new Style(Color.Red)))
                            .BorderColor(Color.Red)
                            .RoundedBorder()
                    );

                    Task.Run(SaveCredentials);
                }
                else
                {
                    AnsiConsole.Write(
                        new Panel(new Text("You have entered an invalid username or password.", new Style(Color.Red)))
                            .BorderColor(Color.Red)
                            .RoundedBorder()
                    );
                }

                ReadCredentialsAgain();
            }
            else
            {
                AnsiConsole.Write(
                    new Panel(new Text($"Unable to log in to Steam: {callback.Result} ({callback.ExtendedResult})", new Style(Color.Red)))
                        .BorderColor(Color.Red)
                        .RoundedBorder()
                );

                ExitToken.Cancel();
            }

            return;
        }

        reconnectCount = 0;

        if (licenseListReceived)
        {
            AnsiConsole.WriteLine("Logged in, continuing...");

            ReconnectEvent.SetResult(true);
            ReconnectEvent = new();

            return;
        }

        Task.Run(RenewRefreshTokenIfRequired);

        var steamid = callback.ClientSteamID ?? new SteamID(0, EUniverse.Public, EAccountType.Invalid);
        Payload.SteamID = steamid.Render();

        if (steamid.AccountType == EAccountType.AnonUser)
        {
            AnsiConsole.WriteLine("Logged in, requesting package for anonymous users...");

            const uint ANONYMOUS_PACKAGE = 17906;

            var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>()!, KnownDepotIds, this);

            Task.Run(async () =>
            {
                var tokenJob = await steamClient.GetHandler<SteamApps>()!.PICSGetAccessTokens(null, ANONYMOUS_PACKAGE);
                tokenJob.PackageTokens.TryGetValue(ANONYMOUS_PACKAGE, out var token);

                await DoRequest(requester,
                [
                    new(ANONYMOUS_PACKAGE, token)
                ]);
            });
        }
        else
        {
            AnsiConsole.WriteLine("Logged in, waiting for licenses...");
        }
    }

    private async Task RenewRefreshTokenIfRequired()
    {
        if (!Configuration.RememberLogin || savedCredentials.RefreshToken == null)
        {
            return;
        }

        try
        {
            var token = new JsonWebToken(savedCredentials.RefreshToken);

            AnsiConsole.WriteLine($"Refresh token is valid until {token.ValidTo:yyyy-MM-dd HH:mm:ss}");

            if (DateTime.UtcNow.Add(TimeSpan.FromDays(30)) >= token.ValidTo)
            {
                var newToken = await steamClient.Authentication.GenerateAccessTokenForAppAsync(steamClient.SteamID!, savedCredentials.RefreshToken, allowRenewal: true);

                if (!string.IsNullOrEmpty(newToken.RefreshToken))
                {
                    AnsiConsole.WriteLine("Renewed the refresh token");

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

    private void OnLicenseList(SteamApps.LicenseListCallback licenseList)
    {
        if (licenseListReceived)
        {
            return;
        }

        licenseListReceived = true;

        var requester = new Requester(Payload, steamClient.GetHandler<SteamApps>()!, KnownDepotIds, this);
        var packages = requester.ProcessLicenseList(licenseList);

        Task.Factory.StartNew(
            async () => await DoRequest(requester, packages),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private async Task DoRequest(Requester requester, List<SteamApps.PICSRequest> packages)
    {
        var storePackages = await Requester.GetOwnedFromStore(steamClient, savedCredentials.RefreshToken!, ApiClient.HttpClient);
        await requester.ProcessPackages(packages, storePackages);

        var success = await ApiClient.SendTokens(Payload, Configuration);

        if (success)
        {
            await KnownDepotIds.SaveKnownDepotIds();
        }

        await ExitToken.CancelAsync();

        steamUser.LogOff();
    }

    private static void SinkUnreadKeys()
    {
        while (Console.KeyAvailable)
        {
            Console.ReadKey(true);
        }
    }
}
