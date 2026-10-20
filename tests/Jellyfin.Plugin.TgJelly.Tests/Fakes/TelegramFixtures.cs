using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Loads the Bot API JSON fixtures under Fixtures/Telegram, fills their <c>{{placeholder}}</c> tokens and parses them
/// the way the poll loop does. Values are inserted as written, so they must already be valid JSON text.
/// </summary>
public static partial class TelegramFixtures
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["update_id"] = "1",
        ["message_id"] = "10",
        ["user_id"] = "42",
        ["chat_id"] = "-1001234567890",
        ["new_chat_id"] = "-1009876543210",
        ["old_chat_id"] = "-1001234567890",
        ["thread_id"] = "7",
        ["request_id"] = "1",
        ["callback_id"] = "cb1",
        ["data"] = "1|m",
        ["text"] = "hello",
    };

    /// <summary>Reads a fixture as an <see cref="Telegram.Bot.Types.Update"/>.</summary>
    /// <param name="name">The file name without extension.</param>
    /// <param name="values">Placeholder values that override the defaults.</param>
    /// <returns>The parsed update.</returns>
    public static Update Update(string name, params (string Placeholder, string Value)[] values)
        => System.Text.Json.JsonSerializer.Deserialize<Update>(Json(name, values), JsonBotAPI.Options)!;

    /// <summary>Reads a fixture and fills its placeholders, without parsing it (for Bot API responses and messages).</summary>
    /// <param name="name">The file name without extension.</param>
    /// <param name="values">Placeholder values that override the defaults.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(string name, params (string Placeholder, string Value)[] values)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Telegram", name + ".json"));
        var map = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);
        foreach (var (placeholder, value) in values)
        {
            map[placeholder] = value;
        }

        return Token().Replace(json, m => map.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new InvalidOperationException($"Fixture {name} has an unknown placeholder {m.Value}"));
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Token();
}
