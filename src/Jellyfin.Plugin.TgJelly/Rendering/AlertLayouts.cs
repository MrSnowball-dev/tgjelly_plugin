using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The buttons of a security or server message (spec §8.1), from the payload snapshot in <see cref="RenderModel.Values"/>.
/// Alerts have no poster; the text comes from the message's template.
/// </summary>
public sealed class AlertLayouts
{
    private readonly Formatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertLayouts"/> class.
    /// </summary>
    /// <param name="formatter">The formatter, for the button labels.</param>
    public AlertLayouts(Formatter formatter)
    {
        _formatter = formatter;
    }

    /// <summary>
    /// Builds an alert's buttons.
    /// </summary>
    /// <param name="model">The alert's model.</param>
    /// <returns>The message without text: only "Release notes" for an update with a URL has a button.</returns>
    public OutboxMessage Render(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var buttons = new List<List<OutboxButton>>();
        if (model.EventKey == "server.update_available"
            && model.Values.TryGetValue(EventFields.Url, out var url)
            && url is { Length: > 0 })
        {
            buttons.Add([new OutboxButton { Text = _formatter.Strings.Get("button.releaseNotes"), Url = url }]);
        }

        return new OutboxMessage { Buttons = buttons };
    }
}
