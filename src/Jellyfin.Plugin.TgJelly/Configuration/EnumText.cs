using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Lenient enum parsing for persisted settings and state. After a downgrade, files may contain enum names this
/// version does not know; they read as a safe fallback instead of failing the whole load (spec F9: a failed XML
/// load makes Jellyfin replace the config with defaults).
/// </summary>
public static class EnumText
{
    /// <summary>
    /// Parses an enum name, or returns the fallback for unknown or missing names.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="text">The persisted name.</param>
    /// <returns>The parsed value, or <see cref="Fallback{T}"/>.</returns>
    public static T Parse<T>(string? text)
        where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : Fallback<T>();

    /// <summary>
    /// The value an unknown name reads as: <see cref="PresetKind.Custom"/> for presets (so a destination never
    /// silently gains every event key), otherwise the enum's default (first) member.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <returns>The fallback value.</returns>
    public static T Fallback<T>()
        where T : struct, Enum
        => typeof(T) == typeof(PresetKind) ? (T)(object)PresetKind.Custom : default;
}
