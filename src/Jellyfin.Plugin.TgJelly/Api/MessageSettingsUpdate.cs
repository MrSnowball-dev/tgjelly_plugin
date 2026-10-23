using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// Checks the entries: each must name a message TgJelly sends.
    /// </summary>
    /// <returns>The problems; empty when the update can be applied.</returns>
    public IReadOnlyList<SettingsError> Validate()
    {
        var errors = new List<SettingsError>();
        var entries = PerMessage ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"Message.PerMessage[{i}]");
            if (entries[i] is not { } entry)
            {
                errors.Add(new SettingsError(field, "Send an entry with an EventKey and the Switches to change."));
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
