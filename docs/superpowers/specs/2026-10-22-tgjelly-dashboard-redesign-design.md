# TgJelly — dashboard redesign and auto-save (design)

Date: 2026-10-22. Status: draft for the owner's review.

This amends the v1 spec (`docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md`, §6.6 "Dashboard") and the templates design (`2026-10-21-tgjelly-templates-design.md`, §6.1). Where they disagree, this document wins.

## 1. Intent

After v0.3, the owner finds the dashboard janky:

> "a text line one after another, block after block, with not enough distinction between them. spacing is inconsistent… renders you provided as mockups looked far better than the outcome… looks like it's been thrown out there."

Jellyfin's stock plugin-page styling suits small pages. TgJelly has outgrown it: it has many settings, a destination editor and a template editor. The owner points to two plugins that render their own settings pages with their own styles:

- **Intro Skipper:** "a structured page overall, with clear sections and settings overview";
- **Segment Editor:** "just looks fresh".

The owner: "I just want settings to look neat and organized."

Second request: **auto-save**. The template preview already updates as you type, and Send test already uses the unsaved text, so a Save button is pointless. The owner extended this to **everything**: "everything auto-saves, like the templates, with a cue when it happens".

Success: the page looks designed and organised, at least as polished as the brainstorming mockups. Every section is clearly separate, spacing is consistent, and there is not a single Save button.

## 2. Goals

1. A custom-rendered dashboard with its **own scoped stylesheet**, built on design tokens (colours, spacing scale, type scale, radius) that read **Jellyfin's theme variables**, so it fits every Jellyfin theme, dark, light or other.
2. **Layout (owner's choice, mockup A):** a header, a sidebar of sections with one section shown at a time, and a content area. On a phone the sidebar becomes a row of tabs at the top.
3. **Sections:** Overview, Destinations, Messages, Templates, Advanced (§5).
4. Inside a section, **cards** with a title, and settings as **rows**: a label, a one-line explanation, and the control on the right. Switches replace checkboxes.
5. **Everything auto-saves**, with no Save buttons. The cue sits **next to the setting that changed** (owner's choice, cue mockup A) (§6).
6. **No decorative symbols** anywhere on the page: no dots, checkmarks, ● markers or emoji status icons (owner: "there is no dots or checkmarks needed anywhere really"). State is shown with words and colour.

## 3. Non-goals

- No new features and no new settings. Everything the dashboard does today stays, reorganised. Anything the page can't do today, it still can't.
- No changes to the bot, the API semantics or the message pipeline, with one exception the owner asked for on 2026-10-22: the bot's **Status** screen says which Jellyfin server it is connected to, "connected to Jellyfin Home (12.2)", instead of "Connected as @bot". The dashboard's connect result keeps the bot name. Endpoints may gain fields only where auto-save needs them (§6.4).
- No UI framework and no Node build step (owner: "no framework is needed really"). The page stays plain ES-module JavaScript served as embedded resources, like today. This matches how Intro Skipper is built, minus its bundler.
- The page's wording doesn't change. Only the Save buttons, the save and confirmation messages they had, and the symbols go.
- The Telegram-style message bubble in the template preview keeps its Telegram look, since it imitates Telegram on purpose. It is the one element not themed like the rest.

## 4. Anti-goals (would be a failure even if it works)

- **Inconsistent spacing or mixed styles:** some cards on the new tokens and others still on Jellyfin's stock form classes. Every section must use the same components.
- **Styles leaking.** Jellyfin keeps a plugin's stylesheet loaded after navigating away, which Intro Skipper documents. Every rule must be scoped under the plugin's root element so other Jellyfin pages never change.
- **Losing what the admin typed:**
  - a save, a refresh or another tab's change wipes a field mid-typing;
  - typing in a field loses focus or caret on re-render. This was fixed in v0.3 for the template textarea, and must hold for every field.
- **Auto-saving an invalid value:** a broken template that goes live, or a URL that breaks the buttons on every message.
- **A cue that nags:** the cue must stay small and short-lived for successes. Only problems stay visible.
- **A layout that fails on a phone,** or that breaks under the Jellyfin themes besides the default dark one.

## 5. Layout and sections

### 5.1 Shell

- **The Jellyfin page** (`tgjelly.html`) becomes a thin shell: the required `data-role="page"` wrapper, one root element (`#tgjelly-root`), the stylesheet and the script. The script renders the app into the root. The page must still satisfy the existing page checks: `data-role="page"`, and no `${` in the HTML.
- **Header:** the title "TgJelly" and a connection label, coloured text with no dot: "Connected · @bot", "Not connected", or a warning label when something needs attention.
- **Sidebar:** the section names, with a short count or state in muted text where it helps, for example "Destinations 3" or "Templates 1 edited".
  - The active section is highlighted with the accent colour, as in the mockup.
  - The active section is kept in the URL hash, or in per-viewer browser storage (*builder's call*), so a reload stays on the same section.
- **Phones and narrow widths:** the sidebar turns into a horizontally scrolling tab row above the content. "Narrow" is measured on the plugin root with container queries, not the window, because Jellyfin's drawer takes width.

### 5.2 Sections and what moves where

| Section | Contains (today's blocks) |
|---|---|
| **Overview** | Status; Connect a bot or Change bot (today's Connect and Bot blocks); a summary: number of destinations and their health, templates edited or flagged, anything needing attention. Each summary line links to its section. |
| **Destinations** | The destination list and editor (today's Destinations), and adding destinations (today's links). |
| **Messages** | The message toggles and the public URL (today's Messages). |
| **Templates** | The template editor from v0.3, restyled. |
| **Advanced** | Proxy, Batching, Update checks, Add chat by ID (only when unlocked, as today), Diagnostics. |

- *Builder's call:* small regroupings inside a section, for example two cards versus one. No block may disappear.
- **Before the bot is connected:** Overview leads with Connect, as the main path. The other sections stay reachable, but say plainly that a bot must be connected first wherever that applies, as today.

### 5.3 Components

One set of reusable render helpers and CSS classes, used by every section:

- **Card:** a title, an optional description, and a body.
- **Setting row:** a label, an explanation and the control on the right, with the cue spot (§6.2) beside the control. Rows are separated by thin dividers.
- **Controls:**
  - a switch (replaces checkboxes);
  - a text field;
  - a select;
  - a number field with range;
  - a button: primary, secondary, or danger for destructive actions;
  - an inline confirmation: a second click with Confirm or Cancel, never a browser dialog, as Reset already works today.
- **Notices:** info, warning and error, in coloured text on a tinted background, with no icons.
- **Empty states:** for example "No destinations yet" with the next step.
- **Design tokens:** colours from Jellyfin's palette variables (`--jf-palette-*`, as Intro Skipper uses), with dark-theme fallbacks; one spacing scale; one type scale; the card radius from Jellyfin's card radius. Accent-coloured text is mixed toward the text colour, so it keeps readable contrast on every theme.

## 6. Auto-save

### 6.1 When things save

- **Switches, selects, and radio-like choices** save immediately on change.
- **Text and number fields** without side effects (batching numbers, the destination editor's text fields) save **800 ms after the last keystroke**, or **when the field loses focus**, whichever comes first (*my call* on the delay).
- **Fields whose save has side effects save only when you leave the field or press Enter, never mid-typing** (*my call*, from the builder check). There is still no Save button.
  - **The proxy fields** (address, username, password): every proxy save restarts the bot session, and partial addresses like `p` parse as valid. Saving mid-typing would restart the bot through a wrong proxy, again and again, and write partial passwords to `secrets.json`.
    - The card saves its fields together when focus leaves the card's last edited field.
    - Choosing a proxy mode with no address shows "Not saved: enter the proxy address" instead of a server error.
  - **The public URL:** a partial URL such as `https://jelly.` passes the rules, and would go live in message buttons while the admin is still typing.
- **Templates** save **1 s after the last keystroke** (*my call*). The live preview keeps its own 500 ms debounce.
- **Actions keep explicit buttons.** These change something real, so they aren't settings:
  - removing a destination;
  - connecting or changing the bot;
  - adding a destination;
  - Reset to built-in;
  - Send test;
  - Check now;
  - Copy diagnostics.

  Destructive ones use the inline confirmation.

### 6.1a Changing a destination's preset

- Picking a preset (Everything, New media, Admin alerts) replaces that destination's event list on the server.
- Saving immediately would make one wrong pick permanently delete a hand-picked **Custom** list. So **leaving Custom** for a preset asks the inline confirmation first: "Replace your custom events with this preset?" with Replace or Cancel (*my call*).
- Switching between presets, or into Custom, saves at once as usual.

### 6.2 The cue

- **On success,** the word "Saved", in the success colour, appears beside the control that changed for about **2 s**, then fades.
- **While the request is in flight longer than about 300 ms,** "Saving…" shows in muted text, so a slow server is visible.
- **On a problem,** "Not saved:" plus the reason shows beside the field in the error colour, and **stays until the value is fixed**, saved, or reverted.
- **No symbols:** no ✓, no ●, no ✅. Words and colour only.

### 6.3 Invalid values are never saved

- Each field is validated before saving, with the same rules the server applies (today's `SettingsError` messages). An invalid value isn't sent; the cue shows the reason.
- **The public URL** has no server-side rule today: the server saves any string and only flags it with `PublicUrlValid`. The page checks it with a client-side copy of `PublicUrlRules`:
  - an absolute `http`/`https` URL;
  - a host containing a dot;
  - not an IPv6 literal.

  It reuses the existing `web.messages.urlInvalid` text. An **empty** URL is valid and saves, since it turns the buttons off. The server is not changed (§3) (*my call*).
- **A template** saves only when the last validation has **no errors**; warnings don't block saving. **Empty text is never auto-saved** (*my call*). The server treats empty text as "remove the override", so **Reset to built-in** stays the one way to reset. With errors, the cue says "Not saved: fix the errors above". The live template keeps the last good saved text until then. This way a broken template never goes live.
- If the server rejects a value anyway (400 with errors), the cue shows the server's message. The field keeps the admin's text, so nothing is lost.

### 6.4 Conflicts and concurrency

- **One page-wide save queue.** Settings, destinations and templates all check the same `Revision` number, so per-resource queues would conflict with each other. Every save goes through one queue, in order. A newer change to the same field replaces a queued one, and saves for different fields run one after another.
- **The revision.** Each save sends the revision the page last saw, as today (`ExpectedRevision`).
  - On a **409 conflict** (a change from the bot or another tab), the page takes the newer server data, re-applies the admin's pending change on top, and retries once.
  - If the same field changed on both sides, the admin's value wins, since they just typed it.
  - A second conflict in a row shows "Not saved: changed elsewhere, check the value" and stops retrying.
- **Typing is never interrupted:** a refresh from elsewhere never replaces a field that has focus or a pending unsaved change (anti-goal §4). Other fields update.
- *Builder's call:* whether auto-save calls today's endpoints with only the changed fields, or with the whole section as today. Prefer partial updates if today's settings endpoint supports them cleanly; otherwise send the whole section, merged with the latest server data.

## 7. Symbols

- Remove every decorative symbol from dashboard text and markup:
  - the `●` edited marker;
  - status dots;
  - `✅`/`⚠️`/`❌`-style health labels;
  - checkmarks.
- Health and state are shown as words in a colour: for example "OK" in the success colour, "Needs permissions" in the warning colour, "Lost" in the error colour.
- Strings in `en.json` that the dashboard uses (`web.*`, and the `dest.health.*` labels as the dashboard shows them) drop their symbols.
- **The bot's own texts keep theirs.** The bot uses emoji and custom icons by design (plan 6), and this rule is for the dashboard only. If a string is shared between the bot and the dashboard, the dashboard strips or replaces the symbol at render time, and the bot's string stays.

## 8. Constraints

- **Plain ES modules,** with no framework and no bundler or Node build in the plugin build. Splitting `tgjelly.js` (≈2700 lines) into several module files is allowed and encouraged. Each file is an embedded resource registered as a page (`PluginPageInfo`) the way `tgjelly.js` is today. CSS becomes its own embedded file, for example `tgjelly.css`, loaded by the shell.
- **All CSS** is scoped under `#tgjelly-root`, or a class on it. No bare element selectors at the top level.
- **The existing render-function architecture and its tests** (`tests/web/*.test.mjs`, the fake-view harness) stay, adapted. The rule from v0.3 holds: no browser `alert`/`confirm`/`prompt`.
- **The template preview sanitizer** (`previewHtml`) and its tests stay unchanged in behaviour.
- **Both Jellyfin tracks:** the smoke tests check that the page and its scripts load on 10.11 and 12. New resources must pass the same checks, including a JavaScript content type.

## 9. Left to the builder

- Exact colours, spacing values and type sizes, within §5.3: tokens derived from Jellyfin's palette, and the mockups' feel as the bar.
- File and module split, and the component helper API.
- Whether the active section lives in the URL hash or in browser storage.
- Animation details for the cue fade, with reduced-motion respected.

## 10. Manual checks for the owner

- Open the dashboard in Jellyfin's default dark theme, a light theme and one other theme. Each section should look consistent, with clear separation and even spacing.
- On a phone: the tabs row, and each section readable without horizontal scrolling.
- Change a switch, a text field and a template:
  - each shows "Saved" beside it;
  - an invalid public URL shows "Not saved:" with the reason, and isn't saved;
  - a template with an error doesn't go live.
- Change a setting in the bot while the dashboard is open, then change another in the dashboard. Nothing is lost.
- Navigate to another Jellyfin settings page and back. The other page looks unchanged, so no styles leaked.
