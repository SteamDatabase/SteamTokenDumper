﻿using System;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SteamTokenDumper;

/// <summary>
/// A column showing the current value of a task.
/// </summary>
internal sealed class IntValueProgressColumn : ProgressColumn
{
    /// <inheritdoc/>
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var total = (int)task.MaxValue;

        if (task.IsFinished)
        {
            return new Markup($"[green]{total}[/]  ").RightJustified();
        }

        var current = (int)task.Value;

        return new Markup($"[green]{current}[/][grey]/{total}[/]  ").RightJustified();
    }

    /// <inheritdoc/>
    public override int? GetColumnWidth(RenderOptions options)
    {
        return 16;
    }
}
