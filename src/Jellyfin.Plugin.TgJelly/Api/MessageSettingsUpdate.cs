using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The <c>Message</c> part of <see cref="SettingsUpdateRequest"/>. Saves are partial (plan 8): entries merge by event
/// key, and anything not sent keeps its value.
/// </summary>
public sealed class MessageSettingsUpdate
{
    /// <summary>Gets or sets the messages whose switches change; <c>null</c> or empty changes none.</summary>
    public IReadOnlyList<MessageSwitchesChange>? PerMessage { get; set; }

    /// <summary>Gets or sets whether quality upgrades are announced; <c>null</c> keeps the saved value.</summary>
    public bool? AnnounceUpgrades { get; set; }

    /// <summary>Gets or sets the fields this version does not know, such as an older page's flat switches.</summary>
    [JsonExtensionData]
    #pragma warning disable CA2227 // System.Text.Json fills extension data through its setter.
    public Dictionary<string, JsonElement>? Unrecognised { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Checks the entries: each must name a message TgJelly sends.
    /// </summary>
    /// <returns>The problems; empty when the update can be applied.</returns>
    public IReadOnlyList<SettingsError> Validate()
    {
        var errors = new List<SettingsError>();
        if (Unrecognised is { Count: > 0 })
        {
            errors.Add(new SettingsError(
                "Message",
                $"This request uses an older format ({string.Join(", ", Unrecognised.Keys.Order(StringComparer.Ordinal))} not understood), so nothing was saved. Reload the TgJelly page to get the current version."));
        }

        var entries = PerMessage ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"Message.PerMessage[{i}]");
            if (entries[i] is not { } entry)
            {
                errors.Add(new SettingsError(field, "Send an entry with an EventKey and the Switches to change."));
            }
            else if (entry.Switches is null || !entry.Switches.HasAny)
            {
                errors.Add(new SettingsError(
                    field + ".Switches",
                    $"The entry for \"{entry.EventKey}\" changes nothing. Send at least one of ShowPoster, ShowOverview, ShowRatingGenres, ShowOpenInJellyfin or ShowProviderLinks as true or false."));
            }
            else if (!MessageOptions.IsKey(entry.EventKey))
            {
                errors.Add(new SettingsError(
                    field + ".EventKey",
                    $"\"{entry.EventKey}\" is not a message TgJelly sends. Use an event key such as media.movie or security.lockout, or media.digest for the digest."));
            }
        }

        return errors;
    }

    /// <summary>
    /// Applies the update to the saved message settings. Call <see cref="Validate"/> first.
    /// </summary>
    /// <param name="options">The message settings to change.</param>
    public void ApplyTo(MessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AnnounceUpgrades = AnnounceUpgrades ?? options.AnnounceUpgrades;
        foreach (var entry in PerMessage ?? [])
        {
            if (entry.Switches is { } patch)
            {
                options.Change(entry.EventKey, patch.ApplyTo);
            }
        }
    }
}
