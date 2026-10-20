using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Templates;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

/// <summary>
/// Generates <c>docs/templates.md</c> from <see cref="TemplateFieldCatalog"/>.
/// </summary>
internal static class TemplateDocs
{
    private const string Intro = """
        # Custom message templates

        This page lists what you can use in a custom template for each event.
        Templates are for advanced use. The default messages need no setup.
        The tables below are generated from the plugin's field list, so they always match the plugin.

        ## How templates work

        A template uses the Scriban language. Write plain text and put fields in double braces, like `{{ title }}`.
        You can use `if`, `for` and the other Scriban statements.

        ### What a template replaces

        A template replaces only the text of the message, or its caption when a poster is sent.
        The poster and the buttons still follow your message settings.
        Digests always use the default layout, and a template does not change them.
        Updates to a message, such as a new episode joining a group, render the same template again with the new data.

        ### Escaping and `raw`

        Every value you print is escaped, so a title like `Tom & Jerry` is safe to send.
        Text you type in the template itself is not escaped, so you can write Telegram HTML tags there.
        The allowed tags are `b`, `strong`, `i`, `em`, `u`, `ins`, `s`, `strike`, `del`, `span class="tg-spoiler"`, `tg-spoiler`, `a`, `code`, `pre`, `blockquote` (also `blockquote expandable`), `tg-emoji` and `tg-time`.
        Every tag must be closed.
        To print a value without escaping it, write `{{ raw value }}`. Use this only for text you trust.

        ### Limits

        Each render may run for at most 2 seconds and print at most 16384 characters.
        A loop may run at most 1000 times, and calls may nest at most 20 levels deep.
        Telegram allows 1024 characters in a caption and 4096 in a text message.
        A longer result is cut to fit.

        ### Checks, flags and fallbacks

        Press **Validate** in the dashboard to check a template. Saving does not check it.
        Validate renders the template twice, once with a full sample and once with missing data.
        It reports unknown fields, tags Telegram doesn't allow, and a result with no text.
        When a template fails while a message is sent, the default layout is sent instead, and the template is flagged with the error.
        A template that produces no text counts as failed.
        A template whose result is cut is flagged "truncated".
        A flag clears when you change the template's text or turn it on or off.

        ### Field types

        - `string` is text.
        - `number` is a whole or decimal number.
        - `bool` is true or false.
        - `string[]` is a list of text. Use `{{ for x in genres }}{{ x }}{{ end }}`.
        - `episode[]` is a list of episodes. Each has a `number` and a `title`.

        A field marked optional is empty when the data is missing. Use `{{ if year }}` to test for it.
        Times come as two fields. `<name>_unix` is Unix seconds and `<name>_text` is text in UTC.

        """;

    private static readonly Dictionary<string, string> Examples = new()
    {
        ["media.movie"] = "<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}\n{{ for g in genres }}{{ g }} {{ end }}\n{{ if runtime_minutes }}{{ runtime_minutes }} min{{ end }}",
        ["media.musicvideo"] = "<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}\n{{ if library }}Added to {{ library }}{{ end }}",
        ["media.episodes"] = "{{ if new_series }}New series! {{ end }}<b>{{ series }}</b> season {{ season }}\n{{ for e in episodes }}{{ e.number }}. {{ e.title }}\n{{ end }}",
        ["media.album"] = "<b>{{ artist }}</b> - {{ album }}\n{{ tracks }} tracks",
        ["media.book"] = "<b>{{ title }}</b>\n{{ if parts > 0 }}Audiobook, {{ parts }} parts{{ else }}Book{{ end }}",
        ["security.login_failed"] = "Failed sign-in for <b>{{ user }}</b>: {{ attempts }} attempts from {{ ip_count }} addresses.\nLast: {{ last_ip }} at {{ last_attempt_text }}{{ if locked }}\nThe account is locked.{{ end }}",
        ["security.lockout"] = "<b>{{ user }}</b> is locked out after {{ attempts }} failed attempts.",
        ["security.login_succeeded"] = "<b>{{ user }}</b> signed in with {{ client }} on {{ device }}.",
        ["security.user_created"] = "User <b>{{ user }}</b> was created.",
        ["security.user_deleted"] = "User <b>{{ user }}</b> was deleted.",
        ["security.password_changed"] = "The password of <b>{{ user }}</b> was changed.",
        ["server.started"] = "Jellyfin {{ version }} started on {{ server }}.",
        ["server.update_available"] = "Jellyfin {{ version }} is available. You run {{ current }}.\n{{ url }}",
        ["server.plugin_installed"] = "Plugin <b>{{ plugin }}</b> {{ version }} was installed.",
        ["server.plugin_uninstalled"] = "Plugin <b>{{ plugin }}</b> {{ version }} was removed.",
        ["server.plugin_updated"] = "Plugin <b>{{ plugin }}</b> is now {{ version }}.\n<blockquote>{{ changelog }}</blockquote>",
        ["server.plugin_install_failed"] = "Plugin <b>{{ plugin }}</b> {{ version }} failed to install.\n<blockquote>{{ error }}</blockquote>",
        ["server.task_failed"] = "Task <b>{{ task }}</b> failed.\n<blockquote>{{ error }}</blockquote>",
        ["server.restart_pending"] = "A restart is needed:\n{{ for r in reasons }}- {{ r }}\n{{ end }}",
    };

    public static string Example(string eventKey) => Examples[eventKey];

    public static string Render()
    {
        var text = new StringBuilder(Intro.ReplaceLineEndings("\n"));
        text.Append("\n## Events\n\n");
        foreach (var key in EventCatalog.AllKeys)
        {
            text.Append(CultureInfo.InvariantCulture, $"### `{key}`\n\n");
            text.Append("| Field | Type | Optional | Description |\n| --- | --- | --- | --- |\n");
            foreach (var field in TemplateFieldCatalog.For(key))
            {
                text.Append(CultureInfo.InvariantCulture, $"| `{field.Name}` | `{field.Type}` | {(field.Optional ? "yes" : "no")} | {field.Description} |\n");
            }

            text.Append("\nExample:\n\n```\n").Append(Examples[key]).Append("\n```\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }
}
