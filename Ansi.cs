using System;
using Spectre.Console;

namespace SteamTokenDumper;

static class Ansi
{
    // https://conemu.github.io/en/AnsiEscapeCodes.html#ConEmu_specific_OSC
    // https://learn.microsoft.com/en-us/windows/terminal/tutorials/progress-bar-sequences
    internal enum ProgressState
    {
        Hidden = 0,
        Default = 1,
        Error = 2,
        Indeterminate = 3,
        Warning = 4,
    }

    const string ESC = "\u001b";
    const string BEL = "\u0007";

    public static void Progress(ProgressTask task)
    {
        Progress(ProgressState.Default, (byte)task.Percentage);
    }

    public static void Progress(ProgressState state, byte progress = 0)
    {
        var caps = AnsiConsole.Profile.Capabilities;

        if (!caps.Interactive || !caps.Ansi || caps.Legacy)
        {
            return;
        }

        Console.Write($"{ESC}]9;4;{(byte)state};{progress}{BEL}");
    }
}
