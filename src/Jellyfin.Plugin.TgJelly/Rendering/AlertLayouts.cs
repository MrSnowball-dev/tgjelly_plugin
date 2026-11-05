using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The buttons of a security or server message, from the payload snapshot in <see cref="RenderModel.Values"/>.
/// The poster is the TgJelly logo when the message's Poster switch is on; the text comes from the message's template.
/// </summary>
public sealed class AlertLayouts
{
    private readonly Formatter _formatter;
    private readonly SettingsStore _settings;
    private readonly LogoFile _logo;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertLayouts"/> class.
    /// </summary>
    /// <param name="formatter">The formatter, for the button labels.</param>
    /// <param name="settings">The settings: the message's Poster switch.</param>
    /// <param name="logo">The logo file.</param>
    public AlertLayouts(Formatter formatter, SettingsStore settings, LogoFile logo)
    {
        _formatter = formatter;
        _settings = settings;
        _logo = logo;
    }

    /// <summary>
    /// Builds an alert's buttons.
    /// </summary>
    /// <param name="model">The alert's model.</param>
    /// <returns>
    /// The message without text: "Release notes" for an update with a URL, and Restart for a pending restart when its
    /// switch is on, are the only buttons; the logo is its poster when the message's switch is on and the logo file can
    /// be written.
    /// </returns>
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

        var switches = _settings.Current.Message.For(model.EventKey);
        if (model.EventKey == "server.restart_pending" && switches.ShowRestartButton)
        {
            buttons.Add([Restart.RestartButton.Button(_formatter.Strings)]);
        }

        var message = new OutboxMessage { Buttons = buttons };
        if (switches.ShowPoster && _logo.Ensure() is { } path)
        {
            message.PosterPath = path;
            message.PosterCacheKey = _logo.CacheKey;
        }

        return message;
    }
}
