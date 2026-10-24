// TgJelly dashboard: the Messages section (design §6.1-6.2). Imported as './configurationpage?name=tgjelly-messages.js'.
//
// One page per message: the Section and Message pickers, What it shows (the message's own switches, the public URL
// under Open in Jellyfin, and Announce upgrades for media messages), the message's template with Send test and Reset,
// and the preview beside them on wide screens.
// - The render functions are named exports. They return HTML built only from esc()/t() output and the tgjelly-ui.js
//   components, so tests/web can call them without a DOM.
// - createMessages(page) is the section's controller. The page passes in its state and its save machinery, and merges
//   the section, the click actions, the input handlers and the hide hook it returns into its own.
// This module imports only its leaf siblings (tgjelly-ui.js, tgjelly-preview.js), never tgjelly.js, which imports it.
import {
    esc, t, text, label, card, row, actionButton, inlineConfirm, notice, textField, switchControl, selectField,
} from './configurationpage?name=tgjelly-ui.js';
import { renderTemplateCheck, renderTemplatePreview, previewHtml } from './configurationpage?name=tgjelly-preview.js';

// The template preview validates this long after the last keystroke.
const PREVIEW_DEBOUNCE_MS = 500;
// The template text saves this long after the last keystroke (plan 8, design §6.1).
const TEMPLATE_SAVE_MS = 1000;

const TEMPLATES_DOCS = 'https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md';

/** The Section picker's choices: the event groups, in the order the catalog lists them. */
const MESSAGE_SECTIONS = ['media', 'security', 'server'];

/** Every message switch (MessageSwitches.Names). A message shows only the ones its template item lists. */
const SWITCHES = ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks'];

/** The settings form path of the switches; one message's switch is at Message.Switches.<event key>.<switch>. */
const SWITCHES_PATH = 'Message.Switches';
const ANNOUNCE_PATH = 'Message.AnnounceUpgrades';

/** The group of an event key: media, security or server (media.digest is media). */
const groupOf = key => String(key).split('.')[0];

/** The settings form path of one message's switch. */
const switchPath = (key, name) => `${SWITCHES_PATH}.${key}.${name}`;

/** The auto-save and cue key of one message's switch. */
const switchSaveKey = (key, name) => `msg:${key}:${name}`;

/** The auto-save and cue key of an event's template text. */
const templateSaveKey = key => `template:${key}`;

const isPlainObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

/** The value at a dotted path of a nested object; undefined when a part is missing. */
const valueAt = (obj, path) => path.split('.').reduce((node, part) => (isPlainObject(node) ? node[part] : undefined), obj);

/** The switches a template item says change its message, in the order shown; none when it says nothing. */
const applicableSwitches = item => (Array.isArray(item && item.ApplicableSwitches) ? item.ApplicableSwitches.filter(name => SWITCHES.includes(name)) : []);

// --- the selection: tgjmsg=<event key> in the URL hash, beside the section's tgj=<name> ---

const HASH_MESSAGE = /([#?&])tgjmsg=([^&]*)/;

/**
 * The message a URL hash names with tgjmsg=<event key>, or null. Whether the server has it is the controller's check.
 * @param {string | undefined} hash
 * @returns {string | null}
 */
export function messageFromHash(hash) {
    const match = HASH_MESSAGE.exec(String(hash ?? ''));
    if (!match) {
        return null;
    }

    try {
        return decodeURIComponent(match[2]) || null;
    } catch {
        return null;
    }
}

/**
 * The hash with tgjmsg=<key> set: replaced where it is, else added to the route's query, else to the whole hash.
 * @param {string} hash
 * @param {string} key an event key
 * @returns {string}
 */
export function hashWithMessage(hash, key) {
    const current = String(hash ?? '');
    const value = encodeURIComponent(key);
    if (HASH_MESSAGE.test(current)) {
        return current.replace(HASH_MESSAGE, `$1tgjmsg=${value}`);
    }

    if (current.startsWith('#/')) {
        return `${current}${current.includes('?') ? '&' : '?'}tgjmsg=${value}`;
    }

    return current === '' || current === '#' ? `#tgjmsg=${value}` : `${current}&tgjmsg=${value}`;
}

/**
 * The template item a page shows: the chosen key while the templates list it, else the first media message, else the
 * first one.
 * @param {object[]} items TemplatesDto.Items
 * @param {string | null | undefined} chosen
 * @returns {object | undefined}
 */
export function selectedItem(items, chosen) {
    return items.find(i => i.EventKey === chosen) ?? items.find(i => groupOf(i.EventKey) === 'media') ?? items[0];
}

// --- the settings model's Message part ---

/**
 * The settings form's Message from a SettingsDto's ({ PerMessage, AnnounceUpgrades }): each message's switches under
 * its event key, nested by the key's parts (so Message.Switches.media.movie.ShowPoster is a dotted path like any
 * other), and the one Announce upgrades.
 * @param {{ PerMessage?: { EventKey: string, Switches: object }[], AnnounceUpgrades?: boolean } | null | undefined} message
 */
export function messageFormData(message) {
    const switches = {};
    for (const entry of Array.isArray(message && message.PerMessage) ? message.PerMessage : []) {
        if (!entry || typeof entry.EventKey !== 'string' || entry.EventKey === '') {
            continue;
        }

        const parts = entry.EventKey.split('.');
        let node = switches;
        for (const part of parts.slice(0, -1)) {
            node = node[part] = isPlainObject(node[part]) ? node[part] : {};
        }

        node[parts.at(-1)] = Object.fromEntries(SWITCHES.map(name => [name, Boolean(entry.Switches && entry.Switches[name])]));
    }

    return { Switches: switches, AnnounceUpgrades: Boolean(message && message.AnnounceUpgrades) };
}

/**
 * The Message part of a PUT Settings body: one entry per message with only the switches at the given paths, and
 * AnnounceUpgrades only when its path is given. The server merges entries by event key and keeps every switch not
 * sent, so a save changes exactly what the admin changed.
 * @param {object} message the form's Message (see messageFormData)
 * @param {string[]} paths dotted form paths, e.g. ['Message.Switches.media.movie.ShowPoster']
 */
export function messageRequest(message, paths) {
    const entries = new Map();
    for (const path of paths.filter(p => p.startsWith(`${SWITCHES_PATH}.`))) {
        const rest = path.slice(SWITCHES_PATH.length + 1);
        const cut = rest.lastIndexOf('.');
        const key = rest.slice(0, cut);
        const switches = entries.get(key) ?? {};
        switches[rest.slice(cut + 1)] = Boolean(valueAt(message.Switches, rest));
        entries.set(key, switches);
    }

    const body = { PerMessage: [...entries].map(([EventKey, Switches]) => ({ EventKey, Switches })) };
    if (paths.includes(ANNOUNCE_PATH)) {
        body.AnnounceUpgrades = Boolean(message.AnnounceUpgrades);
    }

    return body;
}

/**
 * Whether a public URL can be used for the "Open in Jellyfin" buttons: a port of the server's PublicUrlRules.IsValid
 * (an absolute http or https URL whose host contains a dot and is not an IPv6 literal). Empty text is valid: it turns
 * the buttons off. The server saves any string, so the page checks before it saves (plan 8, design §6.3).
 * @param {unknown} value
 * @returns {boolean}
 */
export function validatePublicUrl(value) {
    const url = String(value ?? '').trim();
    if (url === '') {
        return true;
    }

    if (!/^https?:\/\//i.test(url)) {
        return false;
    }

    let parsed;
    try {
        parsed = new URL(url);
    } catch {
        return false;
    }

    // System.Uri refuses a host with an empty label (".", ".a", "a..b"; one trailing dot is fine), with characters
    // outside letters, digits, "-" and "_" ("*.b"), or with a %-escape ("a%2eb"), all of which URL accepts.
    const host = parsed.hostname;
    const rawHost = url.replace(/^https?:\/\//i, '').split(/[/?#]/)[0].replace(/^.*@/, '');
    const labels = host.replace(/\.$/, '').split('.');
    return (parsed.protocol === 'http:' || parsed.protocol === 'https:')
        && !host.startsWith('[')
        && host.includes('.')
        && /^[a-z0-9._-]+$/.test(host)
        && !rawHost.includes('%')
        && labels.every(part => part !== '');
}

// --- rendering ---

/** A message's name in the dashboard: its web.event.* string (no group prefix), else the server's label. */
const messageLabel = (S, item) => (S && Object.hasOwn(S, `web.event.${item.EventKey}`) ? text(S, `web.event.${item.EventKey}`) : String(item.Label ?? item.EventKey));

/**
 * The pickers card: the Section select, then the Message select (that section's messages), then the message's
 * Built-in or Edited label. The options are names only: the state is the label beside them.
 * @param {object[]} items TemplatesDto.Items
 * @param {object} item the selected one
 * @param {Record<string, string>} S
 * @returns {string} HTML
 */
export function renderMessagePickers(items, item, S) {
    const group = groupOf(item.EventKey);
    const sections = MESSAGE_SECTIONS.filter(name => items.some(i => groupOf(i.EventKey) === name));
    const picker = (key, control) => `<label class="tgj-msg-picker"><span class="tgj-row-label">${t(S, key)}</span>${control}</label>`;
    const badge = item.Edited ? label(text(S, 'web.templates.edited'), 'accent') : label(text(S, 'web.templates.builtIn'), 'neutral');
    const body = '<div class="tgj-msg-pickers">'
        + picker('web.messages.section', selectField({
            input: 'msgSection',
            value: group,
            options: sections.map(name => ({ value: name, label: text(S, `web.event.group.${name}`) })),
        }))
        + picker('web.messages.message', selectField({
            input: 'msgKey',
            value: item.EventKey,
            options: items.filter(i => groupOf(i.EventKey) === group).map(i => ({ value: i.EventKey, label: messageLabel(S, i) })),
        }))
        + `<span class="tgj-msg-state">${badge}</span></div>`;
    return card({ title: text(S, 'web.messages.title'), description: text(S, 'web.messages.intro'), body, extraClass: 'tgj-msg-pick' });
}

/** The public URL rows, nested under Open in Jellyfin: the field (saved on blur or Enter), the suggestion, the verdict. */
function publicUrlRows(settings, local, S) {
    const url = String(local.PublicUrl ?? '');
    const rows = [row({
        label: text(S, 'web.messages.url'),
        hint: text(S, 'web.messages.urlHint'),
        extraClass: 'tgj-row--stack',
        cueKey: 'PublicUrl',
        control: textField({
            input: 'publicUrl',
            value: url,
            type: 'url',
            attrs: { 'data-tgj-field': 'PublicUrl', 'data-tgj-save': 'PublicUrl', autocomplete: 'off', spellcheck: 'false', 'aria-label': text(S, 'web.messages.url') },
        }),
    })];
    const suggestion = settings.PublicUrlSuggestion;
    if (suggestion && suggestion !== url) {
        rows.push(notice({
            kind: 'info',
            html: `${t(S, 'web.messages.suggested', { url: suggestion })} <strong>${t(S, 'web.messages.verify')}</strong> `
                + actionButton({ label: text(S, 'web.messages.useSuggested'), act: 'useSuggested' }),
        }));
    }

    // The server judged the saved value; once the user types another one, that verdict no longer applies.
    if (settings.PublicUrl && !settings.PublicUrlValid && url === settings.PublicUrl) {
        rows.push(notice({ kind: 'error', html: t(S, 'web.messages.urlInvalid') }));
    }

    return `<div class="tgj-msg-nested">${rows.join('')}</div>`;
}

/**
 * The What it shows card (design §6.2): the switches that change this message, each saved at once with its cue beside
 * it; the public URL under Open in Jellyfin while that is on; and, for media messages, Announce upgrades, which is one
 * setting for all of them. Empty for a message no switch changes (the digest).
 * @param {object} item the selected template item
 * @param {object | null} settings the SettingsDto
 * @param {{ local: object } | undefined} form state.forms.settings
 * @param {Record<string, string>} S
 * @returns {string} HTML
 */
export function renderMessageSwitches(item, settings, form, S) {
    const names = applicableSwitches(item);
    if (!settings || !form || names.length === 0) {
        return '';
    }

    const key = item.EventKey;
    const media = groupOf(key) === 'media';
    const rows = [];
    for (const name of names) {
        const on = Boolean(valueAt(form.local, switchPath(key, name)));
        const title = text(S, `settings.msg.${name}`);
        rows.push(row({
            label: title,
            // A Security or Server message's poster is the TgJelly logo (design §6.2).
            hint: name === 'ShowPoster' && !media ? text(S, 'web.messages.logoHint') : '',
            cueKey: switchSaveKey(key, name),
            control: switchControl({
                input: 'msgSwitch',
                checked: on,
                label: title,
                attrs: { 'data-tgj-id': key, 'data-tgj-key': name, 'data-tgj-field': switchPath(key, name), 'data-tgj-save': switchSaveKey(key, name) },
            }),
        }));
        if (name === 'ShowOpenInJellyfin' && on) {
            rows.push(publicUrlRows(settings, form.local, S));
        }
    }

    if (media) {
        const announce = text(S, 'settings.msg.AnnounceUpgrades');
        rows.push(row({
            label: announce,
            hint: text(S, 'web.messages.announceHint'),
            cueKey: ANNOUNCE_PATH,
            control: switchControl({
                input: 'msgAnnounce',
                checked: Boolean(valueAt(form.local, ANNOUNCE_PATH)),
                label: announce,
                attrs: { 'data-tgj-field': ANNOUNCE_PATH, 'data-tgj-save': ANNOUNCE_PATH },
            }),
        }));
    }

    return card({ title: text(S, 'web.messages.shows'), body: rows.join(''), extraClass: 'tgj-msg-shows' });
}

/** The template form data for one item (what a save sends besides the revision). */
export function templateFormData(item) {
    return { Text: String(item.Text ?? '') };
}

/** Whether a validation result stops a test send: it had errors, or the request failed. */
const validationFailed = result => Boolean(result && (result.RequestError || (result.Errors ?? []).length > 0));

/** Whether Send test is off: no destination, the last validation failed, or a send or a reset is in flight. */
export function templateTestBlocked(ui) {
    return (ui.destinations ?? []).length === 0 || validationFailed(ui.result) || Boolean(ui.testing) || Boolean(ui.busy);
}

/**
 * The Template card (spec §8.5): the message's template text, which saves itself (its cue sits beside the textarea's
 * label), its errors and length under it, the Fields and Formatting tables, the guide, then Send test and, for an edited
 * template, Reset.
 * @param {object} item the selected template item
 * @param {Record<string, string>} S
 * @param {{ forms?: Record<string, { local: object }>, result?: object | null, lang?: string, busy?: boolean,
 *   destinations?: { Id: string, Title: string }[], testTo?: string, testing?: boolean, testResult?: string,
 *   resetAsk?: boolean }} ui result is the key's last validation; testResult is HTML
 * @returns {string} HTML
 */
export function renderTemplateCard(item, S, ui) {
    const form = (ui.forms ?? {})[`tpl:${item.EventKey}`];
    const local = form ? form.local : templateFormData(item);
    const saveKey = templateSaveKey(item.EventKey);
    const parts = [];
    if (item.Flag) {
        // A truncated template was sent, only shortened; any other flag means the built-in template was sent instead.
        parts.push(item.Flag.Kind === 'truncated'
            ? notice({ kind: 'warning', html: t(S, 'web.templates.flagTruncated') })
            : notice({ kind: 'error', html: `${t(S, 'web.templates.flagError')} ${esc(item.Flag.Error)}` }));
    }

    // The textarea is told apart by its event key, not an id: a refresh keeps a pending text only in its own template.
    parts.push('<div class="tgj-tpl-edit" id="tgj-tpl-edit">'
        + `<div class="tgj-tpl-label"><span class="tgj-row-label" id="tgj-tpl-label">${t(S, 'web.templates.text')}</span>`
        + `<span class="tgj-cue" data-tgj-cue="${esc(saveKey)}" aria-live="polite"></span></div>`
        // The mirror behind the transparent textarea draws the hover link's highlight (design §6.5); the controller
        // fills it on hover, from the textarea's current text.
        + '<div class="tgj-tpl-field"><div class="tgj-tpl-mirror" id="tgj-tpl-mirror" aria-hidden="true"></div>'
        + `<textarea class="tgj-field tgj-template-text" data-tgj-input="tplText" data-tgj-key="${esc(item.EventKey)}" data-tgj-save="${esc(saveKey)}"`
        + ` aria-labelledby="tgj-tpl-label" rows="12" spellcheck="false">${esc(local.Text)}</textarea></div>`
        // The errors and the length, under the text they are about; the controller replaces only this while typing.
        + `<div id="tgj-tpl-check" class="tgj-tpl-check" aria-live="polite">${renderTemplateCheck(ui.result ?? null, S, ui.lang ?? 'en')}</div>`
        + renderTemplateTables(item, S)
        + `<p class="tgj-tpl-docs"><a class="tgj-link" href="${TEMPLATES_DOCS}" target="_blank" rel="noopener noreferrer">${t(S, 'web.templates.docs')}</a></p></div>`);

    const destinations = ui.destinations ?? [];
    const chosen = destinations.some(d => d.Id === ui.testTo) ? ui.testTo : (destinations[0] ? destinations[0].Id : '');
    const testTo = text(S, 'web.templates.testTo');
    const target = destinations.length > 0
        ? selectField({ input: 'tplTestTo', value: chosen, options: destinations.map(d => ({ value: d.Id, label: d.Title })), attrs: { 'aria-label': testTo } })
        : '';
    const testButton = actionButton({ label: text(S, 'web.templates.test'), act: 'tplTest', disabled: templateTestBlocked(ui), attrs: { id: 'tgj-tpl-test' } });
    parts.push(row({
        label: testTo,
        hint: text(S, destinations.length > 0 ? 'web.templates.testNote' : 'web.templates.noDestinations'),
        control: target + testButton + (ui.testResult ? `<span class="tgj-tpl-test-result">${ui.testResult}</span>` : ''),
        extraClass: 'tgj-tpl-test',
    }));

    if (item.Edited) {
        parts.push(ui.resetAsk
            ? inlineConfirm({
                question: text(S, 'web.templates.resetAsk'),
                confirmLabel: text(S, 'web.templates.resetConfirm'),
                confirmAct: 'tplResetConfirm',
                cancelAct: 'tplResetCancel',
                cancelLabel: text(S, 'web.templates.resetCancel'),
            })
            : `<div class="tgj-actions">${actionButton({ label: text(S, 'web.templates.reset'), act: 'tplReset', disabled: ui.busy })}</div>`);
    }

    return card({ title: text(S, 'web.templates.title'), description: text(S, 'web.templates.intro'), body: parts.join(''), extraClass: 'tgj-tpl' });
}

/**
 * The Messages section (design §6.1), top to bottom: the pickers, What it shows, the template, and the preview, which
 * sits beside the two cards on wide screens (tgjelly.css). The preview pane keeps its id: the controller replaces only
 * the pane while the admin types.
 * @param {{ templates: object | null, settings: object | null, forms?: Record<string, object> }} state the page state:
 *   the TemplatesDto, the SettingsDto, and the editor forms (settings, tpl:<key>)
 * @param {Record<string, string>} S
 * @param {{ key?: string, result?: object | null, good?: object | null, busy?: boolean, lang?: string,
 *   destinations?: { Id: string, Title: string }[], testTo?: string, testing?: boolean, testResult?: string,
 *   resetAsk?: boolean }} ui key is the selected message; result its last validation, good the last one without errors
 * @returns {string} HTML
 */
export function renderMessagesSection(state, S, ui) {
    const dto = state && state.templates;
    if (!dto || !Array.isArray(dto.Items) || dto.Items.length === 0) {
        return '';
    }

    const item = selectedItem(dto.Items, ui.key);
    const forms = state.forms ?? {};
    const preview = card({
        title: text(S, 'web.messages.preview'),
        body: `<div id="tgj-tpl-preview" class="tgj-tpl-preview">${renderTemplatePreview(ui.result ?? null, S, ui.good ?? null)}</div>`,
        extraClass: 'tgj-msg-preview',
    });
    return '<div class="tgj-msg-layout">'
        + renderMessagePickers(dto.Items, item, S)
        + '<div class="tgj-msg-cards">'
        + renderMessageSwitches(item, state.settings ?? null, forms.settings, S)
        + renderTemplateCard(item, S, { ...ui, forms })
        + '</div>'
        + `<div class="tgj-msg-side">${preview}</div>`
        + '</div>';
}

// --- the Fields and Formatting tables (design §6.3) ---

/** What the Formatting table lists: Telegram's tags first, then the template constructs people need. */
export const FORMATTING = [
    { write: '<b>…</b>', snippet: '<b>text</b>', placeholder: 'text', descriptionKey: 'web.format.b', looksLikeHtml: '<b>Bold</b>' },
    { write: '<i>…</i>', snippet: '<i>text</i>', placeholder: 'text', descriptionKey: 'web.format.i', looksLikeHtml: '<i>Italic</i>' },
    { write: '<u>…</u>', snippet: '<u>text</u>', placeholder: 'text', descriptionKey: 'web.format.u', looksLikeHtml: '<u>Underlined</u>' },
    { write: '<s>…</s>', snippet: '<s>text</s>', placeholder: 'text', descriptionKey: 'web.format.s', looksLikeHtml: '<s>Struck out</s>' },
    { write: '<tg-spoiler>…</tg-spoiler>', snippet: '<tg-spoiler>text</tg-spoiler>', placeholder: 'text', descriptionKey: 'web.format.spoiler', looksLikeHtml: '<tg-spoiler>Hidden</tg-spoiler>' },
    { write: '<a href="…">…</a>', snippet: '<a href="https://example.com">text</a>', placeholder: 'text', descriptionKey: 'web.format.a', looksLikeHtml: '<a href="https://example.com">A link</a>' },
    { write: '<code>…</code>', snippet: '<code>text</code>', placeholder: 'text', descriptionKey: 'web.format.code', looksLikeHtml: '<code>monospace</code>' },
    { write: '<pre>…</pre>', snippet: '<pre>text</pre>', placeholder: 'text', descriptionKey: 'web.format.pre', looksLikeHtml: '<pre>a block\nof code</pre>' },
    { write: '<blockquote>…</blockquote>', snippet: '<blockquote>text</blockquote>', placeholder: 'text', descriptionKey: 'web.format.quote', looksLikeHtml: '<blockquote>A quote</blockquote>' },
    { write: '<blockquote expandable>…</blockquote>', snippet: '<blockquote expandable>text</blockquote>', placeholder: 'text', descriptionKey: 'web.format.quoteExpandable', looksLikeHtml: '<blockquote expandable>First line\nThe rest, shown when opened</blockquote>' },
    { write: '{{ if field }}…{{ end }}', snippet: '{{ if field }}text{{ end }}', placeholder: 'text', descriptionKey: 'web.format.if', looksLikeHtml: '(2021)' },
    { write: '{{ plural n "a" "b" }}', snippet: '{{ plural n "one" "many" }}', placeholder: 'n', descriptionKey: 'web.format.plural', looksLikeHtml: '3 episodes' },
    { write: '{{ raw value }}', snippet: '{{ raw value }}', placeholder: 'value', descriptionKey: 'web.format.raw', looksLikeHtml: '<b>Dune</b>' },
    { write: '{{- field -}}', snippet: '{{- field -}}', placeholder: 'field', descriptionKey: 'web.format.trim', looksLikeHtml: 'Two lines become one' },
];

const TABLE_STORE = 'tgj.tables.';
const TABLES = ['fields', 'formatting'];

/** Whether a table is open for this viewer; closed when nothing is kept or the browser refuses storage. */
function tableOpen(name) {
    try {
        return globalThis.localStorage.getItem(TABLE_STORE + name) === '1';
    } catch {
        return false;
    }
}

/** Keeps a table's state for this viewer; the table works without it when the browser refuses storage. */
function rememberTable(name, open) {
    try {
        globalThis.localStorage.setItem(TABLE_STORE + name, open ? '1' : '0');
    } catch {
        // Private mode or blocked site data: the state lasts until the page reloads.
    }
}

/** A sample value as the preview shows it: text as it is, a list joined by commas; null for no value. */
function sampleText(value) {
    if (value === null || value === undefined) {
        return null;
    }

    if (Array.isArray(value)) {
        const parts = value.map(sampleText).filter(part => part !== null);
        return parts.length > 0 ? parts.join(', ') : null;
    }

    const shown = typeof value === 'object' ? JSON.stringify(value) : String(value);
    return shown === '' ? null : shown;
}

/** An insert cell: a button, so Tab reaches it and Enter or Space press it. The page routes its click (tplInsert). */
const insertCell = (kind, id, shown, S) => `<td><button type="button" class="tgj-insert" data-tgj-act="tplInsert" data-tgj-insert="${kind}"`
    + ` data-tgj-id="${esc(id)}" data-tgj-instant title="${esc(text(S, 'web.tables.insert'))}"><code>${esc(shown)}</code></button></td>`;

/**
 * The Available fields table: Field, What it is and Example, one row per field of the message. Clicking a field inserts
 * it into the template.
 * @param {object} item the selected template item
 * @param {Record<string, string>} S
 * @returns {string} HTML
 */
export function renderFieldsTable(item, S) {
    const sample = item.SampleFull ?? {};
    const rows = (item.Fields ?? []).map(f => {
        const example = sampleText(sample[f.Name]);
        return `<tr>${insertCell('field', f.Name, `{{ ${f.Name} }}`, S)}`
            + `<td data-label="${esc(text(S, 'web.tables.what'))}">${esc(f.Description)}${f.Optional ? ` ${label(text(S, 'web.templates.optional'), 'neutral')}` : ''}</td>`
            + `<td data-label="${esc(text(S, 'web.tables.example'))}">${example === null ? '<span class="tgj-muted">-</span>' : esc(example)}</td></tr>`;
    });
    return `<table class="tgj-ref-table"><thead><tr><th scope="col">${t(S, 'web.tables.field')}</th><th scope="col">${t(S, 'web.tables.what')}</th>`
        + `<th scope="col">${t(S, 'web.tables.example')}</th></tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

/**
 * The Formatting table: Write, What it does and Looks like. Clicking a format inserts it, or wraps the selection.
 * @param {Record<string, string>} S
 * @returns {string} HTML
 */
export function renderFormattingTable(S) {
    const rows = FORMATTING.map((f, index) => `<tr>${insertCell('format', String(index), f.write, S)}<td data-label="${esc(text(S, 'web.tables.does'))}">${esc(text(S, f.descriptionKey))}</td>`
        + `<td data-label="${esc(text(S, 'web.tables.looks'))}"><div class="tgj-tg-text tgj-tg-sample">${previewHtml(f.looksLikeHtml)}</div></td></tr>`);
    return `<table class="tgj-ref-table"><thead><tr><th scope="col">${t(S, 'web.tables.write')}</th><th scope="col">${t(S, 'web.tables.does')}</th>`
        + `<th scope="col">${t(S, 'web.tables.looks')}</th></tr></thead><tbody>${rows.join('')}</tbody></table>`;
}

/** One expandable table: a toggle button carrying aria-expanded, and the panel it shows. */
const tableSection = (name, title, body) => {
    const open = tableOpen(name);
    return `<div class="tgj-ref"><button type="button" class="tgj-ref-toggle" data-tgj-act="tplTable" data-tgj-id="${name}" data-tgj-instant`
        + ` aria-expanded="${open}" aria-controls="tgj-tbl-${name}">${esc(title)}</button>`
        + `<div class="tgj-ref-panel" id="tgj-tbl-${name}"${open ? '' : ' hidden'}>${body}</div></div>`;
};

/** Both tables under the template, collapsed until the viewer opens them. */
export function renderTemplateTables(item, S) {
    return `<div class="tgj-tpl-tables">${tableSection('fields', text(S, 'web.templates.fields'), renderFieldsTable(item, S))}`
        + `${tableSection('formatting', text(S, 'web.templates.formatting'), renderFormattingTable(S))}</div>`;
}

/**
 * Puts text into a textarea at its caret as typing: through execCommand when the browser has it (so Ctrl+Z undoes it,
 * and the browser fires input), else setRangeText and an input event. The textarea keeps the focus. The caret lands
 * after the insert, or the range select (offsets inside text) is selected.
 * @param {HTMLTextAreaElement} el
 * @param {string} value the text to insert in place of the selection
 * @param {[number, number] | null} select
 */
export function insertText(el, value, select = null) {
    el.focus({ preventScroll: true });
    const start = el.selectionStart;
    const end = el.selectionEnd;
    const doc = globalThis.document;
    let done = false;
    if (doc && typeof doc.execCommand === 'function') {
        try {
            done = doc.execCommand('insertText', false, value) === true;
        } catch {
            done = false;
        }
    }

    if (!done) {
        el.setRangeText(value, start, end, 'end');
        el.dispatchEvent(new Event('input', { bubbles: true }));
    }

    const [from, to] = select ?? [value.length, value.length];
    el.setSelectionRange(start + from, start + to);
}

/** What a format puts in place of the selection (selected, possibly ''): the snippet with the selection at its placeholder. */
export function formatInsert(format, selected) {
    const at = format.snippet.indexOf(format.placeholder);
    const open = format.snippet.slice(0, at);
    const close = format.snippet.slice(at + format.placeholder.length);
    if (selected === '') {
        return { value: format.snippet, select: [open.length, open.length + format.placeholder.length] };
    }

    return { value: open + selected + close, select: null };
}

// --- the hover link between template and preview (design §6.5) ---

/**
 * The mirror's markup: the template text, escaped, with each piece of it in a span data-tgj-src="<index>" and the lit
 * piece carrying tgj-piece-hl. Pieces that overlap or fall outside the text are left out. A last newline gets a space
 * after it, as a textarea shows an empty last line.
 * @param {string} value the textarea's text
 * @param {{ Index: number, Start: number, Length: number }[]} pieces from Templates/Validate, for exactly this text
 * @param {number | null} lit
 * @returns {string} HTML
 */
export function mirrorHtml(value, pieces, lit) {
    let html = '';
    let at = 0;
    for (const piece of [...pieces].sort((a, b) => a.Start - b.Start)) {
        const end = piece.Start + piece.Length;
        if (!Number.isInteger(piece.Index) || piece.Start < at || piece.Length <= 0 || end > value.length) {
            continue;
        }

        html += esc(value.slice(at, piece.Start))
            + `<span data-tgj-src="${piece.Index}"${piece.Index === lit ? ' class="tgj-piece-hl"' : ''}>${esc(value.slice(piece.Start, end))}</span>`;
        at = end;
    }

    return html + esc(value.slice(at)) + (value.endsWith('\n') ? ' ' : '');
}

/** The piece whose mirror span is under the point, or null. */
function pieceAtPoint(mirror, x, y) {
    if (typeof mirror.querySelectorAll !== 'function' || typeof x !== 'number' || typeof y !== 'number') {
        return null;
    }

    for (const span of mirror.querySelectorAll('[data-tgj-src]')) {
        for (const r of span.getClientRects()) {
            if (x >= r.left && x < r.right && y >= r.top && y < r.bottom) {
                return Number(span.getAttribute('data-tgj-src'));
            }
        }
    }

    return null;
}

// --- the controller ---

/**
 * The Messages section's controller. Listeners stay the page's: it routes data-tgj-act clicks to actions and
 * data-tgj-input events to inputs, and calls hide() when the page goes.
 * @param {object} page what the page shares: view, state, api, byId(id), lang(), forget(id) (a container's HTML was
 *   changed in place), renderSection(name), reloadSection(name, options), runAction(name, fn), markDirty(form, path,
 *   value), syncForm(name, data), noteRevision(dto), isOlder(held, dto), pageRevision(), queueSave(job),
 *   saveResult(result, on), failureText(result), releaseFields(key), flush(key), cancelSave(key), loadSettings(),
 *   saveSetting(path, mode, el, options), requestError(S, result), thrownError(S, err), thrownText(S, err), healthText(status, S), fieldSelector(el)
 * @returns {{ section: { id: string, load: () => Promise<void>, render: () => string }, actions: object,
 *   inputs: object, hover: (event: MouseEvent) => void, scroll: (event: Event) => void, hide: () => void }} hover and
 *   scroll take the page's pointer and (captured) scroll events for the hover link
 */
export function createMessages(page) {
    const { state, api } = page;
    const location = () => (globalThis.window ? globalThis.window.location : null);
    // The Messages section's view state: the chosen message (from the page address at first), the last validation and
    // the last one without errors per key, a reset in flight, the test send's destination, in-flight flag and outcome
    // (HTML), and the open reset question.
    Object.assign(state.ui, {
        msgKey: messageFromHash(location() && location().hash) ?? '',
        tplResults: {},
        tplGood: {},
        tplBusy: false,
        tplTestTo: '',
        tplTesting: false,
        tplTestResult: '',
        tplResetAsk: false,
    });
    // The template preview, per event key: the newest Validate request's number (older replies are ignored) and the
    // text its kept result was for (a save checks exactly the text it sends); and the typing debounce.
    const tplSeqs = new Map();
    const tplChecked = new Map();
    let tplTimer = null;
    // The hover link: the lit piece, and what the mirror was last written for (its element, text, pieces and lit piece).
    let litPiece = null;
    let mirrored = null;

    const items = () => (state.templates ? state.templates.Items : []);
    const itemOf = key => items().find(i => i.EventKey === key);

    /** The selected message's event key: the chosen one while it exists, else the first media message ('' with none). */
    const messageKey = () => {
        const item = selectedItem(items(), state.ui.msgKey);
        return item ? item.EventKey : '';
    };

    /** Takes a TemplatesDto into the state and one editor form per event key (keeping unsaved edits). */
    const takeTemplates = dto => {
        if (page.isOlder(state.templates, dto)) {
            return;
        }

        page.noteRevision(dto);
        state.templates = dto;
        const live = new Set(dto.Items.map(i => `tpl:${i.EventKey}`));
        for (const key of Object.keys(state.forms).filter(k => k.startsWith('tpl:') && !live.has(k))) {
            delete state.forms[key];
        }

        for (const item of dto.Items) {
            page.syncForm(`tpl:${item.EventKey}`, templateFormData(item));
        }
    };

    const loadTemplates = async () => {
        const result = await api.get('Templates');
        if (result.status !== 200 || !result.body) {
            throw new Error('HTTP ' + result.status);
        }

        takeTemplates(result.body);
    };

    /** The destination a test send goes to: the chosen one while it exists, else the first ('' when there is none). */
    const templateTestTarget = () => {
        const list = state.destinations ? state.destinations.Items : [];
        return list.some(d => d.Id === state.ui.tplTestTo) ? state.ui.tplTestTo : (list[0] ? list[0].Id : '');
    };

    /** The section's view of the state (see renderMessagesSection). */
    const messagesUi = () => {
        const key = messageKey();
        return {
            key,
            result: state.ui.tplResults[key] ?? null,
            good: state.ui.tplGood[key] ?? null,
            busy: state.ui.tplBusy,
            lang: page.lang(),
            destinations: state.destinations ? state.destinations.Items : [],
            testTo: templateTestTarget(),
            testing: state.ui.tplTesting,
            testResult: state.ui.tplTestResult,
            resetAsk: state.ui.tplResetAsk,
        };
    };

    /** The selected template's textarea, or null when the Messages section isn't on the page. */
    const templateArea = () => {
        const area = page.byId('tgj-tpl-edit');
        return area ? area.querySelector('textarea') : null;
    };

    /**
     * What the hover link works with: the textarea, the mirror and the pieces of the preview on show, or null. The
     * pieces must be for exactly the text in the textarea (ForText): after a keystroke they are not, until the next
     * preview arrives, so no highlight ever points at the wrong place.
     */
    const pieceLink = () => {
        const ui = messagesUi();
        const shown = validationFailed(ui.result) ? ui.good : ui.result;
        const textarea = templateArea();
        const mirror = page.byId('tgj-tpl-mirror');
        if (!shown || !textarea || !mirror || !Array.isArray(shown.Pieces) || shown.Pieces.length === 0
            || typeof shown.ForText !== 'string' || shown.ForText !== String(textarea.value ?? '')) {
            return null;
        }

        return { textarea, mirror, pieces: shown.Pieces };
    };

    /** Writes the mirror for the textarea's text with the lit piece (only when that changed), level with the textarea. */
    const paintMirror = (link, lit) => {
        const value = String(link.textarea.value ?? '');
        if (!mirrored || mirrored.el !== link.mirror || mirrored.value !== value || mirrored.pieces !== link.pieces || mirrored.lit !== lit) {
            link.mirror.innerHTML = mirrorHtml(value, link.pieces, lit);
            mirrored = { el: link.mirror, value, pieces: link.pieces, lit };
        }

        // A scrollbar narrows the textarea's text; the mirror's text must wrap at the same width.
        const ta = link.textarea;
        if (link.mirror.style && [ta.offsetWidth, ta.clientWidth, ta.clientLeft].every(n => typeof n === 'number')) {
            link.mirror.style.right = `${Math.max(0, ta.offsetWidth - ta.clientWidth - 2 * ta.clientLeft)}px`;
        }

        link.mirror.scrollTop = ta.scrollTop;
    };

    /**
     * Lights one piece on both sides, or none (null): its output's spans in the preview and its range in the mirror.
     * It only reads the textarea, so the focus, the caret and typing are never disturbed.
     */
    const lightPiece = piece => {
        const link = piece === null ? null : pieceLink();
        const next = link ? piece : null;
        const pane = page.byId('tgj-tpl-preview');
        if (next !== litPiece && pane && typeof pane.querySelectorAll === 'function') {
            for (const el of pane.querySelectorAll('.tgj-piece-hl')) {
                el.classList.remove('tgj-piece-hl');
            }

            if (next !== null) {
                for (const el of pane.querySelectorAll(`[data-tgj-piece="${next}"]`)) {
                    el.classList.add('tgj-piece-hl');
                }
            }
        }

        litPiece = next;
        if (link) {
            paintMirror(link, next);
        } else {
            const mirror = page.byId('tgj-tpl-mirror');
            if (mirror && mirrored && mirrored.el === mirror && mirrored.lit !== null) {
                mirror.innerHTML = '';
                mirrored = null;
            }
        }
    };

    /** Whether an element is the template's textarea. */
    const isTemplateArea = el => Boolean(el && el.closest && el.closest('[data-tgj-input]')
        && el.closest('[data-tgj-input]').getAttribute('data-tgj-input') === 'tplText');

    /**
     * The page's pointer events (mouseover, mousemove, mouseout): a piece in the preview lights itself and its source; the
     * pointer over the template's text lights the piece under it (found through the mirror's spans); anywhere else, or
     * off the page, nothing is lit.
     * @param {MouseEvent} event
     */
    const hover = event => {
        if (!state.strings) {
            return;
        }

        if (event.type === 'mouseout') {
            if (!event.relatedTarget) {
                lightPiece(null);
            }

            return;
        }

        const target = event.target;
        const onArea = isTemplateArea(target);
        if (event.type === 'mousemove' && !onArea) {
            return;
        }

        const spot = !onArea && target && target.closest ? target.closest('[data-tgj-piece]') : null;
        if (spot) {
            lightPiece(Number(spot.getAttribute('data-tgj-piece')));
            return;
        }

        if (!onArea) {
            lightPiece(null);
            return;
        }

        // The mirror holds the spans to measure: write it (unlit, or with the piece lit now) before reading it.
        const link = pieceLink();
        if (!link) {
            lightPiece(null);
            return;
        }

        paintMirror(link, litPiece);
        lightPiece(pieceAtPoint(link.mirror, event.clientX, event.clientY));
    };

    /** The page's scroll events (captured): the mirror follows the template's textarea. */
    const scroll = event => {
        const mirror = page.byId('tgj-tpl-mirror');
        if (mirror && isTemplateArea(event.target) && typeof event.target.scrollTop === 'number') {
            mirror.scrollTop = event.target.scrollTop;
        }
    };

    /**
     * Shows the selected template's last validation: its errors and length under the textarea, the message in the
     * preview pane. It turns Send test on or off to match. Only those two are replaced: the textarea keeps its focus
     * and caret.
     */
    const showTemplatePreview = () => {
        const ui = messagesUi();
        const check = page.byId('tgj-tpl-check');
        const pane = page.byId('tgj-tpl-preview');
        if (check) {
            check.innerHTML = renderTemplateCheck(ui.result, state.strings, ui.lang);
        }

        if (pane) {
            // The new preview's pieces are other spans: nothing is lit until the pointer moves again.
            lightPiece(null);
            pane.innerHTML = renderTemplatePreview(ui.result, state.strings, ui.good);
        }

        if (check || pane) {
            // The section on the page no longer matches the copy the page keeps, so its next render must write.
            page.forget('tgj-messages');
        }

        const test = page.byId('tgj-tpl-test');
        if (test) {
            test.disabled = templateTestBlocked(ui);
        }
    };

    /**
     * Validates an event's current text (default: the selected message's) and keeps the result for the preview and for
     * the save's check. Every request gets the key's next number; a reply that is not for the newest request is not
     * kept, so a slow older reply can't overwrite a newer one.
     * @returns {Promise<{ text: string, reply: object, failure: string | null } | null>} the text checked, the reply (or
     *   { RequestError } HTML when the request failed) and then the plain-text reason; null without that template
     */
    const validateTemplate = async (key = messageKey()) => {
        const form = state.forms[`tpl:${key}`];
        if (!key || !form) {
            return null;
        }

        const value = String(form.local.Text ?? '');
        const seq = (tplSeqs.get(key) ?? 0) + 1;
        tplSeqs.set(key, seq);
        const S = state.strings;
        let reply;
        let failure = null;
        try {
            const result = await api.post('Templates/Validate', { EventKey: key, Text: value });
            if (result.status === 200 && result.body) {
                reply = result.body;
            } else {
                reply = { RequestError: page.requestError(S, result) };
                failure = page.failureText(result);
            }
        } catch (err) {
            reply = { RequestError: page.thrownError(S, err) };
            failure = page.thrownText(S, err);
        }

        if (seq === tplSeqs.get(key)) {
            state.ui.tplResults[key] = reply;
            tplChecked.set(key, value);
            if (!validationFailed(reply)) {
                state.ui.tplGood[key] = reply;
            }

            if (key === messageKey()) {
                showTemplatePreview();
            }
        }

        return { text: value, reply, failure };
    };

    const previewFailed = err => console.error('TgJelly: the template preview failed', err);

    /** Drops a pending debounced validation. */
    const cancelTemplateTimer = () => {
        if (tplTimer !== null) {
            clearTimeout(tplTimer);
            tplTimer = null;
        }
    };

    /** Shows another message: its page, its preview at once, and its key in the page address (no history entry). */
    const pickMessage = key => {
        cancelTemplateTimer();
        Object.assign(state.ui, { msgKey: key, tplResetAsk: false, tplTestResult: '' });
        const win = globalThis.window;
        if (win && win.location && win.history && typeof win.history.replaceState === 'function') {
            win.history.replaceState(win.history.state, '', hashWithMessage(win.location.hash, key));
        }

        page.renderSection('messages');
        return validateTemplate();
    };

    /**
     * Why an event's typed template can't be saved (plan 8, design §6.3), or null: empty text never saves (the server
     * would take it as "remove the override"; Reset is the way back), nor does text whose latest validation had errors.
     * @param {string} key the event key
     */
    const templateSaveError = key => {
        const S = state.strings;
        const form = state.forms[`tpl:${key}`];
        const value = form ? String(form.local.Text ?? '') : '';
        if (value.trim() === '') {
            return text(S, 'web.templates.notSavedEmpty');
        }

        const result = state.ui.tplResults[key];
        return tplChecked.get(key) === value && result && (result.Errors ?? []).length > 0 ? text(S, 'web.templates.notSavedErrors') : null;
    };

    /**
     * Sends one auto-save of an event's template: its typed text, with the current Revision. A text the preview hasn't
     * checked yet (its Validate is still on the way, or failed) is checked first, so a broken template never goes live.
     * On success the form starts over from the reply, keeping text typed while the request ran.
     * @param {string} key the event key
     */
    const sendTemplate = async key => {
        const S = state.strings;
        const name = `tpl:${key}`;
        if (!state.forms[name] || !state.templates) {
            return { status: 'failed', message: text(S, 'web.request.noTemplate') };
        }

        const reason = templateSaveError(key);
        if (reason) {
            return { status: 'invalid', message: reason };
        }

        let value = String(state.forms[name].local.Text ?? '');
        const known = state.ui.tplResults[key];
        if (tplChecked.get(key) !== value || !known || known.RequestError) {
            const checked = await validateTemplate(key);
            if (checked.failure) {
                return { status: 'failed', message: checked.failure };
            }

            if ((checked.reply.Errors ?? []).length > 0) {
                return { status: 'invalid', message: text(S, 'web.templates.notSavedErrors') };
            }

            value = checked.text;
        }

        return page.saveResult(await api.put(`Templates/${encodeURIComponent(key)}`, { ExpectedRevision: page.pageRevision(), Text: value }), {
            fields: ['Text'],
            saved: dto => {
                // The sent text is now the saved one; text typed while the request ran is still an edit.
                const now = state.forms[name];
                state.forms[name] = { base: { Text: value }, local: now ? now.local : { Text: value }, dirty: true, changed: [] };
                takeTemplates(dto);
                page.renderSection('messages');
            },
            conflict: current => {
                // The form keeps the admin's text on top of the server data (syncFormData): the admin's value wins.
                takeTemplates(current);
                page.renderSection('messages');
            },
            again: () => sendTemplate(key),
        });
    };

    /** Queues the save of an event's typed template, 1 s after the last keystroke or when the textarea is left. */
    const saveTemplate = (key, el) => page.queueSave({
        key: templateSaveKey(key),
        mode: 'debounce',
        delay: TEMPLATE_SAVE_MS,
        fields: [page.fieldSelector(el)],
        validate: () => templateSaveError(key),
        send: () => sendTemplate(key),
    });

    const publicUrlError = () => (validatePublicUrl(state.forms.settings && state.forms.settings.local.PublicUrl)
        ? null
        : text(state.strings, 'web.messages.urlInvalid'));

    /** A switch's new state on click: the opposite of the settings form's value at path. */
    const flipped = path => !valueAt(state.forms.settings && state.forms.settings.local, path);

    const section = {
        id: 'tgj-messages',
        load: async () => {
            await Promise.all([page.loadSettings(), loadTemplates()]);
            // Opening the section shows the preview of the selected message's template at once.
            validateTemplate().catch(previewFailed);
        },
        render: () => renderMessagesSection(state, state.strings, messagesUi()),
    };

    const actions = {
        // Clicking a field or a format puts it into the template at the caret, as typing (the input handler saves it).
        tplInsert: el => {
            const textarea = templateArea();
            if (!textarea) {
                return;
            }

            const kind = el.getAttribute('data-tgj-insert');
            const id = el.getAttribute('data-tgj-id');
            if (kind === 'field') {
                insertText(textarea, `{{ ${id} }}`);
            } else if (kind === 'format' && FORMATTING[Number(id)]) {
                const selected = String(textarea.value ?? '').slice(textarea.selectionStart, textarea.selectionEnd);
                const { value, select } = formatInsert(FORMATTING[Number(id)], selected);
                insertText(textarea, value, select);
            }
        },
        // Opens or closes a table in place (a re-render would cost the textarea its caret) and keeps the choice.
        tplTable: el => {
            const name = el.getAttribute('data-tgj-id');
            const panel = TABLES.includes(name) ? page.byId(`tgj-tbl-${name}`) : null;
            if (!panel) {
                return;
            }

            const open = el.getAttribute('aria-expanded') !== 'true';
            panel.hidden = !open;
            el.setAttribute('aria-expanded', String(open));
            rememberTable(name, open);
        },
        // A whole URL, chosen with a button: it saves at once (the field itself saves on blur or Enter).
        useSuggested: () => {
            if (state.settings && state.settings.PublicUrlSuggestion) {
                page.markDirty('settings', 'PublicUrl', state.settings.PublicUrlSuggestion);
                // The suggestion replaces whatever was typed and waiting, so the field shows it.
                page.releaseFields('PublicUrl');
                page.renderSection('messages');
                page.saveSetting('PublicUrl', 'now', null, { validate: publicUrlError });
            }
        },
        // Reset asks first, inline: Reset or Cancel.
        tplReset: () => {
            state.ui.tplResetAsk = true;
            page.renderSection('messages');
        },
        tplResetCancel: () => {
            state.ui.tplResetAsk = false;
            page.renderSection('messages');
        },
        tplResetConfirm: () => page.runAction('messages', async () => {
            const key = messageKey();
            const name = `tpl:${key}`;
            if (!state.templates) {
                return undefined;
            }

            const S = state.strings;
            // The reset replaces the typed text: a save still waiting for it is dropped, and one in flight is waited
            // for, so the text doesn't come back and the reset sends the Revision that save moved to.
            const saveKey = templateSaveKey(key);
            page.cancelSave(saveKey);
            page.releaseFields(saveKey);
            state.ui.tplBusy = true;
            page.renderSection('messages');
            await page.flush(saveKey);
            let result;
            try {
                result = await api.del(`Templates/${encodeURIComponent(key)}?expectedRevision=${page.pageRevision()}`);
            } finally {
                state.ui.tplBusy = false;
                state.ui.tplResetAsk = false;
            }

            if (result.status === 200 && result.body) {
                // The edits are gone: the form starts over from the built-in text, and the preview follows it.
                cancelTemplateTimer();
                delete state.forms[name];
                takeTemplates(result.body);
                validateTemplate().catch(previewFailed);
                return undefined;
            }

            if (result.status === 409 && result.body && result.body.Current) {
                takeTemplates(result.body.Current);
                state.ui.errors.messages = t(S, 'web.templates.conflict');
                return undefined;
            }

            return result;
        }),
        // Sends the typed text, rendered with sample values, to the chosen destination; the outcome shows by the button.
        tplTest: () => page.runAction('messages', async () => {
            const key = messageKey();
            const form = state.forms[`tpl:${key}`];
            const target = templateTestTarget();
            if (!form || !target) {
                return undefined;
            }

            const S = state.strings;
            state.ui.tplTesting = true;
            state.ui.tplTestResult = '';
            page.renderSection('messages');
            let result;
            try {
                result = await api.post('Templates/Test', { EventKey: key, Text: String(form.local.Text ?? ''), DestinationId: target });
            } finally {
                state.ui.tplTesting = false;
            }

            if (result.status !== 200 || !result.body) {
                state.ui.tplTestResult = page.requestError(S, result);
                return undefined;
            }

            const reply = result.body;
            const health = reply.Health;
            state.ui.tplTestResult = reply.Sent
                ? t(S, 'web.templates.sent')
                : (reply.Error ? esc(reply.Error) : page.healthText(health && health.Status, S));
            // The send may have changed the destination's health.
            await page.reloadSection('destinations', { fresh: true });
            return undefined;
        }),
    };

    const inputs = {
        // A select fires input and then change; the change is the choice. A section shows its first message.
        msgSection: (el, event) => {
            const first = items().find(i => groupOf(i.EventKey) === el.value);
            if (event.type !== 'change' || !first) {
                return undefined;
            }

            return groupOf(messageKey()) === el.value ? undefined : pickMessage(first.EventKey);
        },
        msgKey: (el, event) => (event.type === 'change' && itemOf(el.value) ? pickMessage(el.value) : undefined),
        // Switches are buttons: they reach here on click. Each saves at once, with its cue beside it; a switch the
        // message doesn't have is ignored.
        msgSwitch: (el, event) => {
            const key = el.getAttribute('data-tgj-id');
            const name = el.getAttribute('data-tgj-key');
            if (event.type !== 'click' || !state.forms.settings || !applicableSwitches(itemOf(key)).includes(name)) {
                return;
            }

            const path = switchPath(key, name);
            page.markDirty('settings', path, flipped(path));
            page.renderSection('messages');
            page.saveSetting(path, 'now', el, { key: switchSaveKey(key, name) });
        },
        msgAnnounce: (el, event) => {
            if (event.type === 'click' && state.forms.settings) {
                page.markDirty('settings', ANNOUNCE_PATH, flipped(ANNOUNCE_PATH));
                page.renderSection('messages');
                page.saveSetting(ANNOUNCE_PATH, 'now', el);
            }
        },
        // A partial URL can pass the rules, and would go live in message buttons: this saves on blur or Enter only.
        publicUrl: (el, event) => {
            if (event.type === 'input') {
                page.markDirty('settings', 'PublicUrl', String(el.value ?? ''));
                page.saveSetting('PublicUrl', 'blur', el, { validate: publicUrlError });
            }
        },
        // Typing records the edit; after a pause it refreshes the preview pane (only the pane: re-rendering would move the
        // caret), and after a longer one the text saves itself (plan 8, design §6.1). Typed text acts on input only.
        tplText: (el, event) => {
            const own = el.getAttribute('data-tgj-key');
            const key = itemOf(own) ? own : messageKey();
            if (event.type !== 'input' || !state.forms[`tpl:${key}`]) {
                return;
            }

            page.markDirty(`tpl:${key}`, 'Text', String(el.value ?? ''));
            // The preview's pieces are for the text before this keystroke.
            lightPiece(null);
            cancelTemplateTimer();
            tplTimer = setTimeout(() => {
                tplTimer = null;
                validateTemplate(key).catch(previewFailed);
            }, PREVIEW_DEBOUNCE_MS);
            saveTemplate(key, el);
        },
        tplTestTo: el => {
            state.ui.tplTestTo = String(el.value ?? '');
        },
    };

    return { section, actions, inputs, hover, scroll, hide: cancelTemplateTimer };
}
