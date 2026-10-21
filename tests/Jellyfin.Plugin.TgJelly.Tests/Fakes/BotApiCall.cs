using System;
using System.Text.Json.Nodes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// One request the fake Bot API received: the method and its parameters (multipart file parts as <c>"file:&lt;name&gt;"</c>).
/// </summary>
public sealed record BotApiCall(string Method, JsonObject Body)
{
    /// <summary>
    /// Asserts that the call's text explains each button: it contains <c>&lt;b&gt;label&lt;/b&gt; — </c> for every label.
    /// </summary>
    /// <param name="labels">The button labels, already HTML-escaped as sent.</param>
    public void Explains(params string[] labels)
    {
        var text = (string)Body["text"]!;
        foreach (var label in labels)
        {
            Assert.Contains("<b>" + label + "</b> — ", text, StringComparison.Ordinal);
        }
    }
}
