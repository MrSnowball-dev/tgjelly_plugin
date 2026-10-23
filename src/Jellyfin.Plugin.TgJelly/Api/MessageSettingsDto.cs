using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The <c>Message</c> part of <see cref="SettingsDto"/> (design §6.2): every message with its switches, defaults
/// included, and whether upgrades are announced.
/// </summary>
public sealed class MessageSettingsDto
{
    /// <summary>Gets every message's switches, one entry per <see cref="MessageOptions.Keys"/>, in that order.</summary>
    public IReadOnlyList<MessageSwitchesEntry> PerMessage { get; init; } = [];

    /// <summary>Gets a value indicating whether quality upgrades are announced, for every media message.</summary>
    public bool AnnounceUpgrades { get; init; }

    /// <summary>
    /// Builds the DTO from the settings.
    /// </summary>
    /// <param name="options">The message settings.</param>
    /// <returns>The DTO.</returns>
    public static MessageSettingsDto From(MessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MessageSettingsDto
        {
            PerMessage = [.. MessageOptions.Keys.Select(key => new MessageSwitchesEntry { EventKey = key, Switches = options.For(key) })],
            AnnounceUpgrades = options.AnnounceUpgrades,
        };
    }
}
