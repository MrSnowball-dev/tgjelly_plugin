using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Checks the settings' value ranges and uniqueness rules.
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// Validates a configuration.
    /// </summary>
    /// <param name="configuration">The configuration to check.</param>
    /// <returns>All problems found; empty when valid.</returns>
    public static IReadOnlyList<SettingsError> Validate(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<SettingsError>();
        RequireNotNull(errors, nameof(PluginConfiguration.Proxy), configuration.Proxy);
        RequireNotNull(errors, nameof(PluginConfiguration.Message), configuration.Message);
        RequireNotNull(errors, "Message.PerMessage", configuration.Message?.PerMessage);
        var perMessage = configuration.Message?.PerMessage ?? [];
        for (var i = 0; i < perMessage.Length; i++)
        {
            RequireNotNull(errors, string.Create(CultureInfo.InvariantCulture, $"Message.PerMessage[{i}].Switches"), perMessage[i]?.Switches);
        }

        RequireNotNull(errors, nameof(PluginConfiguration.Batching), configuration.Batching);
        RequireNotNull(errors, nameof(PluginConfiguration.UpdateCheck), configuration.UpdateCheck);
        RequireNotNull(errors, nameof(PluginConfiguration.Destinations), configuration.Destinations);
        RequireNotNull(errors, nameof(PluginConfiguration.Templates), configuration.Templates);

        var b = configuration.Batching ?? new BatchingOptions();

        CheckRange(errors, "Batching.QuietWindowSeconds", b.QuietWindowSeconds, 5, 3600);
        CheckRange(errors, "Batching.MaxHoldMinutes", b.MaxHoldMinutes, 1, 240);
        CheckRange(errors, "Batching.ReleaseQuietSeconds", b.ReleaseQuietSeconds, 5, 600);
        CheckRange(errors, "Batching.DigestThreshold", b.DigestThreshold, 2, 100);
        CheckRange(errors, "Batching.LateEditHours", b.LateEditHours, 0, 24);
        CheckRange(errors, "Batching.ReadyTimeoutMinutes", b.ReadyTimeoutMinutes, 1, 120);
        if (b.MaxHoldMinutes * 60 < b.QuietWindowSeconds)
        {
            errors.Add(new SettingsError("Batching.MaxHoldMinutes", "Must be at least as long as the quiet window."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var chats = new HashSet<(long ChatId, int ThreadId)>();
        var destinations = configuration.Destinations ?? Array.Empty<Destination>();
        for (var i = 0; i < destinations.Length; i++)
        {
            var d = destinations[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Destinations[{i}]");
            if (d is null)
            {
                errors.Add(new SettingsError(prefix, "Required."));
                continue;
            }

            RequireNotNull(errors, prefix + ".Events", d.Events);
            RequireNotNull(errors, prefix + ".LibraryIds", d.LibraryIds);
            RequireNotNull(errors, prefix + ".SilentGroups", d.SilentGroups);
            if (!IsValidId(d.Id))
            {
                errors.Add(new SettingsError(prefix + ".Id", "Must be 8 characters from a-z and 0-9."));
            }
            else if (!ids.Add(d.Id))
            {
                errors.Add(new SettingsError(prefix + ".Id", "Is used by another destination."));
            }

            if (!chats.Add((d.ChatId, d.ThreadId)))
            {
                errors.Add(new SettingsError(prefix + ".ChatId", "This chat and topic is already a destination."));
            }
        }

        return errors;
    }

    /// <summary>
    /// Checks a destination id: exactly 8 characters from [a-z0-9].
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length != 8)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireNotNull(List<SettingsError> errors, string field, object? value)
    {
        if (value is null)
        {
            errors.Add(new SettingsError(field, "Required."));
        }
    }

    private static void CheckRange(List<SettingsError> errors, string field, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add(new SettingsError(field, string.Create(CultureInfo.InvariantCulture, $"Must be between {min} and {max}.")));
        }
    }
}
