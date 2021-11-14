using System;
using SteamKit2;

namespace SteamTokenDumper
{
    internal class SteamKitLogger : IDebugListener
    {
        public void WriteLine(string category, string msg)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{category}] {msg}");
            Console.ResetColor();
        }
    }
}
