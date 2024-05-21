using Spectre.Console;
using SteamKit2;

namespace SteamTokenDumper;

internal sealed class SteamKitLogger : IDebugListener
{
    public void WriteLine(string category, string msg)
    {
        AnsiConsole.MarkupLineInterpolated($"[purple]{category}: {msg}[/]");
    }
}
