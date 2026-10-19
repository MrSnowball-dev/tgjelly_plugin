using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The default security and server layouts (spec §8.1): text only, rendered from the payload snapshot in
/// <see cref="RenderModel.Values"/>.
/// </summary>
public sealed class AlertLayouts
{
    private const string Separator = " · ";

    private readonly Formatter _formatter;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertLayouts"/> class.
    /// </summary>
    /// <param name="formatter">The formatter.</param>
    /// <param name="time">The clock, which decides whether times show their date.</param>
    public AlertLayouts(Formatter formatter, TimeProvider time)
    {
        _formatter = formatter;
        _time = time;
    }

    /// <summary>
    /// Renders an alert.
    /// </summary>
    /// <param name="model">The alert's model.</param>
    /// <returns>The message: <c>Text</c> fitted at 4096, <c>Caption</c> the same text fitted at 1024, no poster.</returns>
    public OutboxMessage Render(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var values = new Values(model.Values, _formatter);
        var time = _formatter.Time(model.At, _time.GetUtcNow());
        var lines = new List<string>(3);
        IReadOnlyList<string> list = [];
        string? quote = null;
        var buttons = new List<List<OutboxButton>>();
        switch (model.EventKey)
        {
            case "security.login_failed":
                lines.Add(Get("security.login_failed.heading", values, EventFields.User));
                if (values.Count(EventFields.Attempts) > 1)
                {
                    lines.Add(_formatter.Strings.Get(
                        "security.login_failed.many",
                        ("attempts", _formatter.Number(values.Count(EventFields.Attempts))),
                        ("ips", _formatter.Strings.Plural("security.ips", values.Count(EventFields.IpCount))),
                        ("ip", values[EventFields.Ip]),
                        ("client", values[EventFields.Client]),
                        ("time", time)));
                }
                else
                {
                    lines.Add(Details(values[EventFields.Ip], values[EventFields.Client], values[EventFields.Device], time));
                }

                if (string.Equals(values.Raw(EventFields.Locked), "true", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(_formatter.Strings.Get("security.locked"));
                }

                break;
            case "security.lockout":
                lines.Add(_formatter.Strings.Plural("security.lockout", values.Count(EventFields.Attempts), ("user", values[EventFields.User])));
                break;
            case "security.login_succeeded":
                lines.Add(Get("security.login_succeeded.heading", values, EventFields.User));
                lines.Add(Details(values[EventFields.Client], values[EventFields.Device]));
                if (values[EventFields.Ip].Length > 0)
                {
                    lines.Add(_formatter.Strings.Get("security.ip", ("ip", values[EventFields.Ip])));
                }

                lines.Add(_formatter.Strings.Get("security.at", ("time", time)));
                break;
            case "security.user_created" or "security.user_deleted" or "security.password_changed":
                lines.Add(Get(model.EventKey, values, EventFields.User));
                break;
            case "server.started":
                lines.Add(Get(model.EventKey, values, EventFields.Version, EventFields.Server));
                break;
            case "server.update_available":
                lines.Add(Get(model.EventKey, values, EventFields.Version, EventFields.Current));
                if (values.Raw(EventFields.Url) is { Length: > 0 } url)
                {
                    buttons.Add([new OutboxButton { Text = _formatter.Strings.Get("button.releaseNotes"), Url = url }]);
                }

                break;
            case "server.plugin_installed" or "server.plugin_updated" or "server.plugin_uninstalled" or "server.plugin_install_failed":
                lines.Add(Get(model.EventKey, values, EventFields.Plugin, EventFields.Version));
                quote = model.EventKey switch
                {
                    "server.plugin_updated" => values.Raw(EventFields.Changelog),
                    "server.plugin_install_failed" => values.Raw(EventFields.Error),
                    _ => null,
                };
                break;
            case "server.task_failed":
                lines.Add(Get(model.EventKey, values, EventFields.Task));
                quote = values.Raw(EventFields.Error);
                break;
            case "server.restart_pending":
                lines.Add(_formatter.Strings.Get(model.EventKey));
                list = model.Lines.Select(_formatter.Escape).ToList();
                break;
            default:
                lines.Add(_formatter.Escape(model.EventKey));
                break;
        }

        var parts = new MessageParts(
            string.Join('\n', lines.Where(l => l.Length > 0)),
            list,
            string.IsNullOrWhiteSpace(quote) ? null : quote.Trim(),
            string.Empty,
            QuoteOverview: true);
        return new OutboxMessage
        {
            Text = MessageFitter.Fit(parts, MessageFitter.TextLimit, More),
            Caption = MessageFitter.Fit(parts, MessageFitter.CaptionLimit, More),
            Buttons = buttons,
        };
    }

    private static string Details(params string[] parts) => string.Join(Separator, parts.Where(p => p.Length > 0));

    private string Get(string key, Values values, params string[] fields)
        => _formatter.Strings.Get(key, fields.Select(f => (f, (object?)values[f])).ToArray());

    private string More(int count) => _formatter.Strings.Get("media.more", ("count", _formatter.Number(count)));

    /// <summary>The payload snapshot: the indexer escapes, <see cref="Raw"/> doesn't.</summary>
    private readonly struct Values(Dictionary<string, string> values, Formatter formatter)
    {
        public string this[string field] => formatter.Escape(Raw(field));

        public string Raw(string field) => values.TryGetValue(field, out var value) ? value : string.Empty;

        public long Count(string field) => long.TryParse(Raw(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
