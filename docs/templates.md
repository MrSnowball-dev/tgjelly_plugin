# Message templates

To change a message, open the event in the dashboard's Templates section and change the built-in template.
This page lists the fields you can use in each template, and the built-in templates.
The tables below are generated from the plugin's field list, so they always match the plugin.

## How templates work

A template uses the Scriban language. Write plain text and put fields in double braces, like `{{ title }}`.
You can use `if`, `for` and the other Scriban statements.

### Start from the built-in template

Every message is a template. Even the default messages are built-in templates.
To change a message, open the event in the dashboard's **Templates** section.
The editor shows the built-in template. Start there and change what you want.
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

## Events

### `media.movie`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `title` | `string` | no | The title. |
| `year` | `number` | yes | The production year. |
| `overview` | `string` | yes | The overview text. |
| `rating` | `number` | yes | The community rating, from 0 to 10. |
| `rating_text` | `string` | yes | The rating with one decimal, e.g. 7.9. |
| `runtime_minutes` | `number` | yes | The runtime in minutes. |
| `runtime_text` | `string` | yes | The runtime as hours and minutes, e.g. 1h 56m. |
| `genres` | `string[]` | no | The genres. |
| `library` | `string` | yes | The library name. |
| `imdb_id` | `string` | yes | The IMDb ID. |
| `tmdb_id` | `string` | yes | The TMDb ID. |
| `jellyfin_url` | `string` | yes | A link that opens the item in Jellyfin. Empty when no public URL is set. |
| `upgraded` | `bool` | no | True when the item replaces an older file. |

Example:

```
<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}
{{ for g in genres }}{{ g }} {{ end }}
{{ if runtime_minutes }}{{ runtime_minutes }} min{{ end }}
```

### `media.episodes`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `series` | `string` | yes | The series name. |
| `season` | `number` | yes | The season number. Specials are 0. |
| `count` | `number` | no | How many episodes the message covers. |
| `count_text` | `string` | no | The episode count with digit grouping. |
| `episodes` | `episode[]` | no | The episodes, in number order. Each has a number (empty for an episode without one), number_text (the number with two digits, empty without a number) and a title. |
| `episodes_more` | `number` | no | How many episodes were left out of the list to fit the message. 0 when none. |
| `episodes_more_text` | `string` | no | The left-out episode count with digit grouping. |
| `new_series` | `bool` | no | True when the series is new to the library. |
| `library` | `string` | yes | The library name. |
| `imdb_id` | `string` | yes | The IMDb ID. |
| `tmdb_id` | `string` | yes | The TMDb ID. |
| `jellyfin_url` | `string` | yes | A link that opens the item in Jellyfin. Empty when no public URL is set. |
| `upgraded` | `bool` | no | True when the item replaces an older file. |

Example:

```
{{ if new_series }}New series! {{ end }}<b>{{ series }}</b> season {{ season }}
{{ for e in episodes }}{{ e.number }}. {{ e.title }}
{{ end }}
```

### `media.album`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `artist` | `string` | yes | The album artist. |
| `album` | `string` | no | The album title. |
| `year` | `number` | yes | The release year. |
| `tracks` | `number` | no | The number of tracks. |
| `tracks_text` | `string` | no | The track count with digit grouping. |
| `library` | `string` | yes | The library name. |
| `jellyfin_url` | `string` | yes | A link that opens the item in Jellyfin. Empty when no public URL is set. |
| `upgraded` | `bool` | no | True when the item replaces an older file. |

Example:

```
<b>{{ artist }}</b> - {{ album }}
{{ tracks }} tracks
```

### `media.musicvideo`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `title` | `string` | no | The title. |
| `year` | `number` | yes | The production year. |
| `overview` | `string` | yes | The overview text. |
| `rating` | `number` | yes | The community rating, from 0 to 10. |
| `rating_text` | `string` | yes | The rating with one decimal, e.g. 7.9. |
| `runtime_minutes` | `number` | yes | The runtime in minutes. |
| `runtime_text` | `string` | yes | The runtime as hours and minutes, e.g. 1h 56m. |
| `genres` | `string[]` | no | The genres. |
| `library` | `string` | yes | The library name. |
| `imdb_id` | `string` | yes | The IMDb ID. |
| `tmdb_id` | `string` | yes | The TMDb ID. |
| `jellyfin_url` | `string` | yes | A link that opens the item in Jellyfin. Empty when no public URL is set. |
| `upgraded` | `bool` | no | True when the item replaces an older file. |

Example:

```
<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}
{{ if library }}Added to {{ library }}{{ end }}
```

### `media.book`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `title` | `string` | no | The title. For an audiobook, the name of the book. |
| `year` | `number` | yes | The year. |
| `overview` | `string` | yes | The overview text. |
| `rating` | `number` | yes | The community rating, from 0 to 10. |
| `rating_text` | `string` | yes | The rating with one decimal, e.g. 8.2. |
| `genres` | `string[]` | no | The genres. |
| `parts` | `number` | no | The number of audiobook parts. 0 for a book. |
| `parts_text` | `string` | no | The part count with digit grouping. |
| `library` | `string` | yes | The library name. |
| `jellyfin_url` | `string` | yes | A link that opens the item in Jellyfin. Empty when no public URL is set. |
| `upgraded` | `bool` | no | True when the item replaces an older file. |

Example:

```
<b>{{ title }}</b>
{{ if parts > 0 }}Audiobook, {{ parts }} parts{{ else }}Book{{ end }}
```

### `security.login_failed`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name that was tried. |
| `attempts` | `number` | yes | How many attempts failed in a row. |
| `attempts_text` | `string` | yes | The attempt count with digit grouping. |
| `ip_count` | `number` | yes | How many different addresses the attempts came from. |
| `ip_count_text` | `string` | yes | The address count with digit grouping. |
| `last_ip` | `string` | yes | The address of the latest attempt. |
| `ip` | `string` | yes | The address of the latest attempt. Same as last_ip. |
| `client` | `string` | yes | The client app. |
| `device` | `string` | yes | The device name. |
| `last_attempt_unix` | `number` | no | When the latest attempt happened, as Unix seconds. |
| `last_attempt_text` | `string` | no | When the latest attempt happened, as text in UTC. |
| `locked` | `bool` | no | True when the account is now locked. |

Example:

```
Failed sign-in for <b>{{ user }}</b>: {{ attempts }} attempts from {{ ip_count }} addresses.
Last: {{ last_ip }} at {{ last_attempt_text }}{{ if locked }}
The account is locked.{{ end }}
```

### `security.lockout`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name. |
| `attempts` | `number` | yes | How many attempts failed. |
| `attempts_text` | `string` | yes | The attempt count with digit grouping. |

Example:

```
<b>{{ user }}</b> is locked out after {{ attempts }} failed attempts.
```

### `security.login_succeeded`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name. |
| `client` | `string` | yes | The client app. |
| `device` | `string` | yes | The device name. |
| `ip` | `string` | yes | The address of the sign-in. |

Example:

```
<b>{{ user }}</b> signed in with {{ client }} on {{ device }}.
```

### `security.user_created`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name. |

Example:

```
User <b>{{ user }}</b> was created.
```

### `security.user_deleted`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name. |

Example:

```
User <b>{{ user }}</b> was deleted.
```

### `security.password_changed`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `user` | `string` | no | The user name. |

Example:

```
The password of <b>{{ user }}</b> was changed.
```

### `server.started`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `version` | `string` | yes | The Jellyfin version. |
| `server` | `string` | yes | The server name. |

Example:

```
Jellyfin {{ version }} started on {{ server }}.
```

### `server.update_available`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `version` | `string` | no | The new Jellyfin version. |
| `current` | `string` | yes | The version that is running. |
| `url` | `string` | yes | A link to the release notes. |

Example:

```
Jellyfin {{ version }} is available. You run {{ current }}.
{{ url }}
```

### `server.plugin_installed`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `plugin` | `string` | no | The plugin name. |
| `version` | `string` | yes | The plugin version. |

Example:

```
Plugin <b>{{ plugin }}</b> {{ version }} was installed.
```

### `server.plugin_updated`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `plugin` | `string` | no | The plugin name. |
| `version` | `string` | yes | The plugin version. |
| `changelog` | `string` | yes | The plugin's change notes. |

Example:

```
Plugin <b>{{ plugin }}</b> is now {{ version }}.
<blockquote>{{ changelog }}</blockquote>
```

### `server.plugin_uninstalled`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `plugin` | `string` | no | The plugin name. |
| `version` | `string` | yes | The plugin version. |

Example:

```
Plugin <b>{{ plugin }}</b> {{ version }} was removed.
```

### `server.plugin_install_failed`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `plugin` | `string` | no | The plugin name. |
| `version` | `string` | yes | The plugin version. |
| `error` | `string` | yes | The error message. |

Example:

```
Plugin <b>{{ plugin }}</b> {{ version }} failed to install.
<blockquote>{{ error }}</blockquote>
```

### `server.task_failed`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `task` | `string` | no | The name of the scheduled task. |
| `error` | `string` | yes | The error message. |

Example:

```
Task <b>{{ task }}</b> failed.
<blockquote>{{ error }}</blockquote>
```

### `server.restart_pending`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `reasons` | `string[]` | no | Why a restart is needed, as plain text. |
| `reasons_more` | `number` | no | How many reasons were left out of the list to fit the message. 0 when none. |
| `reasons_more_text` | `string` | no | The left-out reason count with digit grouping. |

Example:

```
A restart is needed:
{{ for r in reasons }}- {{ r }}
{{ end }}{{ if reasons_more > 0 }}+{{ reasons_more_text }} more{{ end }}
```

### `media.digest`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `at_html` | `string` | no | When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped. |
| `count` | `number` | no | How many items the digest covers. |
| `count_text` | `string` | no | The item count with digit grouping. |
| `libraries` | `library[]` | no | The libraries, each with a name (empty for items without a library) and entries. An entry has kind (movie, episode, season, album, audiobook or other), title, year, series, season_text, episode_text (two digits, or ?), count, count_text, artist and album. |
| `more` | `number` | no | How many entries were left out to fit the message. 0 when none. |
| `more_text` | `string` | no | The left-out count with digit grouping. |

Example:

```
<b>{{ count_text }} new {{ plural count "item" "items" }}</b>
{{ for l in libraries }}{{ l.name ?? "Other" }}: {{ for e in l.entries }}{{ e.title }} {{ end }}
{{ end }}{{ if more > 0 }}and {{ more_text }} more{{ end }}
```

## Built-in templates

These are the templates the plugin uses when you have not edited a message. Copy one into the editor and change it.

### `media.movie`

```
<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}
{{- details = []
    if rating_text
      details = details | array.add ("★ " + rating_text)
    end
    if runtime_text
      details = details | array.add runtime_text
    end
    if genres.size > 0
      details = details | array.add (genres | array.join ", ")
    end }}
{{- if details.size > 0 }}
{{ details | array.join " · " }}
{{- end }}
{{- if overview }}

{{ overview }}
{{- end }}
{{- if upgraded || library }}

{{ end }}
{{- if upgraded }}♻️ Upgraded{{ end }}
{{- if upgraded && library }}
{{ end }}
{{- if library }}📁 {{ library }}{{ end -}}
```

### `media.episodes`

```
{{ if new_series -}}
🆕 New series
{{ end -}}
<b>{{ series }}</b> · {{ if season == 0 }}Specials{{ else }}Season {{ season ?? "?" }}{{ end }} · {{ count_text }} new {{ plural count "episode" "episodes" }}
{{- for episode in episodes }}
{{ if for.first }}
{{ end }}
{{- if episode.number_text }}E{{ episode.number_text }} {{ end }}{{ episode.title }}
{{- end }}
{{- if episodes_more > 0 }}
+{{ episodes_more_text }} more
{{- end }}
{{- if upgraded || library }}

{{ end }}
{{- if upgraded }}♻️ Upgraded{{ end }}
{{- if upgraded && library }}
{{ end }}
{{- if library }}📁 {{ library }}{{ end -}}
```

### `media.album`

```
<b>{{ artist }} — {{ album }}</b>{{ if year }} ({{ year }}){{ end }}
{{ tracks_text }} {{ plural tracks "track" "tracks" }}
{{- if upgraded || library }}

{{ end }}
{{- if upgraded }}♻️ Upgraded{{ end }}
{{- if upgraded && library }}
{{ end }}
{{- if library }}📁 {{ library }}{{ end -}}
```

### `media.musicvideo`

```
<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}
{{- details = []
    if rating_text
      details = details | array.add ("★ " + rating_text)
    end
    if runtime_text
      details = details | array.add runtime_text
    end
    if genres.size > 0
      details = details | array.add (genres | array.join ", ")
    end }}
{{- if details.size > 0 }}
{{ details | array.join " · " }}
{{- end }}
{{- if overview }}

{{ overview }}
{{- end }}
{{- if upgraded || library }}

{{ end }}
{{- if upgraded }}♻️ Upgraded{{ end }}
{{- if upgraded && library }}
{{ end }}
{{- if library }}📁 {{ library }}{{ end -}}
```

### `media.book`

```
{{ if parts > 0 -}}
<b>{{ title }}</b>
{{ parts_text }} {{ plural parts "part" "parts" }}
{{- else -}}
<b>{{ title }}</b>{{ if year }} ({{ year }}){{ end }}
{{- details = []
    if rating_text
      details = details | array.add ("★ " + rating_text)
    end
    if genres.size > 0
      details = details | array.add (genres | array.join ", ")
    end }}
{{- if details.size > 0 }}
{{ details | array.join " · " }}
{{- end }}
{{- if overview }}

{{ overview }}
{{- end }}
{{- end }}
{{- if upgraded || library }}

{{ end }}
{{- if upgraded }}♻️ Upgraded{{ end }}
{{- if upgraded && library }}
{{ end }}
{{- if library }}📁 {{ library }}{{ end -}}
```

### `security.login_failed`

```
🔐 <b>Wrong password for {{ user }}</b>
{{- if attempts > 1 }}
×{{ attempts_text }} from {{ ip_count_text }} {{ plural ip_count "IP" "IPs" }} · last {{ ip }} · {{ client }} · {{ at_html }}
{{- else }}
{{ if ip }}{{ ip }} · {{ end }}{{ if client }}{{ client }} · {{ end }}{{ if device }}{{ device }} · {{ end }}{{ at_html }}
{{- end }}
{{- if locked }}
🔒 locked
{{- end -}}
```

### `security.lockout`

```
🔒 <b>{{ user }}</b> is locked out after {{ attempts_text }} failed {{ plural attempts "attempt" "attempts" }}
```

### `security.login_succeeded`

```
🔐 <b>{{ user }} signed in</b>
{{- if client || device }}
{{ if client }}{{ client }}{{ end }}{{ if client && device }} · {{ end }}{{ if device }}{{ device }}{{ end }}
{{- end }}
{{- if ip }}
🌐 {{ ip }}
{{- end }}
🕒 {{ at_html }}
```

### `security.user_created`

```
🔐 <b>New user: {{ user }}</b>
```

### `security.user_deleted`

```
🔐 <b>User deleted: {{ user }}</b>
```

### `security.password_changed`

```
🔐 <b>Password changed for {{ user }}</b>
```

### `server.started`

```
✅ Jellyfin is up · {{ version }} · <i>{{ server }}</i>
```

### `server.update_available`

```
⬆️ Jellyfin <b>{{ version }}</b> is available (running {{ current }})
```

### `server.plugin_installed`

```
⚙️ <b>Plugin installed: {{ plugin }} {{ version }}</b>
```

### `server.plugin_updated`

```
⚙️ <b>Plugin updated: {{ plugin }} {{ version }}</b>
{{- if changelog }}

<blockquote expandable>{{ changelog }}</blockquote>
{{- end -}}
```

### `server.plugin_uninstalled`

```
⚙️ <b>Plugin removed: {{ plugin }} {{ version }}</b>
```

### `server.plugin_install_failed`

```
⚙️ <b>Plugin install failed: {{ plugin }} {{ version }}</b>
{{- if error }}

<blockquote expandable>{{ error }}</blockquote>
{{- end -}}
```

### `server.task_failed`

```
⚙️ <b>Task failed: {{ task }}</b>
{{- if error }}

<blockquote expandable>{{ error }}</blockquote>
{{- end -}}
```

### `server.restart_pending`

```
⚙️ <b>Jellyfin needs a restart</b>
{{- if reasons.size > 0 }}
{{ end }}
{{- for reason in reasons }}
{{ reason }}
{{- end }}
{{- if reasons_more > 0 }}
+{{ reasons_more_text }} more
{{- end -}}
```

### `media.digest`

```
🆕 <b>{{ count_text }} new {{ plural count "item" "items" }} in Jellyfin</b>

{{ for library in libraries -}}
{{ if !for.first }}
{{ end -}}
<b>{{ library.name ?? "Other" }}</b>: {{ for entry in library.entries -}}
{{ if !for.first }}, {{ end -}}
{{ if entry.kind == "episode" -}}
{{ entry.series }} S{{ entry.season_text }}E{{ entry.episode_text }}
{{- else if entry.kind == "season" -}}
{{ entry.series }} S{{ entry.season_text }} ({{ entry.count_text }} {{ plural entry.count "episode" "episodes" }})
{{- else if entry.kind == "album" -}}
{{ entry.artist }} — {{ entry.album }}
{{- else if entry.kind == "audiobook" -}}
{{ entry.title }}
{{- else -}}
{{ entry.title }}{{ if entry.year }} ({{ entry.year }}){{ end }}
{{- end }}
{{- end }}
{{- end }}
{{- if more > 0 }}
{{- if libraries.size > 0 }}
{{ end -}}
…and {{ more_text }} more
{{- end -}}
```
