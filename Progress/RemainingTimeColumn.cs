using System;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SteamTokenDumper;

/// <summary>
/// A column showing the remaining time of a task.
/// </summary>
internal sealed class RemainingTimeColumn : ProgressColumn
{
    /// <inheritdoc/>
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var time = task.RemainingTime;

        if (time == null || time.Value.TotalMinutes >= 60 || time.Value.Ticks == 0)
        {
            return new Text(string.Empty);
        }

        return new Text($"{time.Value:mm\\:ss}", Color.Grey);
    }

    /// <inheritdoc/>
    public override int? GetColumnWidth(RenderOptions options)
    {
        return 5;
    }
}
