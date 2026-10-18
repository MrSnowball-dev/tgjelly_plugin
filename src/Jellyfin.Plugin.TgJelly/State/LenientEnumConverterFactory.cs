using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Writes enums by name and reads unknown names as <see cref="EnumText.Fallback{T}"/>, so settings.bak.json and
/// the state files written by a newer version still load after a downgrade.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return EnumText.Parse<T>(reader.GetString());
            }

            // An object or array must be consumed whole, or System.Text.Json fails the entire document.
            reader.Skip();
            return EnumText.Fallback<T>();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
