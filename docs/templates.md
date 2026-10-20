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

The dashboard checks a template when you save it.
It renders the template twice, once with a full sample and once with missing data, and reports unknown fields.
When a template fails while a message is sent, the default layout is sent instead, and the template is flagged with the error.
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

## Events

### `media.movie`

| Field | Type | Optional | Description |
| --- | --- | --- | --- |
| `event` | `string` | no | The event key. |
| `at_unix` | `number` | no | When the event happened, as Unix seconds. |
| `at_text` | `string` | no | When the event happened, as text in UTC. |
| `title` | `string` | no | The title. |
| `year` | `number` | yes | The production year. |
| `overview` | `string` | yes | The overview text. |
| `rating` | `number` | yes | The community rating, from 0 to 10. |
| `runtime_minutes` | `number` | yes | The runtime in minutes. |
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
| `series` | `string` | yes | The series name. |
| `season` | `number` | yes | The season number. Specials are 0. |
| `count` | `number` | no | How many episodes the message covers. |
| `episodes` | `episode[]` | no | The episodes, in number order. Each has a number (empty for an episode without one) and a title. |
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
| `artist` | `string` | yes | The album artist. |
| `album` | `string` | no | The album title. |
| `year` | `number` | yes | The release year. |
| `tracks` | `number` | no | The number of tracks. |
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
| `title` | `string` | no | The title. |
| `year` | `number` | yes | The production year. |
| `overview` | `string` | yes | The overview text. |
| `rating` | `number` | yes | The community rating, from 0 to 10. |
| `runtime_minutes` | `number` | yes | The runtime in minutes. |
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
| `title` | `string` | no | The title. For an audiobook, the name of the book. |
| `year` | `number` | yes | The year. |
| `overview` | `string` | yes | The overview text. |
| `parts` | `number` | no | The number of audiobook parts. 0 for a book. |
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
| `user` | `string` | no | The user name that was tried. |
| `attempts` | `number` | yes | How many attempts failed in a row. |
| `ip_count` | `number` | yes | How many different addresses the attempts came from. |
| `last_ip` | `string` | yes | The address of the latest attempt. |
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
| `user` | `string` | no | The user name. |
| `attempts` | `number` | yes | How many attempts failed. |

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
| `reasons` | `string[]` | no | Why a restart is needed, as plain text. |

Example:

```
A restart is needed:
{{ for r in reasons }}- {{ r }}
{{ end }}
```
