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
    public int SchemaVersion { get; set; } = 1;

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

        MigrateTemplates();
        MigrateMessageOptions();
        UpdateCheck.MigrateMode();
    }

    /// <summary>
    /// Plan 7 migration (design §5.1): an override that was switched off, or has no text, is removed. The others are
    /// the template in use, so <see cref="TemplateOverride.Enabled"/> is set to true.
    /// </summary>
    public void MigrateTemplates()
    {
        Templates ??= Array.Empty<TemplateOverride>();
        Templates = [.. Templates.Where(t => t is not null && t.Enabled && !string.IsNullOrWhiteSpace(t.Text))];
    }

    /// <summary>
    /// Plan 9 migration (design §6.2): v0.4's one set of switches becomes each media message's switches, the digest's
    /// included, so nothing a user sees changes. Security and Server messages keep their defaults: no poster. Runs on
    /// every settings load (each <c>SettingsStore</c> copy); the v0.4 values are cleared once read, so later runs and
    /// saves leave the per-message switches alone.
    /// </summary>
    public void MigrateMessageOptions()
    {
        Message ??= new MessageOptions();
        var m = Message;
        m.PerMessage ??= [];
#pragma warning disable CS0618 // the migration is what reads the v0.4 values
        var legacy = m.ShowPoster.HasValue || m.ShowOverview.HasValue || m.ShowRatingGenres.HasValue
            || m.ShowOpenInJellyfin.HasValue || m.ShowProviderLinks.HasValue;
        if (legacy && m.PerMessage.Length == 0)
        {
            var media = new MessageSwitches
            {
                ShowPoster = m.ShowPoster ?? true,
                ShowOverview = m.ShowOverview ?? true,
                ShowRatingGenres = m.ShowRatingGenres ?? true,
                ShowOpenInJellyfin = m.ShowOpenInJellyfin ?? true,
                ShowProviderLinks = m.ShowProviderLinks ?? true,
            };
            m.PerMessage =
            [
                .. MessageOptions.Keys.Select(key => new MessageSwitchesEntry
                {
                    EventKey = key,
                    Switches = EventCatalog.GroupOf(key) == "media" ? media.Copy() : MessageSwitches.DefaultFor(key),
                }),
            ];
        }

        m.ShowPoster = m.ShowOverview = m.ShowRatingGenres = m.ShowOpenInJellyfin = m.ShowProviderLinks = null;
#pragma warning restore CS0618
    }
}
