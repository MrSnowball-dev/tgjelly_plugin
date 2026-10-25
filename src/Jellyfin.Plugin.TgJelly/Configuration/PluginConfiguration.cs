using System;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// TgJelly settings, stored by Jellyfin as XML (spec §10.1).
/// Fields are only ever added, never renamed or retyped.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The highest revision an outside save can set (spec §10.4); far below <see cref="long.MaxValue"/>, so later
    /// increments never wrap around.
    /// </summary>
    public const long MaxOutsideRevision = long.MaxValue / 2;

    /// <summary>
    /// Gets or sets the settings schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// Gets or sets the settings revision, increased on every save.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// Gets or sets the UI and message language (hidden in v1).
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets the public server URL used for "Open in Jellyfin" links.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy settings. Credentials live in secrets.json.
    /// </summary>
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>
    /// Gets or sets what messages show: the switches of each message.
    /// </summary>
    public MessageOptions Message { get; set; } = new();

    /// <summary>
    /// Gets or sets the batching values.
    /// </summary>
    public BatchingOptions Batching { get; set; } = new();

    /// <summary>
    /// Gets or sets the update-check settings.
    /// </summary>
    public UpdateCheckOptions UpdateCheck { get; set; } = new();

    /// <summary>
    /// Gets or sets the notification destinations.
    /// </summary>
    public Destination[] Destinations { get; set; } = Array.Empty<Destination>();

    /// <summary>
    /// Gets or sets the custom template overrides.
    /// </summary>
    public TemplateOverride[] Templates { get; set; } = Array.Empty<TemplateOverride>();

    /// <summary>
    /// Replaces <c>null</c> sections, arrays and strings with their defaults. Used for settings that arrive from
    /// outside <c>SettingsStore</c>, e.g. a POST to Jellyfin's generic configuration endpoint with
    /// <c>"Batching": null</c>.
    /// </summary>
    public void Normalize()
    {
        Language ??= "en";
        PublicUrl ??= string.Empty;
        Proxy ??= new ProxySettings();
        Proxy.Url ??= string.Empty;
        Message ??= new MessageOptions();
        Batching ??= new BatchingOptions();
        UpdateCheck ??= new UpdateCheckOptions();
        Destinations ??= Array.Empty<Destination>();
        Templates ??= Array.Empty<TemplateOverride>();
        for (var i = 0; i < Destinations.Length; i++)
        {
            var d = Destinations[i] ??= new Destination();
            d.Id ??= string.Empty;
            d.Title ??= string.Empty;
            d.Events ??= Array.Empty<string>();
            d.LibraryIds ??= Array.Empty<string>();
            d.SilentGroups ??= Array.Empty<string>();
            d.TopicRoutes = [.. (d.TopicRoutes ?? []).Where(r => r is not null)];
            d.KnownTopics = [.. (d.KnownTopics ?? []).Where(t => t is not null)];
            foreach (var r in d.TopicRoutes)
            {
                r.Slot ??= string.Empty;
            }

            foreach (var t in d.KnownTopics)
            {
                t.Name ??= string.Empty;
            }
        }

        for (var i = 0; i < Templates.Length; i++)
        {
            var t = Templates[i] ??= new TemplateOverride();
            t.EventKey ??= string.Empty;
            t.Text ??= string.Empty;
        }

        Message.PerMessage ??= [];
        Message.PerMessage = [.. Message.PerMessage.Where(e => e is not null)];
        foreach (var entry in Message.PerMessage)
        {
            entry.EventKey ??= string.Empty;
            entry.Switches ??= MessageSwitches.DefaultFor(entry.EventKey);
        }

        Templates = [.. Templates.Where(t => !string.IsNullOrWhiteSpace(t.Text))];
    }
}
