# TgJelly — dashboard tweaks, per-message options and the Log (design)

Date: 2026-10-23. Status: draft for the owner's review.

This amends the dashboard redesign (`2026-10-22-tgjelly-dashboard-redesign-design.md`), the templates design (`2026-10-21-tgjelly-templates-design.md`) and the v1 spec (`2026-10-16-tgjelly-v1-design.md`). Where they disagree, this document wins. Everything those documents settle and this one doesn't touch stays as it is: auto-save and its timing, one page-wide queue and `Revision`, scoped CSS on Jellyfin's palette, no framework, and no browser dialogs.

## 1. Intent

The owner tried v0.4 (the redesigned dashboard) and asked for a round of tweaks, plus one new feature, a Log.

Their overall verdict: "you've gone too far on 'plain'". The page reads flat. Statuses are bare words, and settings are spread across tabs that belong together. Messages and Templates in particular make the owner hop between two sections to set up one message.

The Log answers one question: **is everything working, and if not, where did it break?** The owner wants to compare what TgJelly sent with what actually arrived in Telegram. When the bot is quiet, the Log should show whether nothing happened or something failed: Telegram rejected the message, the connection failed, or another error. The owner: "I need them all to be clear and upfront, no 'Unknown error please try again' anywhere."

Success means:
- statuses look like the outlined Built-in/Edited labels the owner liked;
- each message is set up on one page;
- every failure the owner meets says what happened and what to do;
- the Log explains every silence.

The owner will check it on the next pre-release.

## 2. Goals

1. **Labels:** every status in the dashboard uses the outlined, coloured pill style of today's Built-in/Edited badge (§3).
2. **Overview:** remove "At a glance", move the version into the header, and turn Access into a notice (§4).
3. **Destinations:**
   - health shows only when something is wrong;
   - the event switches are separated by group, without repeated prefixes;
   - the disconnect confirmation offers to remove the destinations (§5).
4. **Messages** absorbs Templates. One page per message holds what it shows (per-message switches), its template, an exact Telegram preview, and expandable tables of fields and formatting you can click to insert. A hover links template pieces to their output (§6).
5. **Advanced:**
   - proxy credentials appear only with a proxy;
   - update checks get finer choices;
   - Batching moves here;
   - Diagnostics says what it collects (§7).
6. **Log:** a new top-level section listing every event, per destination, with its outcome and a plain reason (§8).
7. **Clear errors everywhere:** no vague error text in the dashboard or the bot (§9).

## 3. Labels

Today's `.tgj-badge` is a pill with a 1px outline in a soft tint of the state colour, the text in the state colour, and no fill. It becomes the one way the dashboard shows any state. Words, not symbols, as before.

| State | Colour | Examples |
|---|---|---|
| good | success | Saved, Connected, Sent |
| in progress | accent | Saving…, Waiting, Connecting |
| neutral | muted | Built-in, Skipped, Off |
| attention | warning | Photos blocked, Sent late, Sent with built-in |
| bad | error | Not saved, Failed, Lost, Needs permissions |
| accent | accent | Edited |

- The auto-save cue becomes a label: "Saved", "Saving…", "Not saved".
  - The reason after "Not saved" stays as plain error-coloured text next to the label, so it can be long.
  - Timing is unchanged: "Saved" fades after 2000 ms, and "Saving…" appears after 300 ms in flight.
- The header's connection state, destination health, Log results and template badges all use it.
- The no-symbol rule still holds. A label is a word in an outline.

## 4. Overview and header

- **Version:** remove the "TgJelly version" line from the status block. Show the version as a small label next to "TgJelly" in the page header, e.g. `TgJelly  v0.5`, on every section.
- **"At a glance":** remove the card.
- **Access:** no longer a card of its own. It becomes an info notice inside Overview: the same text that explains BotFather's Mini App access restriction, styled as a notice with the info tint.
- The rest of Overview stays: Connect when no bot is connected, the connection state, and notices.

## 5. Destinations

### 5.1 Health only when wrong

- A healthy destination shows no health label at all. The owner: "if the plugin is connected it is implied that everything is ok, only show wrongs."
- Photos blocked, Needs permissions, Topic missing and Lost show as labels (§3) in the row and in the editor.
- The bot is unchanged: its Destinations screen keeps its current health display.

### 5.2 Event switches by group

- In the editor, events are grouped into separate blocks: **Media**, **Security**, **Server**.
  - Each group is its own card, or a clearly separated sub-card, with the group name as its title.
  - Each block has a group-level "all on/off" switch, if one exists today; otherwise there is none.
- Options inside a group don't repeat the group name. "Media: New movie" becomes "New movie" under Media.
  - The dashboard gets its own `web.*` labels where today it shares prefixed bot strings. The bot's strings stay unchanged.

### 5.3 Disconnect keeps destinations, with an option to remove them

- Disconnecting the bot keeps destinations, as it does today (`BotStateReset.ForDisconnect` keeps them).
- The inline disconnect confirmation gains a switch: **"Also remove all destinations"**, off by default.
  - On: the disconnect also deletes every destination and its per-destination state.
  - Off: they stay, and work again after reconnecting the same bot. A different bot marks them Lost, as today.
- The confirmation text says what each choice keeps.
- The API: `DELETE Token` gains an optional flag, for example `?removeDestinations=true`. No flag means keep.

## 6. Messages (Messages and Templates merged)

### 6.1 One section, one page per message

- The sidebar's **Templates** item goes away. **Messages** holds everything about messages.
- At the top are two pickers:
  - **Section**: Media, Security, Server;
  - **Message**: the events in that section, plus "Digest" under Media.
- The Built-in/Edited label sits next to the pickers. The dropdown options carry no "(Edited)" suffix.
- The current message persists in the page address, the way the section does today.
- The page, top to bottom:
  - **What it shows** (§6.2).
  - **Template:** the template text at full width, with its Saved cue, the length and the errors. Below it are Send test and Reset. Their behaviour is unchanged from v0.4.
  - **Preview:** an exact Telegram rendering (§6.4).
    - On wide screens it sits beside the first two cards and stays in view on scroll.
    - On narrow screens it sits under the template.
  - **Available fields** and **Formatting:** tables (§6.3).
- **The public URL** lives inside the Open in Jellyfin switch (§6.2).
  - It is shown only while that switch is on for the current message.
  - It is still one global setting, so its hint says it applies to every message's button.
  - Validation and blur-or-Enter saving are unchanged.
- **Batching** moves to Advanced (§7).

### 6.2 Per-message switches

- Today `MessageOptions` holds one set of switches for all media messages: Poster, Overview, Rating and genres, Open in Jellyfin, Provider links, and Announce upgrades. They become **per message**, keyed by event key.
- **Media messages** (movie, episodes, album, music video, book, digest) each get the switches that apply to them:
  - Poster;
  - Overview;
  - Rating and genres;
  - Open in Jellyfin;
  - IMDb/TMDB buttons (Provider links).
  - Announce upgrades applies only where upgrades exist. Whether it stays global or goes per message is the builder's call (§11); it must stay reachable from the message it affects.
  - A switch that makes no sense for an event is hidden for that event.
- **Security and Server messages** get only **Poster**, off by default.
  - When on, the message is sent as a photo of the existing plugin logo, `build/logo.png` (1280×720, the same image as `gh-pages/logo.png`), with the text as its caption.
  - The logo is embedded in the plugin and cached by Telegram file_id like any poster.
  - All other switches are hidden for these messages.
- **Upgrade:**
  - Each media message's switches start with the global value it had in v0.4, so nothing a user sees changes after the upgrade.
  - Security and Server Poster starts off.
  - The global `MessageOptions` is migrated and removed from the settings DTO, or kept only as the migration source (builder's call).
- **Everything that reads these switches reads the per-message value:**
  - the pipeline's layouts;
  - the template model's emptying of hidden fields;
  - the template sample and preview;
  - Diagnostics;
  - the bot.
- **The bot:**
  - its **Message style** screen gains the same two-step pick (section, then message) before the switches, using inline buttons and explaining each button per the existing bot rules;
  - its own strings keep their icons.
- **Saving:** switches save at once through the page queue, each with its own cue.

### 6.3 Fields and Formatting tables

- **Two tables under the template:**
  - **Available fields** for the current message: Field, What it is, Example. The example is the field's value in the sample data, and it notes "optional" where the field can be empty.
  - **Formatting**: Write, What it does, Looks like.
    - It lists the allowed tags: bold, italic, underline, strikethrough, spoiler, link, code, pre, quote, and expandable quote.
    - It also lists the template constructs people need: `{{ if field }}…{{ end }}`, `{{ plural n "a" "b" }}`, `raw`, and the `{{-`/`-}}` line-break trimming.
    - "Looks like" renders the example in the Telegram preview style.
- **Inserting:**
  - **Clicking the first cell** (the field or the snippet) inserts it at the textarea's cursor.
  - For formatting, a click with text selected wraps the selection. With no selection, it inserts the snippet and selects its placeholder text.
  - There are no separate Insert buttons.
  - The cell looks clickable, with a hover state, a pointer and keyboard focus. Enter or Space activates it.
- **After an insert:**
  - the textarea keeps focus, and the caret lands after the insert;
  - it counts as typing, so the debounced save and the preview follow;
  - undo (Ctrl+Z) should undo the insert, if the browser allows it via `execCommand('insertText')` or an equivalent. If not, that's acceptable.
- **Both tables are expandable:** collapsed by default, opened by clicking their title. The open or closed state is remembered per viewer.
- The template guide link stays.

### 6.4 The preview is exactly Telegram

- The preview copies Telegram's own dark-theme rendering, as in the owner-approved mockup:
  - **Bubble:** Telegram's colours, corner radius and tail, and the time at the bottom right inside the bubble.
  - **Photo messages:** the photo on top with the caption under it, in the same bubble.
  - **Inline keyboard:** separate rounded keys **under** the bubble, not inside it. Each key has a translucent dark fill, white centred text, and the small ↗ mark at the top right for URL buttons. Keys are laid out in Telegram's rows (the real `reply_markup` row structure).
  - **Formatting:** spoiler as Telegram's blurred or dotted block, quotes with the left bar, links in Telegram's link blue, and code in monospace.
- It stays exempt from the palette-token rule (redesign Ruling R3), with its `tgj-tg-` prefixed classes.
- The sanitiser (`previewHtml`) keeps its behaviour and its tests.

### 6.5 Hover link between template and preview

- Hovering a piece of the template highlights the output it produced in the preview, and the other way round.
- **A piece** is one `{{ … }}` output expression, one `{{ if }}…{{ end }}` block (as a whole), or one run of literal text between expressions. Several fields on one line are separate pieces. The owner: "at least 'section' level, because we can have several fields in one line."
- **Highlight:** a soft accent background with an outline, on both sides at once. Moving the pointer away clears it.
- **How the mapping is made is the builder's call.** One workable way: the preview render (Validate, preview mode only) wraps each piece's output in invisible markers carrying the piece's source span. The page strips the markers into `data-` spans after sanitising. The sent message never contains markers.
- **Pieces with no output** (an `if` whose condition is false) highlight only on the template side.
- **Truncation:** when shortening cut a piece's output, the visible part still maps.
- **The textarea** can't style ranges itself. The builder may use an overlay mirror behind a transparent textarea, or another technique, as long as the caret, selection, scrolling and typing are never disturbed (redesign: "typing is never interrupted").

## 7. Advanced

- **Proxy:** the Username and Password fields appear only when the proxy type is SOCKS5 or HTTP, not with no proxy. Hiding them doesn't clear them.
- **MTProto proxy: not added.** The plugin talks to Telegram through the Bot API over HTTPS, and MTProto proxies carry only the client protocol, so a bot can't use one. The owner raised it as a "maybe". A one-line hint under the proxy type says only SOCKS5 and HTTP proxies work for bots.
- **Update checks:** "Notify about" becomes three switches:
  - **Fixes for my version**, e.g. 10.11.1 → 10.11.2: same first two version numbers, higher third.
  - **New releases**, e.g. 10.11 → 10.12: same first number, higher second.
  - **New major versions**, e.g. 10.x → 12.0: higher first number.
  - **Upgrade:** `SameMajor` becomes the first two on; `AnyNewer` becomes all three on.
  - All three off is allowed and means no update messages. The page says so in the hint, because the Check for updates switch stays.
  - The bot's Update checks screen gets the same three toggles.
  - Fix the bot's wording: it says it checks for plugin releases, but the checker watches **Jellyfin** releases.
- **Batching:** the card moves here from Messages, unchanged.
- **Diagnostics:** an info notice above Copy diagnostics says exactly what the report contains and why.
  - The "what" list comes from `DiagnosticsReport` as built: versions, settings, destination ids and health, recent errors, and so on. Write the list from the code, not from memory.
  - It states what is never included: the bot token, and message contents beyond what the report holds.
  - The why: "to paste into a bug report or a support request".

## 8. Log

### 8.1 What the owner sees

- **Log** is a top-level sidebar section, **after Advanced**. On a phone it is the last tab.
- **Lead text:** "Every event TgJelly received, and what happened to it, for the last 7 days." (Builder: the retention may differ from 7 days, and the text must match it.)
- **Controls:**
  - a filter, **All / Problems only**, where Problems means Failed and the attention states;
  - a **Destination** select;
  - an **Event** select.
  - No summary counts. The owner dropped them.
- **Top line:** "Last message to Telegram: <time>". Under it, when Jellyfin has been quiet for more than a day, or since startup when there is nothing: "No events from Jellyfin since <time>". This tells silence from failure at a glance.
- **Table:** one row **per message per destination**, newest first. This is the owner-approved mockup's rule ("One row per message per chat"), and it serves the owner's purpose: comparing what TgJelly sent with what arrived in the bot.
  - Items batched into one message (four episodes, a digest) or alerts merged into one alert are **one row**. They are not separate Skipped rows.
  - Outcomes that happen before any destination is chosen (no destination wants the event; the item never got its metadata and was dropped) are rows with a dash for Destination.
  - Columns:
  - **When:** the local time, plus the date when it isn't today. Hovering shows the full date and time with seconds.
  - **Event:** the title (e.g. "Dune: Part Two", "The Bear, S04E01–E04", "Login failed: admin"), and under it the message's label (e.g. "New movie").
  - **Destination:** the destination's name, or a dash when no destination was involved.
  - **Result:** a label (§3).
  - **Why:** a plain reason. It is **empty for Sent**.
- **Results:**

| Result | Colour | Why (examples) |
|---|---|---|
| Sent | good | (empty) |
| Waiting | in progress | "Collecting more episodes, sends at 13:52" |
| Skipped | neutral | "This destination only gets the Kids library", "New movie is off for this destination", "This destination is paused", "No destination has Security alerts on" |
| Sent late | attention | "Couldn't reach Telegram for 4 min (connection timed out), sent on the 3rd try" |
| Sent with built-in | attention | "Your edited template failed (unknown field `ratng`), so the built-in one was used" |
| Sent without photo | attention | "This chat doesn't allow photos, so the text was sent alone" |
| Failed | bad | "Telegram refused it: the bot was removed from the chat" |

- **More than one detail on one send** (my call):
  - The row gets the label of the most serious detail, in this order: Failed, Sent with built-in, Sent without photo, Sent late.
  - Why lists every detail, in that order, separated by "; ".
- **Shortened is not a problem** (my call). Shortening a long overview is routine, so a shortened send is plain Sent with an empty Why. The Messages page already flags a template whose messages were shortened.
- **Clicking a Failed or attention row** expands it. The expansion shows:
  - what Telegram or the network said, verbatim (e.g. `403 Forbidden: bot was kicked from the supergroup chat`);
  - how many tries there were, and whether it will retry, and when;
  - what to do, e.g. "Add the bot back to Admin alerts, then press Check again on the destination".
- **Updates:** the table refreshes with the page's existing status poll and doesn't disturb an expanded row. Older rows load on scroll or with a "Show older" action (builder's call).

### 8.2 What gets recorded

- **A message, per destination, at each point where it ends or changes state** (§8.1: one row per message per destination):
  - **Skipped:** no destination wants this event, a destination has it off, the library filter excluded it, the destination is paused, or the item wasn't ready and was dropped. Each case gets its own reason. Items merged into a message that went out are part of that message's row, never a Skipped row.
  - **Waiting:** held for batching or for metadata. The row is updated in place when the message goes out, so a message's row moves from Waiting to its final result.
  - **Sent:** with any attention detail (late, retries, built-in fallback, no photo). Shortening is not a detail (§8.1).
  - **Failed:** permanently refused, retries exhausted, or dropped because the destination is Lost. It carries the exact Telegram error code and description, or the exact network error.
- **Exclusions:**
  - Late edits to an earlier message count as a Sent row ("Added 2 episodes to the message sent at 11:30").
  - Test messages (Send test, Check again) are not logged as events. A Failed one already shows its result where it was pressed.
- **Storage:**
  - in the plugin's data folder, separate from settings so it never touches `Revision`;
  - bounded by age (7 days) and by count (a cap, e.g. 5,000 rows, oldest dropped first);
  - written off the hot path, so a slow disk never delays a message;
  - lost or corrupt log data never affects sending: the log starts empty and says so.
- **API:** a read-only `GET Log` with the filters and paging. Admin only, like every other endpoint.
- **Privacy:** the log holds what the messages hold (titles, user names for security events). It never holds the bot token. The Diagnostics notice (§7) mentions whether the log is included in the report; including the last problems is the builder's call.

## 9. Clear errors everywhere

The owner: "no 'Unknown error please try again' anywhere."

- **Audit every error string and error path in the dashboard and the bot:**
  - `web.*`, the bot's strings, HTTP error handling in `tgjelly.js`, controller error responses, and the Telegram and network error mapping.
  - Each error must say **what happened**, quoting Telegram's or the network's own words where they exist, and **what to do next**.
- **Generic fallbacks become specific.**
  - `web.header.unknown` ("Status unavailable") says why: the status request failed with what, and that the page retries.
  - A truly unexpected server error shows the HTTP status and the server's message, and suggests Copy diagnostics.
  - "Try again" alone is never the whole message.
- **Tests:**
  - a string test fails on `web.*` or bot strings containing "unknown error", "something went wrong", or a message that is only "try again";
  - a JS test checks that a failed request's cue or notice contains the HTTP status or the server's error text.

## 10. Non-goals and anti-goals

- **Non-goals:**
  - no MTProto proxy;
  - no per-destination message switches (they are per message, not per message per destination);
  - no new events;
  - no log export or search beyond the filters;
  - no log in the bot.
- **Anti-goals (a failure even if it works):**
  - Statuses back to bare words.
  - Any vague error.
  - The hover link or click-to-insert interrupting typing.
  - The Log slowing or blocking a send.
  - The upgrade changing what any existing message looks like.
  - The preview disagreeing with what Telegram shows.

## 11. Left to the builder

- Where Announce upgrades lives: global or per media message.
- How hover mapping is produced (§6.5), and the textarea highlight technique.
- Log storage format, the row cap, and paging.
- Whether the global `MessageOptions` stays as a migration source.
- Group-level switches in the destination editor, only if they already exist.
- Exact wording of new strings, following the existing plain style. The bot explains each button on its screen.

## 12. Manual checks for the owner (next pre-release)

- Labels look like the Built-in/Edited badge everywhere, in dark and light themes.
- **Overview:** the version is next to "TgJelly", there's no "At a glance", and Access is a notice.
- **Destinations:**
  - a healthy one shows no label, and a broken one shows it;
  - the events are grouped, without prefixes;
  - disconnect with and without "Also remove all destinations".
- **Messages:**
  - turn Poster off for New episodes only, and a movie still gets its poster;
  - Security alert with the Poster switch on sends the logo;
  - click fields and formatting to insert;
  - hover link both ways on a line with several fields;
  - the preview matches the real Telegram message, buttons included.
- **Advanced:**
  - proxy credentials appear only with a proxy;
  - the update switches;
  - the Diagnostics notice.
- **Log:**
  - a movie added shows Sent;
  - a destination with that library filtered out shows Skipped with the reason;
  - kicking the bot from a chat shows Failed with Telegram's words;
  - the hover shows the full date.
