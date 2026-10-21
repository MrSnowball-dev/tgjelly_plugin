# TgJelly — every message is a template (design)

Date: 2026-10-21. Status: draft for the owner's review.
Amends the v1 spec `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (§6.6 item 5, §8.1, §8.5, §8.6, §11, §14). Where this document and the v1 spec disagree, this document wins. Everything not mentioned here stays as the v1 spec says.

## 1. Intent

The owner opened the custom template editor in the v0.2 dashboard to see what they could tweak, and found an empty text box. The built-in messages are built in C# (`MediaLayouts`, `AlertLayouts`), so there is no template text to show.

The owner wants **every message the bot sends to be template-driven**, for the easiest changes: open an event, see the exact template the bot uses today, change a word or a line, and check the result. The guide (`docs/templates.md`) stays for deeper changes.

Success: for any event, the editor shows the template in use, a Telegram-like preview of its output, and a way to send it to a real chat. Nobody who leaves the templates alone sees any difference in what the bot sends.

## 2. Goals

1. Every notification is rendered from a Scriban template, including the **digest**. Today digests always use the default layout; that exception goes away.
2. Every event key, and the digest, ships a **built-in template**. Its output must be **identical** to today's default layout for the same input. That covers text and caption, every toggle combination and every shortening case. The owner: "keep them exactly like today".
3. The dashboard editor shows the template in use, with a **Built-in** or **Edited** badge, a live **preview** that looks like a Telegram message, **Send test to** a chosen destination, **Reset to built-in** and **Save**.
4. Long messages shorten themselves as they do today, in the same order, for built-in and edited templates alike: first the overview (or an alert's quoted text), then the list with "+N more", then the end of the text.

## 3. Non-goals

- No change to how messages look. Built-in output stays byte-identical to v0.2.
- No template editing in the bot. Templates stay dashboard-only.
- No templates for the bot's own screens (menus, onboarding, `/status` and so on), or for the destination test message sent at setup. "Every message" means every **notification**.
- No templates for posters or buttons. They still follow the toggles (§8.2–§8.4 of the v1 spec).
- No notice when a plugin update changes a built-in template that the admin has edited (*my call*: YAGNI for now; the edited copy simply stays).
- No new languages. Only `en` exists. The design must not block more (§7).

## 4. Anti-goals (would be a failure even if it works)

- A built-in template that differs from today's output in any case the old layout handled. That includes whitespace, the "+N more" line, `…` placement, toggles and the 1024/4096 fits.
- Built-in templates that are hard to read or tweak, e.g. made of string-table lookups like `{{ t "media.headingYear" title year }}`. The point is that an admin can see "Season", "new episodes" and "📁" right in the text and change them.
- A broken edited template that loses a notification. Today a failing custom template falls back to the default layout. Now it falls back to the **built-in template**, and the editor shows the flag.
- The preview executing anything from the template output as page HTML (see §6.3).

## 5. Rendering

### 5.1 Templates

- **Built-in templates** are embedded resources, one file per key, including the digest's `media.digest`: `Templates/BuiltIn/en/<event key>.sbn`. Their literal text is today's English wording from `en.json`, which moves out of the string table into the templates. Keys the bot still uses stay in `en.json`; keys only the old layouts used are removed.
- **An edited template** is stored in `PluginConfiguration.Templates` (`TemplateOverride`). The editor no longer has an Enabled checkbox, so an override that exists is the template in use. *My call:* keep the property for config compatibility and ignore it; see §8 for migration.
- **Saving** a text identical to the built-in template (after normalizing line endings) removes the override instead of storing a copy, so the badge goes back to Built-in.
- The template in use for a key is the override if there is one, otherwise the built-in template.

### 5.2 The model

- The flat model keeps every field `docs/templates.md` lists today, under the same names, so templates written for v0.2 keep working.
- The builder adds what the built-in templates need to reproduce today's output. Every added field goes into `TemplateFieldCatalog`, with a description and a sample, and into `docs/templates.md`. That includes:
  - pre-formatted values, for example `rating_text` ("7.9", in the configured culture), `runtime_text` ("1h 56m"), `count_text`, and the season label "Season 2" or "Specials";
  - the `two`-digit episode number;
  - the time as a ready `tg-time` element.
  The builder decides whether formatting is done by model fields or by registered functions. Either way:
  - a value that must be inserted as HTML (the `tg-time` element) is a `RawHtml` value, or comes from a function that returns one;
  - plain values stay escaped as the v1 spec §8.5 says.
- **A plural helper is needed**, because "1 new episode" / "8 new episodes" and "and 3 more" are plural forms today. *My call* on its shape, since it goes into every template and can't change later:
  - `{{ plural count "form1" "form2" … }}` takes the forms **positionally**, in the CLDR category order of the configured language: English `one other`; a later Russian table would use `one few many other`.
  - The helper picks the form by that language's rule. Today `StringTable.Plural` is a hard-coded one/other rule. The helper gets a small per-language rule table with only English in it, so a language added later only adds a rule and its forms.
  - With fewer forms than the language has, the last form is used for the missing categories.
  - The helper returns the chosen form only. The number is written by the template, for example `{{ count_text }} new {{ plural count "episode" "episodes" }}`.
- **Words in the template text:** every word a reader sees is written in the built-in template where that is practical, so an admin can change it. That covers "Season", "Specials", "new episodes", "+N more", the digest's "Other" library, "📁" and the alert headlines. *My call:* three exceptions stay pre-formatted, because they are formatting rather than wording:
  - the runtime's "h"/"m" in `runtime_text`;
  - the "UTC" fallback inside the `tg-time` element;
  - the `server.restart_pending` reason lines, which `AlertCoalescer` builds ahead of time and stores in the model that later edits re-render from. They stay strings from `en.json`, given to the template as `reasons[]`.
  The season fields stay in the model as well (`season`, `season_text`) for templates that want them.
- **Toggles:** the builder empties the fields a toggle hides:
  - `ShowRatingGenres` off → `rating`/`rating_text` null and `genres` empty;
  - `ShowOverview` off → `overview` null.
  Templates, built-in or edited, then honor the toggles without checking them.

### 5.3 Shortening

Today `MessageFitter` shortens a `MessageParts` (head, list, overview, tail) in a fixed order. Templates make the text opaque, so shortening moves to the model.

- Each key declares at most one **shrinkable text** and at most one **shrinkable list**:
  - the text is `overview` for media, the changelog or error quote for the plugin and task alerts;
  - the list is `episodes` for `media.episodes`, the reason lines for `server.restart_pending`, and the entries for the digest.
- The fitter renders the template. If the plain length is over the budget (limit minus the 40-code-point delayed reserve), it re-renders with a smaller model, in today's order:
  1. **The shrinkable text** is cut to a rune prefix plus `…`, the longest that fits (binary search), and dropped entirely when the best prefix is under 20 runes. This is today's `MinOverview` rule.
  2. **The shrinkable list** keeps fewer items from the end, and the model's `<list>_more` count field (for example `episodes_more`) is set to the number removed. The template prints the "+N more" line from that field.
  3. If it still doesn't fit, `HtmlText.Truncate(text, budget, "…")`.
- **The digest** today keeps the first K entries across libraries (binary search) and adds "…and N more". With templates, its model is built from the kept entries, grouped by library as today, plus `more` = the number dropped.
- Shortening runs twice per message, as today: once for the caption (1024) and once for the text (4096).
- **Flags:** shortening by steps 1–2 is normal and not flagged. Step 3 on an **edited** template is flagged "truncated", as today. Step 3 on a built-in template is not flagged, matching today, where the default layout was never flagged.
- Parsed templates are cached by (key, hash of text) as today. The extra renders are cheap. A message never needs more than about 15 renders for the text and about one render per list item it removes. The 2 s render timeout applies per render (*my call*); a message whose renders keep failing falls back as in §5.4.

### 5.4 Failure and fallback

- **An edited template that fails at send time** falls back to the built-in template for that key and is flagged, as today. A failure is a parse, runtime or limit error, a whitelist problem, or empty output.
- **A built-in template that fails** is a bug. The renderer logs it and sends a minimal last-resort text: the event's label and the item title or user name, escaped. It never drops the notification.
- **The delayed-delivery line** (`⏱ …`) is still added at send time outside the template, with the 40-code-point reserve kept.

### 5.5 Proving "exactly like today"

This is the core acceptance test, and the old layouts are only deleted after it passes.

1. Before any layout is removed, a test corpus renders every event key and the digest through the **old** `MediaLayouts`/`AlertLayouts`. Output goes to checked-in golden files for both caption and text. The corpus covers:
   - full and sparse data;
   - each toggle on and off;
   - a long overview, a long episode list and long alert quotes that force each shortening step;
   - a digest that overflows;
   - specials (season 0), unnumbered episodes, missing years, `NewSeries`, `Upgraded`, no library name, and a time today versus another day;
   - a login failure with one attempt and with several.
2. The template pipeline must reproduce every golden file byte for byte.
3. Only then are the C# layouts deleted. The golden tests stay as regression tests for the built-in templates.

## 6. The dashboard editor

### 6.1 Placement and layout

- The editor moves out of **Advanced** into its own section, **Message templates**, after Messages (*my call*, agreed with the owner in the playback). Since every message is a template, it is no longer an expert feature.
- **Wide screens:** the template on the left, the preview on the right (owner's choice, mockup A).
- **Narrow screens** (below about 700 px, i.e. phones): one column, with the preview under the template (owner's choice, mockup 1). No tabs.
- **Top:** the event picker, listing every event key plus the digest.
- **Under the template:** the existing "Fields you can use" list and the guide link stay.
- **Buttons:**
  - **Send test to: [destination ▾]**;
  - **Reset to built-in**, shown only when Edited, after a confirmation;
  - **Save**.
- The digest note ("digests always use the default layout") goes away.

### 6.2 The preview

- **Live:** it updates as the admin types, debounced (*my call*: about 500 ms after the last keystroke), by calling the validate endpoint with the box's text.
- **Data:** the full sample model of that key (`SampleFull`), **passed through the same toggles as a real message** (§5.2). With Show overview off, the preview has no overview. With Show poster off, a media key previews as text with the 4096 limit and no poster placeholder. The validate endpoint still runs the sparse trial render too, and its errors show like the others.
- **What it shows:** a Telegram-style message bubble:
  - a poster placeholder when the key has a poster (media keys except the digest);
  - the formatted text, measured and shortened exactly as at send time: as a caption for media keys, as text for the rest;
  - the buttons the sample would get with the current toggles and public URL.
- **Errors** (parse errors with line and column, unknown fields, whitelist problems) show above the preview. The preview keeps the last good render, dimmed.
- **The length line** stays, for example "812 / 1024", with the warning when a media template's full output is over 1024: "will be shortened when sent with a poster".

### 6.3 Preview safety

The preview renders Telegram HTML as formatted text, which v0.2 did not do: it showed escaped source.

- The output has already passed `TelegramHtmlValidator`'s whitelist.
- The dashboard still builds the preview by **parsing the whitelist itself** and creating DOM elements, never by assigning the output to `innerHTML`. Anything outside the whitelist is shown as text.
- How each tag renders:
  - `b`/`strong`, `i`/`em`, `u`/`ins`, `s`/`strike`/`del`, `code`, `pre` → the matching elements;
  - `tg-spoiler`/`span.tg-spoiler` → a blurred span that reveals on click;
  - `blockquote` and `blockquote expandable` → a quote; an expandable one starts collapsed;
  - `a` → a link with `rel="noopener noreferrer"` and `target="_blank"`, and only `http(s)`/`tg` URLs (others render as text);
  - `tg-emoji` → its fallback emoji;
  - `tg-time` → its fallback text.

### 6.4 Send test

- **Send test to** lists the configured destinations by title and sends the template **in the box, saved or not**, so a change can be tried in Telegram before saving.
- **Data:** the same full sample model as the preview.
- **The message** is sent as a real notification of that key would be, with the sample passed through the toggles as in §6.2:
  - the shortening and the caption/text choice;
  - the destination's silent setting, and its topic;
  - the buttons from the toggles;
  - *my call*: a **sample poster** for media keys when Show poster is on: `logo.png`, which is added to the plugin as an embedded resource (today it is only in `build/` and on Pages). So the caption limit and the photo layout are really tested. The sample's "Open in Jellyfin" button points to the public URL's web root. IMDb and TMDb use the sample IDs.
- **No "test" marker** is added, so the message looks exactly as it would. The dropdown's label says it's a test, and the dashboard notes that the destination's members will see it.
- **It doesn't go through the Outbox.** It is a direct send, like the destination test at setup. The result appears next to the button: "Sent" or the Telegram error. Templates that fail validation can't be sent; the button is disabled while errors show.
- **API:** `POST Templates/Test` `{eventKey, text, destinationId}` → the same `TestResultDto` shape as the destination test.

## 7. Languages

- Built-in templates are stored per language (`BuiltIn/<lang>/…`).
- The plugin picks `PluginConfiguration.Language`, falling back to `en` for a key with no file in that language.
- Edited templates are not per language.
- Only `en` ships now.

## 8. Migration from v0.2

- An override with `Enabled = true` and non-empty text becomes an **Edited** template.
- An override with `Enabled = false`, or empty text, is **dropped** on load (*my call*: the new editor has no place for a disabled draft, and v0.2 is a pre-test release whose only user is the owner).
- Template flags carry over unchanged.

## 9. API changes

- `GET Templates`: each item gains `BuiltIn` (the built-in text) and `Edited` (bool), and `Text` becomes the text in use. `Enabled` is removed from the DTO.
- `PUT Templates/{key}` `{text, revision}`: saves, and text equal to the built-in removes the override (§5.1). `DELETE Templates/{key}` = Reset.
- `POST Templates/Validate`: also returns the preview's rendered HTML (fitted as at send time), the message type (caption or text), whether there's a poster, and the button rows.
- `POST Templates/Test`: new (§6.4).
- The digest's template key is its existing event key, `media.digest` (as in `NotificationPipeline`).

## 10. Docs

- `docs/templates.md` gains the new fields, the plural helper, the shrinkable fields and `<list>_more` counts.
- It also gains a short "start from the built-in template" section and the built-in template of each key, generated from the embedded files so it cannot drift.
- The README's mention of custom templates is updated to say every message is a template.

## 11. Left to the builder

- Whether formatting is done by model fields or by registered Scriban functions (§5.2), and the plural helper's exact syntax.
- How the fitter asks the model builder for a smaller model, for example a callback taking (text length, items kept).
- The preview's exact styling, as long as it reads as a Telegram message in Jellyfin's dark and light themes.
- File and class layout for the built-in templates and the new endpoints.

## 12. Manual checks for the owner (added to v1 spec §14.4)

- Open each event in the editor and check that the built-in template reads naturally and is easy to tweak.
- Edit the Movie template, preview it, send a test to a channel and to a forum topic, save, and wait for a real movie to arrive.
- Reset it, and confirm that the next real movie looks like v0.2.
- Check the editor on a phone (one column, preview under the template).
