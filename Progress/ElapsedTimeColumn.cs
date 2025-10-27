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
        var elapsed = task.ElapsedTime;

        if (elapsed == null || elapsed.Value.TotalMinutes >= 60 || elapsed.Value.Ticks == 0)
        {
            return new Text(string.Empty);
        }

        return new Text($"{elapsed.Value:mm\\:ss}", Color.Blue);
    }

    /// <inheritdoc/>
    public override int? GetColumnWidth(RenderOptions options)
    {
        return 5;
    }
}
