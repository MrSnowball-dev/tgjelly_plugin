using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Renders a model for one destination (spec §8): picks the layout by <see cref="RenderModel.Kind"/> and applies the
/// destination's silent groups (§8.4).
/// </summary>
public sealed class NotificationRenderer
{
    private readonly MediaLayouts _media;
    private readonly AlertLayouts _alerts;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationRenderer"/> class.
    /// </summary>
    /// <param name="media">The media layouts.</param>
    /// <param name="alerts">The security and server layouts.</param>
    public NotificationRenderer(MediaLayouts media, AlertLayouts alerts)
    {
        _media = media;
        _alerts = alerts;
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
            RenderKind.Digest => _media.RenderDigest(model),
            _ => _alerts.Render(model),
        };
        if (message is not null)
        {
            message.Silent = destination.SilentGroups.Contains(EventCatalog.GroupOf(model.EventKey), StringComparer.Ordinal);
        }

        return message;
    }
}
