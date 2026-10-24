using System;
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
        # Message templates

        To change a message, open the message on the dashboard's Messages page and change the built-in template.
        This page lists the fields you can use in each template, and the built-in templates.
        The tables below are generated from the plugin's field list, so they always match the plugin.

        ## How templates work

        A template uses the Scriban language. Write plain text and put fields in double braces, like `{{ title }}`.
        You can use `if`, `for` and the other Scriban statements.

        ### Start from the built-in template

        Every message is a template. Even the default messages are built-in templates.
        To change a message, open the message on the dashboard's **Messages** page.
        The editor shows the built-in template. Start there and change what you want.
        Under the template, the Available fields and Formatting tables list what you can use. Click the first cell of a row to insert it at the cursor.
        You see a live preview of the result as you type. Press **Send test** to send it to a destination.
        Press **Reset** to go back to the built-in template.
        The built-in templates are also listed at the end of this page.

        ### What a template replaces

        A template replaces only the text of the message, or its caption when a poster is sent.
        The poster and the buttons still follow your message settings.
        Digests are templates too, and you can edit them like any other message.
        Updates to a message, such as a new episode joining a group, render the same template again with the new data.

        ### Escaping and `raw`

        Every value you print is escaped, so a title like `Tom & Jerry` is safe to send.
        Text you type in the template itself is not escaped, so you can write Telegram HTML tags there.
        The allowed tags are `b`, `strong`, `i`, `em`, `u`, `ins`, `s`, `strike`, `del`, `span class="tg-spoiler"`, `tg-spoiler`, `a`, `code`, `pre`, `blockquote` (also `blockquote expandable`), `tg-emoji` and `tg-time`.
        Every tag must be closed.
        To print a value without escaping it, write `{{ raw value }}`. Use this only for text you trust.

        ### Line breaks: `{{-` and `-}}`

        Every line break you type in a template is printed, also the one at the end of a line that only holds a tag.
        A dash inside the braces removes them. `{{-` removes the line break and spaces just before the tag. `-}}` removes the line break and spaces just after it.
        The dash only removes empty space. It never removes text or a value.

        The built-in templates use this a lot, so a line is printed only when it has something to show.
        Here is the end of the built-in movie template. It prints "♻️ Upgraded" and the library, each on its own line, under a blank line:

        ```
        {{- if upgraded || library }}

        {{ end }}
        {{- if upgraded }}♻️ Upgraded{{ end }}
        {{- if upgraded && library }}
        {{ end }}
        {{- if library }}📁 {{ library }}{{ end -}}
        ```

        Line by line:

        1. `{{- if upgraded || library }}` starts with a dash, so the line break before it is removed. When there is nothing to show, nothing is printed at all. When there is, it prints what sits between the tag and `{{ end }}`: two line breaks, which make the blank line.
        2. `{{- if upgraded }}` removes the line break after the first `{{ end }}`, so that line break is not printed twice. It prints "♻️ Upgraded" right after the blank line.
        3. `{{- if upgraded && library }}` prints one line break, only when both lines are shown. That puts the library on its own line.
        4. `{{- if library }}` prints the library. The `-}}` at the very end removes the last line break of the file, so the message does not end with an empty line.

        To add a line safely, start it with a dash and put its line break inside the `if`:

        ```
        {{- if year }}
        📅 {{ year }}{{ end }}
        ```

        The dash removes the line break before the tag, and the line break inside the `if` is printed only when the year is. So an empty field leaves no empty line.
        A line of plain text without a tag at its start keeps the line break before it.
        Check the preview as you type. An extra empty line means a dash is missing. Two lines run together means a dash too many.

        ### Plural words

        Use `plural` to pick the right word for a number: `{{ plural count "episode" "episodes" }}`.
        The first word is for one, the second for all other numbers.

        ### Limits

        Each render may run for at most 2 seconds and print at most 16384 characters.
        Fitting a message to Telegram's limits can take many renders. All of them together may take at most 5 seconds; after that the template counts as failed with "The template took too long".
        A loop may run at most 1000 times, and calls may nest at most 20 levels deep.
        Telegram allows 1024 characters in a caption and 4096 in a text message.

        ### When a message is too long

        A long message is shortened. A caption is cut to 1024 characters and a text message to 4096. The plugin leaves 40 characters of room for the delayed-delivery line.
        It shortens in this order:

        1. The long text is cut and ends with `…`. This is the `overview` of a movie, music video or book, the `changelog` of a plugin update, or the `error` of a failed install or task. If less than 20 characters would be left, the text is dropped.
        2. The list keeps as many items as fit. A count field tells you how many were left out, so you can print "+N more". The fields are `episodes_more` for `episodes`, `reasons_more` for `reasons`, and `more` for the digest's `libraries`. Each has a `_text` twin with digit grouping, such as `episodes_more_text`, for printing. A digest can end up with no entries at all. If a library is named "Other" and some items have no library, the digest shows two "Other" lines.
        3. If it is still too long, the whole text is cut.

        Your template does not need to handle this. It only needs to print the count field if you want a "+N more" line.
        A result cut in step 3 is flagged "truncated" when the template is yours.

        ### Previews, flags and fallbacks

        The dashboard renders your template live and shows what it would look like in Telegram, with errors and warnings.
        Your template saves itself a second after you stop typing. A template with errors is not saved, and the last saved text stays live until you fix them. Empty text is never saved: use Reset to go back to the built-in template.
        When an edited template fails while a message is sent, the built-in template is sent instead, and your template is flagged with the error.
        A template that produces no text counts as failed.
        If the built-in template fails too, the plugin sends a minimal text with the event name and the item or user name.
        A flag clears when you change the template's text or reset it.

        ### Field types

        - `string` is text.
        - `number` is a whole or decimal number.
        - `bool` is true or false.
        - `string[]` is a list of text. Use `{{ for x in genres }}{{ x }}{{ end }}`.
        - `episode[]` is a list of episodes. Each has a `number`, a `number_text` (the number with two digits, like `03`) and a `title`. `number` and `number_text` are empty for an episode without a number.
        - `library[]` is the digest's list of libraries. Each has a `name`, which is empty for items without a library, and a list of `entries`. The `libraries` field of `media.digest` lists what an entry holds.

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
        ["media.digest"] = "<b>{{ count_text }} new {{ plural count \"item\" \"items\" }}</b>\n{{ for l in libraries }}{{ l.name ?? \"Other\" }}: {{ for e in l.entries }}{{ e.title }} {{ end }}\n{{ end }}{{ if more > 0 }}and {{ more_text }} more{{ end }}",
        ["server.restart_pending"] = "A restart is needed:\n{{ for r in reasons }}- {{ r }}\n{{ end }}{{ if reasons_more > 0 }}+{{ reasons_more_text }} more{{ end }}",
    };

    public static string Example(string eventKey) => Examples[eventKey];

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    public static string Render()
    {
        var text = new StringBuilder(Intro.ReplaceLineEndings("\n"));
        text.Append("\n## Events\n\n");
        foreach (var key in TemplateKeys.All)
        {
            text.Append(CultureInfo.InvariantCulture, $"### `{key}`\n\n");
            text.Append("| Field | Type | Optional | Description |\n| --- | --- | --- | --- |\n");
            foreach (var field in TemplateFieldCatalog.For(key))
            {
                text.Append(CultureInfo.InvariantCulture, $"| `{field.Name}` | `{field.Type}` | {(field.Optional ? "yes" : "no")} | {field.Description} |\n");
            }

            text.Append("\nExample:\n\n```\n").Append(Examples[key]).Append("\n```\n\n");
        }

        text.Append("## Built-in templates\n\n");
        text.Append("These are the templates the plugin uses when you have not edited a message. Copy one into the editor on the Messages page and change it.\n\n");
        foreach (var key in TemplateKeys.All)
        {
            var builtIn = (BuiltInTemplates.Get(key, "en") ?? string.Empty).TrimEnd('\n');
            var fence = new string('`', Math.Max(3, LongestRun(builtIn, '`') + 1));
            text.Append(CultureInfo.InvariantCulture, $"### `{key}`\n\n{fence}\n{builtIn}\n{fence}\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }
}
