using System.Threading.Tasks;
using Spectre.Console;
using SteamKit2.Authentication;

namespace SteamTokenDumper;

internal sealed class ConsoleAuthenticator : IAuthenticator
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
        AnsiConsole.MarkupLine("[green][bold]STEAM GUARD![/][/] Use the Steam Mobile App to confirm your sign in...");

        return Task.FromResult(true);
    }
}
