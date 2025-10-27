using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

[assembly: CLSCompliant(false)]
namespace SteamTokenDumper;

internal static class Application
{
    public static string AppPath { get; private set; } = string.Empty;

    public static async Task Main()
    {
        WindowsDisableConsoleQuickEdit.Disable();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "SteamDB Token Dumper";

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        AppPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)!;

        using var application = new Program();
        await application.RunAsync();
    }
}
