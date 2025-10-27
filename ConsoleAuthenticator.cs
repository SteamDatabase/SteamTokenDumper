using System.Threading.Tasks;
using Spectre.Console;
using SteamKit2.Authentication;

namespace SteamTokenDumper;

internal sealed class ConsoleAuthenticator(bool SkipDeviceConfirmation) : IAuthenticator
{
    /// <inheritdoc />
    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
        {
            AnsiConsole.MarkupLine("[red]The previous two-factor auth code you have provided is incorrect.[/]");
        }

        var code = await AnsiConsole.AskAsync<string>("[green][bold]STEAM GUARD![/][/] Enter your two-factor code from your authenticator app:");

        return code.Trim();
    }

    /// <inheritdoc />
    public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
        {
            AnsiConsole.MarkupLine("[red]The previous two-factor auth code you have provided is incorrect.[/]");
        }

        var code = await AnsiConsole.AskAsync<string>($"[green][bold]STEAM GUARD![/][/] Please enter the auth code sent to the email at {Markup.Escape(email)}:");

        return code.Trim();
    }

    /// <inheritdoc />
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        if (SkipDeviceConfirmation)
        {
            return Task.FromResult(false);
        }

        AnsiConsole.MarkupLine("[green][bold]STEAM GUARD![/][/] Use the Steam Mobile App to confirm your login...");
        AnsiConsole.MarkupLine("If you want to enter a two-factor code instead, take a look at the config file.");

        return Task.FromResult(true);
    }
}
