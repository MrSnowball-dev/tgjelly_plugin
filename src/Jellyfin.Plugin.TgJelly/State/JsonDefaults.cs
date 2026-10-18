using System.Text.Json;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Shared JSON options for every file TgJelly writes: camelCase, indented, enums by name (unknown names read
/// leniently), and <c>null</c> rejected for non-nullable members so a damaged file is quarantined instead of
/// crashing later code.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Gets the options. Cached once, as CA1869 requires.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        Converters = { new LenientEnumConverterFactory() },
    };
}
