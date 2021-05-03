using System;
using System.IO;
using System.Threading.Tasks;

namespace SteamTokenDumper
{
    internal class Configuration
    {
        public bool RememberLogin { get; private set; }
        public bool SkipAutoGrant { get; private set; }
        public bool VerifyBeforeSubmit { get; private set; }
        public bool DumpPayload { get; private set; }

        public async Task Load()
        {
            var path = Path.Combine(Program.AppPath, "SteamTokenDumper.config.ini");
            var lines = await File.ReadAllLinesAsync(path);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                {
                    continue;
                }

                var option = line.Split('=', 2, StringSplitOptions.TrimEntries);

                if (option.Length != 2)
                {
                    continue;
                }

                switch (option[0])
                {
                    case "RememberLogin":
                        RememberLogin = option[1] == "1";
                        break;
                    case "SkipAutoGrant":
                        SkipAutoGrant = option[1] == "1";
                        break;
                    case "VerifyBeforeSubmit":
                        VerifyBeforeSubmit = option[1] == "1";
                        break;
                    case "DumpPayload":
                        DumpPayload = option[1] == "1";
                        break;
                }
            }

            if (RememberLogin)
            {
                Console.WriteLine("Will remember your login.");
            }

            if (SkipAutoGrant)
            {
                Console.WriteLine("Will skip auto granted packages.");
            }

            if (DumpPayload)
            {
                Console.WriteLine("Will dump payload.");
            }

            if (VerifyBeforeSubmit)
            {
                Console.WriteLine("Will ask for confirmation before sending results.");
            }
        }
    }
}
