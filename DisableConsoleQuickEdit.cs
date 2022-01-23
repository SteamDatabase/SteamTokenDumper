using System;
using System.Runtime.InteropServices;

namespace SteamTokenDumper;

internal static class WindowsDisableConsoleQuickEdit
{
    const uint EnableQuickEdit = 0x0040;
    const int StandardInputHandle = -10;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    internal static void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var consoleHandle = GetStdHandle(StandardInputHandle);

        if (!GetConsoleMode(consoleHandle, out var consoleMode))
        {
            return;
        }

        consoleMode &= ~EnableQuickEdit;

        SetConsoleMode(consoleHandle, consoleMode);
    }
}
