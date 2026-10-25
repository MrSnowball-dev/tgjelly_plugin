using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Remembers which custom templates had a problem at send time. A flag counts only while the override exists
/// and its text is the one that was flagged.
/// </summary>
public sealed class TemplateFlags
{
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateFlags"/> class.
    /// </summary>
    /// <param name="state">The state store.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="time">The clock.</param>
    public TemplateFlags(StateStore state, SettingsStore settings, TimeProvider time)
    {
        _state = state;
        _settings = settings;
        _time = time;
    }

    /// <summary>
    /// Flags a template. Nothing is written when the same flag is already stored.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="kind"><c>error</c> or <c>truncated</c>.</param>
    /// <param name="error">The error text, empty for <c>truncated</c>.</param>
    /// <param name="textHash">The <see cref="TemplateText.Hash"/> of the template text.</param>
    public void Flag(string eventKey, string kind, string error, string textHash)
    {
        var same = _state.Read(s => s.Status.TemplateFlags.TryGetValue(eventKey, out var f)
            && f.Kind == kind && f.Error == error && f.TextHash == textHash);
        if (same)
        {
            return;
        }

        var now = _time.GetUtcNow();
        _state.Update(s => s.Status.TemplateFlags[eventKey] = new TemplateFlag { Kind = kind, Error = error, At = now, TextHash = textHash });
    }

    /// <summary>
    /// Removes a template's flag.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    public void Clear(string eventKey)
    {
        if (_state.Read(s => s.Status.TemplateFlags.ContainsKey(eventKey)))
        {
            _state.Update(s => s.Status.TemplateFlags.Remove(eventKey));
        }
    }

    /// <summary>
    /// Returns a template's flag while it still applies.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The flag, or <c>null</c> when none is stored, the override is gone, or its text changed.</returns>
    public TemplateFlag? Current(string eventKey)
    {
        var flag = _state.Read(s => s.Status.TemplateFlags.TryGetValue(eventKey, out var f)
            ? new TemplateFlag { Kind = f.Kind, Error = f.Error, At = f.At, TextHash = f.TextHash }
            : null);
        if (flag is null)
        {
            return null;
        }

        var template = _settings.Current.Templates.FirstOrDefault(t => string.Equals(t.EventKey, eventKey, StringComparison.Ordinal));
        return template is not null && TemplateText.Hash(template.Text) == flag.TextHash ? flag : null;
    }

    /// <summary>
    /// Returns the event keys that have a stored flag, in order.
    /// </summary>
    /// <returns>The keys.</returns>
    public IReadOnlyList<string> Keys() => _state.Read(s => s.Status.TemplateFlags.Keys.Order(StringComparer.Ordinal).ToArray());
}
