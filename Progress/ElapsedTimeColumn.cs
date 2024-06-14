using System;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SteamTokenDumper;

/// <summary>
/// A column showing the elapsed time of a task.
/// </summary>
internal sealed class ElapsedTimeColumn : ProgressColumn
{
    /// <inheritdoc/>
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var elatimesed = task.ElapsedTime;

        if (elatimesed == null || elatimesed.Value.TotalMinutes >= 60 || elatimesed.Value.Ticks == 0)
        {
            return new Text(string.Empty);
        }

        return new Text($"{elatimesed.Value:mm\\:ss}", Color.Blue);
    }

    /// <inheritdoc/>
    public override int? GetColumnWidth(RenderOptions options)
    {
        return 5;
    }
}
