using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Renders a model for one destination (spec §8): the caption and text come from the message's template
/// (<see cref="TemplateRenderer"/>), the poster and buttons from the layout picked by <see cref="RenderModel.Kind"/>,
/// and the destination's silent groups (§8.4) decide the notification sound. A digest is text only.
/// </summary>
public sealed class NotificationRenderer
{
    private readonly MediaLayouts _media;
    private readonly AlertLayouts _alerts;
    private readonly TemplateRenderer _templates;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationRenderer"/> class.
    /// </summary>
    /// <param name="media">The media layouts, for the poster and buttons.</param>
    /// <param name="alerts">The security and server layouts, for the buttons.</param>
    /// <param name="templates">The template renderer, for the caption and text.</param>
    public NotificationRenderer(MediaLayouts media, AlertLayouts alerts, TemplateRenderer templates)
    {
        _media = media;
        _alerts = alerts;
        _templates = templates;
    }

    /// <summary>
    /// Renders a message.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="destination">The destination it goes to.</param>
    /// <returns>The message, or <c>null</c> when a media group's items are all gone.</returns>
    public OutboxMessage? Render(RenderModel model, Destination destination)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);
        var message = model.Kind switch
        {
            RenderKind.Media => _media.Render(model),
            RenderKind.Digest => new OutboxMessage(),
            _ => _alerts.Render(model),
        };
        if (message is null)
        {
            return null;
        }

        // Every template key has a built-in template (BuiltInTemplatesTests), so the text is always set.
        if (_templates.Render(model, message.PosterPath is not null) is { } text)
        {
            message.Caption = text.Caption;
            message.Text = text.Text;
            message.TemplateKey = text.TemplateKey;
            message.TemplateFallback = text.FallbackError;
        }

        message.Silent = destination.SilentGroups.Contains(EventCatalog.GroupOf(model.EventKey), StringComparer.Ordinal);
        return message;
    }
}
