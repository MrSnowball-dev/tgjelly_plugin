// TgJelly dashboard page (spec §11.1). Loaded through data-controller="__plugin/tgjelly.js".
//
// Layout of this module:
// - pure helpers and render functions are named exports, so tests/web can call them without a DOM;
// - every render function returns HTML built only from esc()/t()/plural() output, so server text is always escaped;
// - the default export is the page controller: it owns the state, the loaders, the click actions and the Status poller.
// The strings table is always called S, so the key check in tests/web can find the key at every t() and plural() call.
//
// Sibling modules are imported by the URL Jellyfin serves them under (plan 8); tests/web/jellyfin-resolve.mjs maps it
// to the file for Node.
import {
    esc, label, card, row, actionButton, inlineConfirm, notice, emptyState, textField, switchControl, selectField, numberField,
} from './configurationpage?name=tgjelly-ui.js';
import { createAutosave, CONFLICT_KEY } from './configurationpage?name=tgjelly-autosave.js';

// esc lives in tgjelly-ui.js; it stays a named export here for the page's callers and tests.
export { esc };

const POLL_MS = 3000;
// The template preview validates this long after the last keystroke.
const PREVIEW_DEBOUNCE_MS = 500;
// The template text saves this long after the last keystroke (design §6.1).
const TEMPLATE_SAVE_MS = 1000;

/** Connection states shown in the error color (the ConnectionState names from GET Status). */
const PROBLEM_STATES = new Set(['TokenRejected', 'WebhookConflict', 'ConflictingInstance', 'Unreachable', 'InvalidBaseUrl', 'Incompatible']);

/**
 * Looks up a string and fills its {name} placeholders. The result is HTML: the text and the args are both escaped.
 * A missing key gives the key itself; a placeholder without an arg stays as it is.
 * @param {Record<string, string> | null} S the string table from GET Strings
 * @param {string} key
 * @param {Record<string, unknown>} [args]
 * @returns {string}
 */
export function t(S, key, args = {}) {
    return fill(S, key, args, esc);
}

/**
 * The plain-text twin of t(): the same lookup and fallbacks, nothing escaped. For the labels passed to the
 * tgjelly-ui.js helpers, which escape their text themselves; t() stays for HTML written here.
 * @param {Record<string, string> | null} S
 * @param {string} key
 * @param {Record<string, unknown>} [args]
 * @returns {string}
 */
export function text(S, key, args = {}) {
    return fill(S, key, args, String);
}

function fill(S, key, args, encode) {
    const source = S && Object.hasOwn(S, key) ? String(S[key]) : key;
    let out = '';
    let last = 0;
    for (const match of source.matchAll(/\{(\w+)\}/g)) {
        out += encode(source.slice(last, match.index)) + encode(Object.hasOwn(args, match[1]) ? args[match[1]] ?? '' : match[0]);
        last = match.index + match[0].length;
    }

    return out + encode(source.slice(last));
}

/** The key plural() and pluralText() look up: key.<category>, or key.other when the table lacks that category. */
function pluralKey(S, key, count, lang) {
    let category;
    try {
        category = new Intl.PluralRules(lang).select(count);
    } catch {
        category = new Intl.PluralRules('en').select(count);
    }

    return S && Object.hasOwn(S, `${key}.${category}`) ? `${key}.${category}` : `${key}.other`;
}

/**
 * Picks key.one or key.other (any other plural category falls back to .other) and passes the formatted count as {count}.
 * @param {Record<string, string> | null} S
 * @param {string} key the key without its .one/.other suffix
 * @param {number} count
 * @param {Record<string, unknown>} [args]
 * @param {string} [lang]
 * @returns {string} HTML, as t() returns
 */
export function plural(S, key, count, args = {}, lang = 'en') {
    return t(S, pluralKey(S, key, count, lang), { ...args, count: formatNumber(count, lang) });
}

/** plural() as plain text, as text() is to t(). */
export function pluralText(S, key, count, args = {}, lang = 'en') {
    return text(S, pluralKey(S, key, count, lang), { ...args, count: formatNumber(count, lang) });
}

/**
 * Formats a number for the page's language, e.g. 1,000.
 * @param {number} value
 * @param {string} [lang]
 * @returns {string}
 */
export function formatNumber(value, lang = 'en') {
    try {
        return new Intl.NumberFormat(lang).format(value);
    } catch {
        return String(value);
    }
}

/**
 * Formats an ISO timestamp from the API as local date and time.
 * @param {string} iso
 * @param {string} [lang]
 * @returns {string}
 */
export function formatTime(iso, lang = 'en') {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
        return String(iso ?? '');
    }

    try {
        return date.toLocaleString(lang);
    } catch {
        return date.toISOString();
    }
}

function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/**
 * Flattens nested objects into dotted paths: { a: { b: 1 } } gives { 'a.b': 1 }. Arrays and empty objects are values.
 * @param {object} obj
 * @returns {Record<string, unknown>}
 */
export function flatten(obj) {
    const out = {};
    const walk = (node, prefix) => {
        for (const [key, value] of Object.entries(node ?? {})) {
            if (isPlainObject(value) && Object.keys(value).length > 0) {
                walk(value, prefix + key + '.');
            } else {
                out[prefix + key] = value;
            }
        }
    };
    walk(obj, '');
    return out;
}

/**
 * Turns dotted paths back into nested objects; the inverse of flatten().
 * @param {Record<string, unknown>} flat
 * @returns {object}
 */
export function unflatten(flat) {
    const out = {};
    for (const [path, value] of Object.entries(flat ?? {})) {
        const parts = path.split('.');
        let node = out;
        for (const part of parts.slice(0, -1)) {
            node = node[part] = isPlainObject(node[part]) ? node[part] : {};
        }

        node[parts.at(-1)] = value;
    }

    return out;
}

function sameValue(a, b) {
    return a === b || JSON.stringify(a) === JSON.stringify(b);
}

/**
 * Merges the user's unsaved edits onto fresh server data after a 409 (spec §10.4). All three are flat (see flatten()).
 * @param {Record<string, unknown>} base what the user started editing from
 * @param {Record<string, unknown>} local the form's current values
 * @param {Record<string, unknown>} server the fresh data from the 409
 * @returns {{ merged: Record<string, unknown>, changed: string[] }} merged is server with every path the user edited
 *   taken from local; changed lists (sorted) the paths both the user and the server changed, to highlight.
 */
export function mergeConflict(base, local, server) {
    const merged = { ...server };
    const changed = [];
    for (const path of new Set([...Object.keys(base), ...Object.keys(local)])) {
        if (sameValue(local[path], base[path])) {
            continue;
        }

        if (Object.hasOwn(local, path)) {
            merged[path] = local[path];
        } else {
            delete merged[path];
        }

        if (!sameValue(server[path], base[path])) {
            changed.push(path);
        }
    }

    return { merged, changed: changed.sort() };
}

/**
 * A fresh form state for an editor (spec §10.4): base is the server data the edits start from, local the values shown.
 * Given an existing dirty form, it keeps the user's edits on top of the fresh data instead (see mergeConflict()); base
 * becomes fresh, and changed collects the paths the server also changed, to highlight. Data is a plain object.
 * @param {{ base: object, local: object, dirty: boolean, changed: string[] } | null} form the current form, if any
 * @param {object} fresh the server's data
 * @returns {{ base: object, local: object, dirty: boolean, changed: string[] }}
 */
export function syncFormData(form, fresh) {
    if (!form || !form.dirty) {
        return { base: fresh, local: structuredClone(fresh), dirty: false, changed: [] };
    }

    const server = flatten(fresh);
    const { merged, changed } = mergeConflict(flatten(form.base), flatten(form.local), server);
    const dirty = Object.keys({ ...merged, ...server }).some(path => !sameValue(merged[path], server[path]));
    return {
        base: fresh,
        local: unflatten(merged),
        dirty,
        changed: dirty ? [...new Set([...form.changed, ...changed])].sort() : [],
    };
}

/**
 * Returns a copy of the form with one local value set and marked dirty.
 * @param {{ base: object, local: object, dirty: boolean, changed: string[] }} form
 * @param {string} path a dotted path, e.g. 'Batch.Seconds'
 * @param {unknown} value
 */
export function editForm(form, path, value) {
    const local = structuredClone(form.local);
    const parts = path.split('.');
    let node = local;
    for (const part of parts.slice(0, -1)) {
        node = node[part] = isPlainObject(node[part]) ? node[part] : {};
    }

    node[parts.at(-1)] = value;
    return { ...form, local, dirty: true };
}

async function readBody(response) {
    if (response.status === 204 || typeof response.text !== 'function') {
        return null;
    }

    const text = await response.text();
    if (!text) {
        return null;
    }

    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

/**
 * Wraps Jellyfin's ApiClient for the TgJelly API. Every call resolves { status, body }: a 204 or an empty body gives
 * body null, and a 4xx/5xx is a value, not a throw, so callers can read 400/409 bodies. A network failure still throws.
 * @param {object} ApiClient window.ApiClient (jellyfin-apiclient)
 */
export function createApi(ApiClient) {
    const send = async (type, path, body) => {
        // No dataType: with dataType 'json' the client can't read a 204 or a 4xx body, so the Response is parsed here.
        const request = { url: ApiClient.getUrl('TgJelly/' + path), type, contentType: 'application/json' };
        if (body !== undefined) {
            request.data = JSON.stringify(body);
        }

        let response;
        try {
            response = await ApiClient.fetch(request);
        } catch (err) {
            // jellyfin-apiclient rejects with the Response itself on a non-2xx status.
            if (err && typeof err.status === 'number') {
                response = err;
            } else {
                throw err;
            }
        }

        return { status: response.status, body: await readBody(response) };
    };

    return {
        get: path => send('GET', path),
        post: (path, body) => send('POST', path, body),
        put: (path, body) => send('PUT', path, body),
        del: path => send('DELETE', path),
    };
}

/**
 * The error text for a failed API result: the server's Message when it sent one, otherwise the status code.
 * @param {Record<string, string>} S
 * @param {{ status: number, body: any }} result
 * @returns {string} HTML
 */
export function requestError(S, result) {
    return result.body && result.body.Message
        ? esc(result.body.Message)
        : t(S, 'web.requestFailed', { reason: result.status });
}

/**
 * Renders the Status section (spec §6.6 item 1) from GET Status.
 * @param {object} status the StatusDto
 * @param {Record<string, string>} S
 * @param {string} [lang]
 * @returns {string} HTML
 */
export function renderStatus(status, S, lang = 'en') {
    const counters = status.Counters ?? {};
    const dropped = (counters.DroppedIntake ?? 0) + (counters.DroppedOutbox ?? 0) + (counters.Evictions ?? 0);
    const parts = [];

    if (!status.Compatible) {
        parts.push(notice({ kind: 'error', extraClass: 'tgj-compat', html: esc(status.CompatMessage) }));
    } else {
        // Connecting and the two waits are in progress; no token is neutral.
        const tone = PROBLEM_STATES.has(status.ConnectionState) ? 'bad'
            : status.ConnectionState === 'Connected' ? 'good'
                : status.ConnectionState === 'NoToken' ? 'neutral' : 'progress';
        const takeOver = status.ConnectionState === 'WebhookConflict'
            ? actionButton({ label: text(S, 'web.status.takeOver'), act: 'takeover', kind: 'primary' })
            : '';
        // The label holds the state word; the server's message (long when something is wrong) is plain text beside it.
        const word = status.ConnectionState === 'Connected' ? text(S, 'web.status.connected') : connectionLabel(status, S).text;
        const message = status.ConnectionMessage && status.ConnectionMessage !== word
            ? `<span class="tgj-conn-message">${esc(status.ConnectionMessage)}</span>` : '';
        parts.push(row({
            label: text(S, 'web.status.connection'),
            control: label(word, tone) + message + takeOver,
        }));
    }

    if (!status.HasToken) {
        // Before a bot is connected the card is just the connection line, below the Connect card.
        return card({ title: text(S, 'web.status.title'), body: parts.join('') });
    }

    parts.push(notice({ kind: 'info', html: text(S, 'web.bot.access') }));
    parts.push(row({
        label: text(S, 'web.status.activity'),
        extraClass: 'tgj-row--stack',
        control: `<span class="tgj-row-hint">${t(S, 'web.status.counts', {
            pending: formatNumber(status.PendingMedia ?? 0, lang),
            outbox: formatNumber(status.OutboxCount ?? 0, lang),
            dropped: formatNumber(dropped, lang),
        })}</span>`,
    }));

    for (const entry of status.Notices ?? []) {
        parts.push(notice({
            kind: 'info',
            html: `<span class="tgj-notice-text">${esc(entry.Text)}</span>`
                + actionButton({ label: text(S, 'web.status.dismiss'), act: 'dismiss', id: entry.Id }),
        }));
    }

    const errors = status.LastErrors ?? [];
    if (errors.length > 0) {
        const items = [...errors].reverse().map(e => `<li>${t(S, 'web.status.error', {
            time: formatTime(e.At, lang),
            source: e.Source,
            message: e.Message,
        })}</li>`);
        parts.push(row({
            label: text(S, 'web.status.errors'),
            extraClass: 'tgj-row--stack',
            control: `<ul class="tgj-errors tgj-row-hint">${items.join('')}</ul>`,
        }));
    }

    return card({ title: text(S, 'web.status.title'), body: parts.join('') });
}

/** A link to Telegram, or '' when the URL isn't one the server should have sent. */
function telegramLink(url, label) {
    return typeof url === 'string' && url.startsWith('https://t.me/')
        ? `<a class="tgj-btn" href="${esc(url)}" target="_blank" rel="noopener noreferrer">${label}</a>`
        : '';
}

/**
 * The proxy card (spec §6.6 item 5, §9.1; design §6.1): under Advanced, and inside the "Can't reach Telegram?" panel.
 * Its fields auto-save together, as one save keyed 'proxy', when the focus leaves the card (every proxy save restarts
 * the bot session); so they all carry data-tgj-save="proxy", and the card has one cue, beside the mode.
 * @param {object | null} settings the SettingsDto (null until it loads)
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is the editor state: local.Mode and local.Url. The proxy username and
 *   password are not part of it.
 * @param {{ nested?: boolean }} [options] nested: inside another card (the connect panel), so no card of its own
 * @returns {string} HTML
 */
export function renderProxyForm(settings, S, ui, { nested = false } = {}) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local;
    const modes = ['System', 'None', 'Http', 'Socks5'].map(value => ({ value, label: text(S, PROXY_MODE_LABELS[value]) }));
    const save = { 'data-tgj-save': 'proxy', autocomplete: 'off' };
    // Username and password are never written into the HTML (spec §10.2): the controller puts typed text back into the
    // inputs after a render.
    const field = (input, labelKey, type, value, placeholder = '') => row({
        label: text(S, labelKey),
        control: textField({ input, value, type, placeholder, attrs: { ...save, 'aria-label': text(S, labelKey) } }),
    });
    const rows = [row({
        label: text(S, 'web.proxy.mode'),
        cueKey: 'proxy',
        control: selectField({ input: 'proxyMode', value: local.Mode, options: modes, attrs: { 'data-tgj-save': 'proxy', 'aria-label': text(S, 'web.proxy.mode') } }),
    })];
    if (local.Mode === 'Http' || local.Mode === 'Socks5') {
        rows.push(field('proxyUrl', 'web.proxy.url', 'text', local.Url ?? ''));
    }

    rows.push(field('proxyUsername', 'web.proxy.username', 'text', '', settings.HasProxyUsername ? text(S, 'web.proxy.saved') : ''));
    rows.push(field('proxyPassword', 'web.proxy.password', 'password', '', settings.HasProxyPassword ? text(S, 'web.proxy.saved') : ''));
    return nested
        ? `<div class="tgj-proxy-form"><h3 class="tgj-subtitle">${t(S, 'web.proxy.title')}</h3>${rows.join('')}</div>`
        : card({ title: text(S, 'web.proxy.title'), body: rows.join(''), extraClass: 'tgj-proxy-form' });
}

const PROXY_MODE_LABELS = { System: 'web.proxy.modeSystem', None: 'web.proxy.modeNone', Http: 'web.proxy.modeHttp', Socks5: 'web.proxy.modeSocks5' };

/**
 * The token form (spec §6.1): a password input that is never pre-filled, the two notes for an existing bot, the
 * profile checkbox and Connect. Connecting is an explicit action: nothing here auto-saves.
 */
function renderTokenForm(ui, S) {
    const busy = Boolean(ui.saving);
    const notes = ui.mode === 'existing'
        ? `<p class="tgj-row-hint">${t(S, 'web.connect.noteRestrict')}</p><p class="tgj-row-hint">${t(S, 'web.connect.noteOther')}</p>`
        : '';
    return '<div class="tgj-token-form">'
        + row({
            label: text(S, 'web.connect.tokenLabel'),
            extraClass: 'tgj-row--stack',
            control: textField({ input: 'token', value: '', type: 'password', attrs: { autocomplete: 'off', 'aria-label': text(S, 'web.connect.tokenLabel'), disabled: busy } }),
        })
        + notes
        + '<label class="tgj-check"><input type="checkbox" data-tgj-input="setProfile"'
        + `${ui.setProfile ? ' checked' : ''}${busy ? ' disabled' : ''} /><span>${t(S, 'web.connect.setProfile')}</span></label>`
        + `<div class="tgj-actions">${actionButton({ label: text(S, 'web.connect.save'), act: 'saveToken', kind: 'primary', disabled: busy })}`
        + `${ui.hasToken ? actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelToken', disabled: busy }) : ''}</div></div>`;
}

/**
 * The connect card (spec §6.1): shown without a token, or while changing it, and the outcome of the last save.
 * @param {{ hasToken: boolean, changingToken?: boolean, mode?: string | null, saving?: boolean, setProfile?: boolean,
 *   result?: object | null, unreachable?: boolean, proxy?: object }} ui hasToken comes from GET Status; result is
 *   { kind: 'Connected' | 'NeedsConfirm' | 'Error', message, username, webhookDeleted }; proxy is the proxy form's ui
 * @param {Record<string, string>} S
 * @param {{ OpenBot?: string } | null} links
 * @param {object | null} [settings] for the proxy panel
 * @returns {string} HTML
 */
export function renderConnect(ui, S, links, settings = null) {
    const result = ui.result;
    const busy = Boolean(ui.saving);
    if (result && result.kind === 'Connected') {
        return result.webhookDeleted ? notice({ kind: 'info', html: t(S, 'web.connect.webhookRemoved') }) : '';
    }

    if (ui.hasToken && !ui.changingToken) {
        return '';
    }

    const title = text(S, 'web.connect.title');
    if (result && result.kind === 'NeedsConfirm') {
        return card({
            title,
            body: `<p>${esc(result.message)}</p><div class="tgj-actions">`
                + actionButton({ label: text(S, 'web.connect.switch'), act: 'switchBot', kind: 'primary', disabled: busy })
                + actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelSwitch', disabled: busy })
                + '</div>',
        });
    }

    const parts = [];
    if (!ui.mode) {
        parts.push('<div class="tgj-actions">'
            + actionButton({ label: text(S, 'web.connect.createNew'), act: 'connectNew', kind: 'primary', disabled: busy })
            + actionButton({ label: text(S, 'web.connect.haveBot'), act: 'connectExisting', disabled: busy })
            + `${ui.hasToken ? actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelToken', disabled: busy }) : ''}</div>`);
    } else {
        if (ui.mode === 'new') {
            parts.push('<ol class="tgj-steps">'
                + `<li>${t(S, 'web.connect.step1')} ${telegramLink('https://t.me/BotFather', t(S, 'web.connect.openBotFather'))}</li>`
                + `<li>${t(S, 'web.connect.step2')}</li><li>${t(S, 'web.connect.step3')}</li></ol>`);
        }

        parts.push(renderTokenForm(ui, S));
    }

    if (result && result.kind === 'Error') {
        parts.push(notice({ kind: 'error', html: esc(result.message) }));
    }

    if (ui.unreachable) {
        parts.push(`<div class="tgj-unreachable"><h3>${t(S, 'web.connect.unreachableTitle')}</h3>`
            + `<p class="tgj-row-hint">${t(S, 'web.connect.unreachableHint')}</p>${renderProxyForm(settings, S, ui.proxy ?? {}, { nested: true })}</div>`);
    }

    return card({ title, body: parts.join('') });
}

/**
 * The Bot card (spec §6.6 item 2, §6.4): the username, the access reminder, Open in Telegram, Change token and
 * Disconnect bot with its inline confirmation.
 * @param {object} status the StatusDto
 * @param {{ OpenBot?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ confirmDisconnect?: boolean, saving?: boolean }} ui
 * @returns {string} HTML
 */
export function renderBot(status, links, S, ui) {
    if (!status || !status.HasToken) {
        return '';
    }

    const busy = Boolean(ui.saving);
    const disconnect = ui.confirmDisconnect
        ? inlineConfirm({
            question: text(S, 'web.bot.confirmDisconnect'),
            confirmLabel: text(S, 'web.bot.confirmYes'),
            confirmAct: 'disconnectYes',
            cancelLabel: text(S, 'web.connect.cancel'),
            cancelAct: 'disconnectNo',
        })
        : actionButton({ label: text(S, 'web.bot.disconnect'), act: 'disconnect', kind: 'danger', disabled: busy });
    return card({
        title: text(S, 'web.bot.title'),
        body: row({ label: text(S, 'web.bot.name'), control: `<b>@${esc(status.BotUsername)}</b>` })
            + `<div class="tgj-actions">${telegramLink(links && links.OpenBot, t(S, 'web.bot.open'))}`
            + `${actionButton({ label: text(S, 'web.bot.change'), act: 'changeToken', disabled: busy })}${disconnect}</div>`,
    });
}

/** The proxy editor's data from a SettingsDto. Username and Password are absent until the user types in them. */
export function proxyFormData(settings) {
    return { Mode: settings.ProxyMode, Url: settings.ProxyUrl };
}

const PRESETS = ['Everything', 'NewMedia', 'AdminAlerts', 'Custom'];

/** The editor's data for one destination from the DestinationDto: the fields its auto-saves can send. */
export function destinationFormData(dest) {
    return {
        Enabled: Boolean(dest.Enabled),
        Preset: dest.Preset,
        Events: [...(dest.Events ?? [])],
        LibraryIds: [...(dest.LibraryIds ?? [])],
        SilentGroups: [...(dest.SilentGroups ?? [])],
    };
}

/**
 * The body of a destination's PUT: only the fields that differ from base, plus ExpectedRevision (spec §7.2, §10.4).
 * Events make the server switch the destination to Custom; a Preset other than Custom sends the Preset alone.
 * @param {object} base the form data the edit started from
 * @param {object} local the form's current data
 * @param {number} revision
 * @returns {object | null} null when nothing changed
 */
export function destinationChanges(base, local, revision) {
    const body = { ExpectedRevision: revision };
    const differs = key => !sameValue(local[key], base[key]);
    if (local.Preset === 'Custom' && differs('Events')) {
        body.Events = local.Events;
    } else if (differs('Preset')) {
        body.Preset = local.Preset;
    }

    for (const key of ['LibraryIds', 'SilentGroups', 'Enabled']) {
        if (differs(key)) {
            body[key] = local[key];
        }
    }

    return Object.keys(body).length > 1 ? body : null;
}

const libraryKey = id => String(id).replaceAll('-', '').toLowerCase();
const sameLibrary = (a, b) => libraryKey(a) === libraryKey(b);

/**
 * The dashboard's health words and colours (design §7). The bot's dest.health.* labels carry emoji by design, so the
 * dashboard has its own web.dest.health.* copies and leaves the bot's strings alone.
 */
const HEALTH = {
    Ok: { key: 'web.dest.health.Ok', tone: 'good' },
    PhotosBlocked: { key: 'web.dest.health.PhotosBlocked', tone: 'attention' },
    NeedsPermissions: { key: 'web.dest.health.NeedsPermissions', tone: 'bad' },
    TopicMissing: { key: 'web.dest.health.TopicMissing', tone: 'bad' },
    Lost: { key: 'web.dest.health.Lost', tone: 'bad' },
};

/** A health status as a label's { tone, text }; a status this page doesn't know shows as its name, neutral. */
const healthState = (status, S) => {
    const known = HEALTH[status || 'Ok'];
    return known ? { tone: known.tone, text: text(S, known.key) } : { tone: 'neutral', text: String(status) };
};

/** A health status as escaped HTML text, for an outcome line ("Check again", a test send). */
export const healthText = (status, S) => esc(healthState(status, S).text);

/** The health as labels: the status, plus "Photos blocked" when photos are blocked too. */
function healthLabels(health, S) {
    const status = health && health.Status ? health.Status : 'Ok';
    const asLabel = ({ tone, text: words }) => label(words, tone);
    return asLabel(healthState(status, S))
        + (health && health.PhotosBlocked && status !== 'PhotosBlocked' ? asLabel(healthState('PhotosBlocked', S)) : '');
}

/** The save key (and cue key) of one control in a destination's editor: 'dest:<id>:<what>'. */
const destSaveKey = (id, what) => `dest:${id}:${what}`;

/**
 * The editor card for one destination (spec §7.2, §7.3; design §6.1, §6.1a): the preset, the event switches by group,
 * the libraries and the silent groups. Every control saves itself at once (data-tgj-save, with its cue beside it);
 * Send test, Check again and Remove stay actions, and Remove asks inline first.
 * @param {object} dest the DestinationDto
 * @param {{ EventKeys: string[], Groups: string[] }} catalog
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object }, confirmRemove?: boolean, leave?: boolean, result?: string, presetAsk?: boolean }} ui
 *   form defaults to the destination's own values; result is HTML shown under the buttons; presetAsk shows the
 *   "replace your custom events?" question under the preset
 * @returns {string} HTML
 */
export function renderEditor(dest, catalog, libraries, S, ui) {
    const local = (ui.form && ui.form.local) || destinationFormData(dest);
    const id = dest.Id;
    const toggle = (input, what, checked, label, attrs) => {
        const key = destSaveKey(id, what);
        return row({ label, control: switchControl({ input, checked, label, attrs: { 'data-tgj-id': id, ...attrs, 'data-tgj-save': key } }), cueKey: key });
    };
    const parts = [];

    const presetKey = destSaveKey(id, 'Preset');
    parts.push(row({
        label: text(S, 'web.dest.preset'),
        control: selectField({
            input: 'destPreset',
            value: local.Preset,
            options: PRESETS.map(p => ({ value: p, label: text(S, `dest.preset.${p}`) })),
            attrs: { 'data-tgj-id': id, 'data-tgj-save': presetKey, 'aria-label': text(S, 'web.dest.preset') },
        }),
        cueKey: presetKey,
    }));
    if (ui.presetAsk) {
        parts.push(inlineConfirm({
            question: text(S, 'web.dest.presetConfirm'),
            confirmLabel: text(S, 'web.dest.presetReplace'),
            confirmAct: 'presetReplace',
            cancelAct: 'presetCancel',
            cancelLabel: text(S, 'dest.btn.cancel'),
            id,
        }));
    }

    const events = new Set(local.Events);
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.events.title')}</h3>`);
    for (const group of catalog.Groups) {
        const keys = catalog.EventKeys.filter(k => k.startsWith(group + '.'));
        parts.push(`<h4 class="tgj-group-title">${t(S, `groups.${group}`)}</h4>`
            + keys.map(k => toggle('destEvent', `event:${k}`, events.has(k), text(S, `dest.event.${k}`), { 'data-tgj-key': k })).join(''));
    }

    // Removing a leftover library has no switch of its own to show the cue beside, so the Libraries title carries it.
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.libraries.title')} `
        + `<span class="tgj-cue" data-tgj-cue="${esc(destSaveKey(id, 'libraries'))}" aria-live="polite"></span></h3>`);
    if (dest.LibraryFilterBroken) {
        parts.push(notice({ kind: 'error', html: t(S, 'web.dest.librariesRemoved') }));
    }

    const chosen = new Set(local.LibraryIds.map(libraryKey));
    parts.push(libraries.map(l => toggle('destLibrary', `lib:${l.Id}`, chosen.has(libraryKey(l.Id)), l.Name, { 'data-tgj-lib': l.Id })).join(''));
    for (const gone of (dest.RemovedLibraryIds ?? []).filter(x => local.LibraryIds.some(y => sameLibrary(x, y)))) {
        parts.push(row({
            label: text(S, 'dest.libraries.removed', { id: gone }),
            control: actionButton({ label: text(S, 'web.dest.removeLibrary'), act: 'removeLibrary', id, attrs: { 'data-tgj-lib': gone } }),
        }));
    }

    parts.push(`<p class="tgj-row-hint">${t(S, chosen.size === 0 ? 'dest.libraries.all' : 'dest.libraries.hint')}</p>`);

    const silent = new Set(local.SilentGroups);
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.silent.title')}</h3>`
        + catalog.Groups.map(g => toggle('destSilent', `silent:${g}`, silent.has(g), text(S, `groups.${g}`), { 'data-tgj-group': g })).join('')
        + `<p class="tgj-row-hint">${t(S, 'dest.silent.hint')}</p>`);

    parts.push('<div class="tgj-actions">'
        + actionButton({ label: text(S, 'dest.btn.test'), act: 'testDest', id })
        + actionButton({ label: text(S, 'dest.btn.check'), act: 'checkDest', id })
        + (ui.confirmRemove ? '' : actionButton({ label: text(S, 'dest.btn.remove'), act: 'remove', kind: 'danger', id }))
        + '</div>');
    if (ui.confirmRemove) {
        // "Bot leaves the chat" is part of the question, not a setting: it saves nothing.
        parts.push(row({
            label: text(S, 'web.dest.leave'),
            control: switchControl({ input: 'destLeave', checked: Boolean(ui.leave), label: text(S, 'web.dest.leave'), attrs: { 'data-tgj-id': id } }),
        }));
        parts.push(inlineConfirm({
            question: text(S, 'web.dest.confirmRemove', { title: dest.Title }),
            confirmLabel: text(S, 'dest.btn.remove'),
            confirmAct: 'removeYes',
            cancelAct: 'removeNo',
            cancelLabel: text(S, 'dest.btn.cancel'),
            id,
        }));
    }

    if (ui.result) {
        parts.push(`<p class="tgj-row-hint tgj-dest-result">${ui.result}</p>`);
    }

    return card({ title: dest.Title, description: destinationMeta(dest, S), body: parts.join(''), extraClass: 'tgj-dest-editor' });
}

/** A destination's kind and preset, as plain text: "Supergroup · Everything". */
const destinationMeta = (dest, S) => text(S, 'web.dest.meta', { kind: text(S, `dest.kind.${dest.Kind}`), preset: text(S, `dest.preset.${dest.Preset}`) });

/**
 * The Destinations section (spec §6.6 item 3; design §5.2): a card with one row per destination (its title, the kind and
 * preset muted, the health as coloured words, the Sending switch and Edit), then the open destination's editor card.
 * Add destination, and "Set up manually" once the manual block is unlocked (spec §6.5), close the list, or make its
 * empty state.
 * @param {object | null} dto the DestinationsDto
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {{ StartAdd?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ editing?: string | null, forms?: Record<string, object>, confirmRemove?: string | null, leave?: boolean,
 *   results?: Record<string, string>, presetAsk?: string | null }} ui forms is keyed 'dest:<id>' like the controller's
 *   state.forms; presetAsk is the id whose editor asks before leaving Custom
 * @returns {string} HTML
 */
export function renderDestinations(dto, libraries, links, S, ui) {
    if (!dto) {
        return '';
    }

    const forms = ui.forms ?? {};
    const add = telegramLink(links && links.StartAdd, t(S, 'web.dest.add'))
        + (dto.ManualUnlocked
            ? `<span class="tgj-row-hint">${t(S, 'web.dest.manualHint')}</span>${actionButton({ label: text(S, 'web.dest.manualLink'), act: 'open-manual' })}`
            : '');
    const rows = dto.Items.map(dest => {
        const form = forms[`dest:${dest.Id}`];
        const key = destSaveKey(dest.Id, 'Enabled');
        return row({
            label: dest.Title,
            hint: destinationMeta(dest, S),
            extraClass: 'tgj-dest-row',
            control: healthLabels(dest.Health, S)
                + `<span class="tgj-switch-pair"><span class="tgj-switch-label">${t(S, 'web.dest.enabled')}</span>`
                + switchControl({
                    input: 'destEnabled',
                    checked: form ? Boolean(form.local.Enabled) : Boolean(dest.Enabled),
                    label: text(S, 'web.dest.enabled'),
                    attrs: { 'data-tgj-id': dest.Id, 'data-tgj-save': key },
                })
                + '</span>'
                + actionButton({ label: text(S, ui.editing === dest.Id ? 'web.dest.close' : 'web.dest.edit'), act: 'edit', id: dest.Id }),
            cueKey: key,
        });
    });
    const body = rows.length === 0
        ? emptyState({ text: text(S, 'web.dest.empty'), actionHtml: add })
        : rows.join('') + `<div class="tgj-actions">${add}</div>`;
    const cards = [card({ title: text(S, 'web.dest.title'), body })];

    const editing = dto.Items.find(d => d.Id === ui.editing);
    if (editing) {
        cards.push(renderEditor(editing, dto.Catalog, libraries ?? [], S, {
            form: forms[`dest:${editing.Id}`],
            confirmRemove: ui.confirmRemove === editing.Id,
            leave: ui.leave,
            result: (ui.results ?? {})[editing.Id],
            presetAsk: ui.presetAsk === editing.Id,
        }));
    }

    return `<div class="tgj-stack">${cards.join('')}</div>`;
}


/** The preset choices for the manual block: every preset but Custom. */
const presetOptions = S => PRESETS.filter(p => p !== 'Custom').map(p => ({ value: p, label: text(S, `dest.preset.${p}`) }));

/**
 * The manual card under Advanced (spec §6.5): "Add chat by ID" and the detected chats. Only when the gate is unlocked.
 * These are actions (Add, Set up, Dismiss), not settings: nothing here auto-saves.
 * @param {object | null} dto the DestinationsDto
 * @param {Record<string, string>} S
 * @param {{ input?: string, preset?: string, result?: string, detectedPresets?: Record<string, string>, busy?: boolean }} ui
 *   result is HTML from the last add
 * @returns {string} HTML, or '' while locked
 */
export function renderManual(dto, S, ui) {
    if (!dto || !dto.ManualUnlocked) {
        return '';
    }

    const parts = [
        row({
            label: text(S, 'web.manual.label'),
            extraClass: 'tgj-row--stack',
            control: textField({ input: 'manualInput', value: ui.input ?? '', attrs: { autocomplete: 'off', 'aria-label': text(S, 'web.manual.label') } }),
        }),
        row({
            label: text(S, 'web.dest.preset'),
            control: selectField({ input: 'manualPreset', value: ui.preset ?? 'Everything', options: presetOptions(S), attrs: { 'aria-label': text(S, 'web.dest.preset') } }),
        }),
        `<div class="tgj-actions">${actionButton({ label: text(S, 'web.manual.add'), act: 'manualAdd', kind: 'primary' })}</div>`,
    ];
    if (ui.result) {
        parts.push(`<div class="tgj-manual-result">${ui.result}</div>`);
    }

    const detected = dto.DetectedChats ?? [];
    if (detected.length > 0) {
        parts.push(`<h3 class="tgj-subtitle">${t(S, 'detected.title')}</h3>`);
        for (const chat of detected) {
            const id = String(chat.ChatId);
            parts.push(row({
                label: text(S, 'detected.line', { title: chat.Title }),
                extraClass: 'tgj-detected',
                control: selectField({
                    input: 'detectedPreset',
                    value: (ui.detectedPresets ?? {})[id] ?? 'Everything',
                    options: presetOptions(S),
                    attrs: { 'data-tgj-id': id, 'aria-label': text(S, 'web.dest.preset') },
                })
                    + actionButton({ label: text(S, 'web.manual.setUp'), act: 'detectedSetUp', kind: 'primary', id })
                    + actionButton({ label: text(S, 'web.manual.dismiss'), act: 'detectedDismiss', id }),
            }));
        }
    }

    return card({ title: text(S, 'web.manual.title'), body: parts.join('') });
}

const MESSAGE_TOGGLES = ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks', 'AnnounceUpgrades'];
const UPDATE_MODES = ['SameMajor', 'AnyNewer'];

/** The batching fields with their allowed ranges (spec §10.1) and the strings that label and describe them. */
const BATCHING_FIELDS = [
    { key: 'QuietWindowSeconds', min: 5, max: 3600, label: 'web.batching.quiet.label', hint: 'web.batching.quiet.hint' },
    { key: 'MaxHoldMinutes', min: 1, max: 240, label: 'web.batching.maxHold.label', hint: 'web.batching.maxHold.hint' },
    { key: 'ReleaseQuietSeconds', min: 5, max: 600, label: 'web.batching.releaseQuiet.label', hint: 'web.batching.releaseQuiet.hint' },
    { key: 'DigestThreshold', min: 2, max: 100, label: 'web.batching.digest.label', hint: 'web.batching.digest.hint' },
    { key: 'LateEditHours', min: 0, max: 24, label: 'web.batching.lateEdit.label', hint: 'web.batching.lateEdit.hint' },
    { key: 'ReadyTimeoutMinutes', min: 1, max: 120, label: 'web.batching.readyTimeout.label', hint: 'web.batching.readyTimeout.hint' },
];

/**
 * The settings editor's data from a SettingsDto: the fields PUT Settings takes. Mode may arrive as a name or a number.
 * @param {object} settings
 */
export function settingsFormData(settings) {
    const mode = settings.UpdateCheck ? settings.UpdateCheck.Mode : undefined;
    return {
        PublicUrl: settings.PublicUrl ?? '',
        Message: { ...(settings.Message ?? {}) },
        Batching: { ...(settings.Batching ?? {}) },
        UpdateCheck: {
            Enabled: Boolean(settings.UpdateCheck && settings.UpdateCheck.Enabled),
            Mode: typeof mode === 'number' ? UPDATE_MODES[mode] ?? UPDATE_MODES[0] : mode ?? UPDATE_MODES[0],
        },
    };
}

/** The value at a dotted path ('Message.ShowPoster') of a nested object; undefined when a part is missing. */
function pathValue(obj, path) {
    return path.split('.').reduce((node, part) => (isPlainObject(node) ? node[part] : undefined), obj);
}

/** A copy of target with the value at each dotted path taken from source (both settings form data). */
function overlayPaths(target, source, paths) {
    const out = structuredClone(target);
    for (const path of paths) {
        const parts = path.split('.');
        let node = out;
        for (const part of parts.slice(0, -1)) {
            node = node[part] = isPlainObject(node[part]) ? node[part] : {};
        }

        node[parts.at(-1)] = structuredClone(pathValue(source, path));
    }

    return out;
}

/**
 * The PUT Settings body for an auto-save: the form's values at the given dotted paths (the field that changed) laid
 * over the saved ones (the form's base), so edits still waiting in other fields are not sent with it. Also the
 * client-side errors that stop it (a batching value that isn't a whole number); see batchingError for the ranges.
 * @param {{ base: object, local: object }} form
 * @param {string[]} paths e.g. ['Message.ShowPoster']
 * @param {number} revision the Revision the page last saw (ExpectedRevision)
 * @param {Record<string, string>} S
 * @returns {{ body: object | null, errors: { Field: string, Message: string }[] }} Message is plain text
 */
export function settingsRequest(form, paths, revision, S) {
    const data = overlayPaths(form.base, form.local, paths);
    const errors = BATCHING_FIELDS
        .filter(f => !Number.isInteger(data.Batching[f.key]))
        .map(f => ({ Field: `Batching.${f.key}`, Message: text(S, 'web.settings.notNumber') }));
    return errors.length > 0
        ? { body: null, errors }
        : {
            body: {
                ExpectedRevision: revision,
                PublicUrl: String(data.PublicUrl ?? '').trim(),
                Message: data.Message,
                Batching: data.Batching,
                UpdateCheck: data.UpdateCheck,
            },
            errors: [],
        };
}

/**
 * Whether a public URL can be used for the "Open in Jellyfin" buttons: a port of the server's PublicUrlRules.IsValid
 * (an absolute http or https URL whose host contains a dot and is not an IPv6 literal). Empty text is valid: it turns
 * the buttons off. The server saves any string, so the page checks before it saves (design §6.3).
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
        && labels.every(label => label !== '');
}

/**
 * Why a batching value can't be saved (plain text), or null: the server's rules (SettingsValidator) and its messages,
 * checked before the save. batching is the Batching object as it would be sent: this field's value with the others.
 * @param {{ key: string, min: number, max: number }} field one of BATCHING_FIELDS
 * @param {Record<string, unknown>} batching numbers, or the text typed where it isn't a whole number
 * @param {Record<string, string>} S
 * @returns {string | null}
 */
export function batchingError(field, batching, S) {
    const value = batching[field.key];
    if (!Number.isInteger(value)) {
        return text(S, 'web.settings.notNumber');
    }

    if (value < field.min || value > field.max) {
        return text(S, 'web.settings.range', { min: field.min, max: field.max });
    }

    const quiet = batching.QuietWindowSeconds;
    const hold = batching.MaxHoldMinutes;
    const pair = field.key === 'QuietWindowSeconds' || field.key === 'MaxHoldMinutes';
    return pair && Number.isInteger(quiet) && Number.isInteger(hold) && hold * 60 < quiet ? text(S, 'web.settings.maxHoldShort') : null;
}

/**
 * The Messages card (spec §6.6 item 4, §8.3, §8.4; design §6.1): the public URL, which saves when the field is left
 * or on Enter, and the six message switches, which save at once. Each row has the cue spot of its own save.
 * @param {object | null} settings the SettingsDto
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is state.forms.settings
 * @returns {string} HTML
 */
export function renderMessages(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local;
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

    for (const key of MESSAGE_TOGGLES) {
        const label = text(S, `settings.msg.${key}`);
        const path = `Message.${key}`;
        rows.push(row({
            label,
            cueKey: path,
            control: switchControl({
                input: 'msgToggle',
                checked: Boolean(local.Message && local.Message[key]),
                label,
                attrs: { 'data-tgj-key': key, 'data-tgj-field': path, 'data-tgj-save': path },
            }),
        }));
    }

    return card({ title: text(S, 'web.messages.title'), body: rows.join('') });
}

/**
 * The batching card under Advanced (spec §10.1): six whole-number fields, each with its range in the explanation.
 * Each saves 800 ms after the last keystroke, or when the field is left.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {object} ui as for renderMessages
 * @returns {string} HTML
 */
export function renderBatching(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const rows = BATCHING_FIELDS.map(f => {
        const path = `Batching.${f.key}`;
        return row({
            label: text(S, f.label),
            hint: text(S, f.hint, { min: f.min, max: f.max }),
            cueKey: path,
            control: numberField({
                input: 'batching',
                value: ui.form.local.Batching[f.key],
                min: f.min,
                max: f.max,
                attrs: { 'data-tgj-key': f.key, 'data-tgj-field': path, 'data-tgj-save': path, 'aria-label': text(S, f.label) },
            }),
        });
    });
    return card({ title: text(S, 'web.batching.title'), body: rows.join('') });
}

/**
 * The update-check card under Advanced (spec §7.6): the switch and the mode, each saved at once.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {object} ui as for renderMessages
 * @returns {string} HTML
 */
export function renderUpdates(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local.UpdateCheck;
    const enabled = text(S, 'settings.updates.enabled');
    const body = row({
        label: enabled,
        cueKey: 'UpdateCheck.Enabled',
        control: switchControl({
            input: 'updEnabled',
            checked: Boolean(local.Enabled),
            label: enabled,
            attrs: { 'data-tgj-field': 'UpdateCheck.Enabled', 'data-tgj-save': 'UpdateCheck.Enabled' },
        }),
    }) + row({
        label: text(S, 'web.updates.mode'),
        cueKey: 'UpdateCheck.Mode',
        control: selectField({
            input: 'updMode',
            value: local.Mode,
            options: UPDATE_MODES.map(mode => ({ value: mode, label: text(S, `settings.updates.mode.${mode}`) })),
            attrs: { 'data-tgj-field': 'UpdateCheck.Mode', 'data-tgj-save': 'UpdateCheck.Mode', 'aria-label': text(S, 'web.updates.mode') },
        }),
    });
    return card({ title: text(S, 'web.updates.title'), description: text(S, 'settings.updates.hint'), body });
}

const TEMPLATES_DOCS = 'https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md';

/** The template form data for one item (what a save sends besides the revision). */
export function templateFormData(item) {
    return { Text: String(item.Text ?? '') };
}

/** The auto-save and cue key of an event's template text. */
const templateSaveKey = key => `template:${key}`;

/** Tags of Telegram HTML that the preview shows as the same tag, without any attribute. */
const PREVIEW_SAME = new Set(['b', 'strong', 'i', 'em', 'u', 'ins', 's', 'strike', 'del', 'code', 'pre']);
/** Every tag the preview understands; any other tag is shown as text. */
const PREVIEW_KNOWN = new Set([...PREVIEW_SAME, 'tg-spoiler', 'span', 'blockquote', 'a', 'tg-emoji', 'tg-time']);
// A tag: optional /, the name, attributes (a name, maybe = and a quoted or bare value), >. Sticky: matched at lastIndex.
const PREVIEW_TAG = /<(\/?)([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[^\s=>/"']+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'<>`]+))?)*)\s*>/y;
const PREVIEW_ATTR = /([^\s=>/"']+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'<>`]+)))?/g;
const NAMED_ENTITIES = { lt: '<', gt: '>', amp: '&', quot: '"' };

/** Decodes the entities Telegram accepts (&lt; &gt; &amp; &quot; and numeric ones), once; anything else stays as it is. */
function decodeEntities(text) {
    return text.replace(/&(?:#(\d{1,7})|#[xX]([0-9a-fA-F]{1,6})|(lt|gt|amp|quot));/g, (whole, dec, hex, name) => {
        if (name) {
            return NAMED_ENTITIES[name];
        }

        const code = dec !== undefined ? Number(dec) : parseInt(hex, 16);
        return code > 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? String.fromCodePoint(code) : whole;
    });
}

/** An href the preview may link to: http:, https: or tg:, else null. */
function safeHref(value) {
    const href = String(value ?? '').trim();
    return /^(?:https?|tg):/i.test(href) ? href : null;
}

/** Splits Telegram HTML into tokens: text (decoded), and open and close tags with their attributes and raw source. */
function previewTokens(source) {
    const tokens = [];
    let text = '';
    let i = 0;
    while (i < source.length) {
        const lt = source.indexOf('<', i);
        if (lt < 0) {
            text += source.slice(i);
            break;
        }

        text += source.slice(i, lt);
        PREVIEW_TAG.lastIndex = lt;
        const match = PREVIEW_TAG.exec(source);
        if (!match) {
            text += '<';
            i = lt + 1;
            continue;
        }

        if (text) {
            tokens.push({ type: 'text', value: decodeEntities(text) });
            text = '';
        }

        const attrs = {};
        for (const attr of match[3].matchAll(PREVIEW_ATTR)) {
            const name = attr[1].toLowerCase();
            if (!Object.hasOwn(attrs, name)) {
                attrs[name] = decodeEntities(attr[2] ?? attr[3] ?? attr[4] ?? '');
            }
        }

        tokens.push({ type: match[1] ? 'close' : 'open', name: match[2].toLowerCase(), attrs, raw: match[0] });
        i = lt + match[0].length;
    }

    if (text) {
        tokens.push({ type: 'text', value: decodeEntities(text) });
    }

    return tokens;
}

/** A node list as HTML: text escaped, markup (already safe) as it is. */
const nodesHtml = nodes => nodes.map(n => (n.html !== undefined ? n.html : esc(n.text))).join('');

/** An expandable quote: the first line is the summary, the rest the body. */
function expandableQuote(children) {
    const index = children.findIndex(n => n.text !== undefined && n.text.includes('\n'));
    let summary = children;
    let body = [];
    if (index >= 0) {
        const cut = children[index].text.indexOf('\n');
        summary = [...children.slice(0, index), { text: children[index].text.slice(0, cut) }];
        body = [{ text: children[index].text.slice(cut + 1) }, ...children.slice(index + 1)];
    }

    return `<details class="tgj-tg-quote"><summary>${nodesHtml(summary)}</summary>${nodesHtml(body)}</details>`;
}

/** The safe preview markup for one closed Telegram element. */
function previewElement(name, attrs, children) {
    const inner = nodesHtml(children);
    if (PREVIEW_SAME.has(name)) {
        return `<${name}>${inner}</${name}>`;
    }

    switch (name) {
        case 'tg-spoiler':
            return `<span class="tgj-tg-spoiler">${inner}</span>`;
        case 'span':
            return String(attrs.class ?? '').trim() === 'tg-spoiler' ? `<span class="tgj-tg-spoiler">${inner}</span>` : inner;
        case 'blockquote':
            return Object.hasOwn(attrs, 'expandable') ? expandableQuote(children) : `<blockquote>${inner}</blockquote>`;
        case 'a': {
            const href = safeHref(attrs.href);
            return href === null ? inner : `<a target="_blank" rel="noopener noreferrer" href="${esc(href)}">${inner}</a>`;
        }
        default:
            // tg-emoji and tg-time show their fallback text.
            return inner;
    }
}

/**
 * Turns Telegram HTML (from the server's Validate) into markup that is safe to put into the page. It tokenizes the
 * input and re-emits only Telegram's formatting tags, with no attribute from the input except a checked link href:
 * b, strong, i, em, u, ins, s, strike, del, code and pre as themselves; tg-spoiler and span class="tg-spoiler" as a
 * span.tgj-tg-spoiler; blockquote (an expandable one as details/summary); a as a new-tab link for http:, https: and tg:
 * hrefs only, otherwise its text; tg-emoji and tg-time as their text. An unknown, unclosed or stray tag is shown as text.
 * Entities are decoded once, and all text is escaped on output.
 * @param {string} telegramHtml
 * @returns {string} HTML
 */
export function previewHtml(telegramHtml) {
    // Each open element: its tag, attributes, raw source and children; the root holds the output.
    const root = { children: [] };
    const stack = [root];
    const top = () => stack.at(-1);
    // An element that never closed: its tag's source as text, then its children.
    const unwrap = frame => [{ text: frame.raw }, ...frame.children];

    for (const token of previewTokens(String(telegramHtml ?? ''))) {
        if (token.type === 'text') {
            top().children.push({ text: token.value });
        } else if (token.type === 'open' && PREVIEW_KNOWN.has(token.name)) {
            stack.push({ name: token.name, attrs: token.attrs, raw: token.raw, children: [] });
        } else if (token.type === 'close' && stack.some((f, i) => i > 0 && f.name === token.name)) {
            while (top().name !== token.name) {
                const open = stack.pop();
                top().children.push(...unwrap(open));
            }

            const frame = stack.pop();
            top().children.push({ html: previewElement(frame.name, frame.attrs, frame.children) });
        } else {
            top().children.push({ text: token.raw });
        }
    }

    while (stack.length > 1) {
        const open = stack.pop();
        top().children.push(...unwrap(open));
    }

    return nodesHtml(root.children);
}

/** The message's inline buttons: one row each, a link only for an http:, https: or tg: URL. */
function previewButtons(rows) {
    const html = (rows ?? []).filter(row => Array.isArray(row) && row.length > 0).map(row => `<div class="tgj-tg-row">${row.map(b => {
        const href = safeHref(b && b.Url);
        const label = esc(b && b.Text);
        return href === null
            ? `<span class="tgj-tg-button">${label}</span>`
            : `<a class="tgj-tg-button" target="_blank" rel="noopener noreferrer" href="${esc(href)}">${label}</a>`;
    }).join('')}</div>`);
    return html.length > 0 ? `<div class="tgj-tg-buttons">${html.join('')}</div>` : '';
}

/**
 * The template preview pane: the validation's errors and warnings, then the message as Telegram would show it (poster
 * placeholder, text, buttons) and its length. With errors, the last good preview is shown dimmed instead.
 * @param {object | null} result the last TemplateValidationDto; RequestError (HTML) when the request itself failed
 * @param {Record<string, string>} S
 * @param {string} [lang]
 * @param {object | null} [lastGood] the last validation of this template without errors
 * @returns {string} HTML
 */
export function renderTemplatePreview(result, S, lang = 'en', lastGood = null) {
    if (!result) {
        return '';
    }

    const parts = [];
    if (result.RequestError) {
        parts.push(`<p class="tgj-error">${result.RequestError}</p>`);
    }

    for (const e of result.Errors ?? []) {
        parts.push(`<p class="tgj-error">${e.Line
            ? t(S, 'web.templates.errorAt', { line: e.Line, column: e.Column ?? 0, message: e.Message })
            : esc(e.Message)}</p>`);
    }

    for (const w of result.Warnings ?? []) {
        parts.push(`<p class="tgj-tpl-warning">${esc(w)}</p>`);
    }

    const failed = Boolean(result.RequestError) || (result.Errors ?? []).length > 0;
    const shown = failed ? lastGood : result;
    if (shown) {
        parts.push(`<div class="tgj-tg-bubble${failed ? ' tgj-tg-stale' : ''}">`
            + (shown.Poster ? `<div class="tgj-tg-poster">${t(S, 'web.templates.poster')}</div>` : '')
            + `<div class="tgj-tg-text">${previewHtml(shown.Html)}</div>${previewButtons(shown.Buttons)}</div>`);
    }

    if (!result.RequestError && (!failed || result.Length > 0)) {
        parts.push(`<p class="tgj-row-hint tgj-tpl-length">${t(S, 'web.templates.length', { length: formatNumber(result.Length ?? 0, lang), limit: formatNumber(result.Limit ?? 0, lang) })}</p>`);
    }

    return parts.join('');
}

/** Whether a validation result stops a test send: it had errors, or the request failed. */
const validationFailed = result => Boolean(result && (result.RequestError || (result.Errors ?? []).length > 0));

/** Whether Send test is off: no destination, the last validation failed, or a send or a reset is in flight. */
export function templateTestBlocked(ui) {
    return (ui.destinations ?? []).length === 0 || validationFailed(ui.result) || Boolean(ui.testing) || Boolean(ui.busy);
}

/**
 * The Message templates section (spec §8.5): every message comes from a template, and this edits the one in use per
 * event, with a live Telegram-like preview, a test send and a reset to the built-in template. The text saves itself
 * (design §6.1): its cue sits beside the textarea's label.
 * @param {object | null} dto the TemplatesDto
 * @param {Record<string, string>} S
 * @param {{ key?: string, forms?: Record<string, { local: object }>, result?: object | null,
 *   good?: object | null, busy?: boolean, lang?: string, destinations?: { Id: string, Title: string }[],
 *   testTo?: string, testing?: boolean, testResult?: string, resetAsk?: boolean }} ui key is the selected event;
 *   result is its last validation, good the last one without errors; testResult is HTML
 * @returns {string} HTML
 */
export function renderTemplates(dto, S, ui) {
    if (!dto || !Array.isArray(dto.Items) || dto.Items.length === 0) {
        return '';
    }

    const lang = ui.lang ?? 'en';
    const item = dto.Items.find(i => i.EventKey === ui.key) ?? dto.Items[0];
    const form = (ui.forms ?? {})[`tpl:${item.EventKey}`];
    const local = form ? form.local : templateFormData(item);
    const saveKey = templateSaveKey(item.EventKey);
    const eventLabel = text(S, 'web.templates.event');
    const options = dto.Items.map(i => ({ value: i.EventKey, label: i.Edited ? text(S, 'web.templates.eventEdited', { label: i.Label }) : i.Label }));
    const badge = item.Edited ? label(text(S, 'web.templates.edited'), 'accent') : label(text(S, 'web.templates.builtIn'), 'neutral');
    const parts = [row({
        label: eventLabel,
        control: badge + selectField({ input: 'tplEvent', value: item.EventKey, options, attrs: { 'aria-label': eventLabel } }),
        extraClass: 'tgj-tpl-event',
    })];
    if (item.Flag) {
        // A truncated template was sent, only shortened; any other flag means the built-in template was sent instead.
        parts.push(item.Flag.Kind === 'truncated'
            ? notice({ kind: 'warning', html: t(S, 'web.templates.flagTruncated') })
            : notice({ kind: 'error', html: `${t(S, 'web.templates.flagError')} ${esc(item.Flag.Error)}` }));
    }

    // The textarea is told apart by its event key, not an id: a refresh keeps a pending text only in its own template.
    const rows = (item.Fields ?? []).map(f => `<tr><td><code>${esc(f.Name)}</code></td><td>${esc(f.Type)}</td>`
        + `<td>${f.Optional ? t(S, 'web.templates.optional') : ''}</td><td>${esc(f.Description)}</td></tr>`);
    parts.push('<div class="tgj-tpl-grid"><div class="tgj-tpl-edit">'
        + `<div class="tgj-tpl-label"><span class="tgj-row-label" id="tgj-tpl-label">${t(S, 'web.templates.text')}</span>`
        + `<span class="tgj-cue" data-tgj-cue="${esc(saveKey)}" aria-live="polite"></span></div>`
        + `<textarea class="tgj-field tgj-template-text" data-tgj-input="tplText" data-tgj-key="${esc(item.EventKey)}" data-tgj-save="${esc(saveKey)}"`
        + ` aria-labelledby="tgj-tpl-label" rows="12" spellcheck="false">${esc(local.Text)}</textarea>`
        + `<details class="tgj-tpl-fields"><summary>${t(S, 'web.templates.fields')}</summary><table class="tgj-template-fields">${rows.join('')}</table></details>`
        + `<p class="tgj-tpl-docs"><a class="tgj-link" href="${TEMPLATES_DOCS}" target="_blank" rel="noopener noreferrer">${t(S, 'web.templates.docs')}</a></p></div>`
        + `<div id="tgj-tpl-preview" class="tgj-tpl-preview">${renderTemplatePreview(ui.result ?? null, S, lang, ui.good ?? null)}</div></div>`);

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
 * The diagnostics block under Advanced (spec §12): Copy diagnostics, and the text itself when the clipboard refused it.
 * @param {Record<string, string>} S
 * @param {{ text?: string, copied?: boolean }} ui
 * @returns {string} HTML
 */
export function renderDiagnostics(S, ui) {
    const parts = [`<div class="tgj-actions">${actionButton({ label: text(S, 'web.diag.copy'), act: 'copyDiagnostics' })}</div>`];
    if (ui.copied) {
        parts.push(`<p class="tgj-row-hint">${t(S, 'web.diag.copied')}</p>`);
    }

    if (ui.text) {
        parts.push(`<p class="tgj-row-hint">${t(S, 'web.diag.manual')}</p>`
            + `<textarea id="tgj-diagnostics-text" class="tgj-field tgj-diag-text" readonly rows="12">${esc(ui.text)}</textarea>`);
    }

    return card({ title: text(S, 'web.diag.title'), body: parts.join('') });
}

// --- the shell (plan 8, design §5.1-5.2): header, sidebar, one section shown at a time ---

/** The dashboard's sections, in sidebar order; each label is web.nav.<name>. */
export const SECTIONS = ['overview', 'destinations', 'messages', 'templates', 'advanced'];
const NAV_LABELS = {
    overview: 'web.nav.overview',
    destinations: 'web.nav.destinations',
    messages: 'web.nav.messages',
    templates: 'web.nav.templates',
    advanced: 'web.nav.advanced',
};

/** Today's section containers, by the dashboard section that holds them (design §5.2). */
const SECTION_HOSTS = {
    // Connect precedes Status: with no bot it leads (design §5.2), and with a bot it is empty, so Status leads.
    overview: ['tgj-connect', 'tgj-status', 'tgj-bot'],
    destinations: ['tgj-destinations'],
    messages: ['tgj-messages'],
    templates: ['tgj-templates'],
    advanced: ['tgj-proxy', 'tgj-batching', 'tgj-updates', 'tgj-manual', 'tgj-diagnostics'],
};

/** How long the first show waits for tgjelly.css before showing the page unstyled. */
const STYLESHEET_WAIT_MS = 1000;

/** The header's connection label: a label's HTML, a word in an outline and no symbol. */
const connectionHtml = connection => label(connection.text, connection.tone);

/** The header's version label, or '' before the version is known. */
const versionHtml = version => (version ? label(`v${version}`, 'neutral') : '');

/**
 * The header's connection label for a Status: "Connected · @bot", "Not connected", "Connecting", or a warning when the
 * connection needs attention or the status can't be read.
 * @param {object | null} status the last GET Status body
 * @param {Record<string, string> | null} S
 * @param {boolean} [failed] the last GET Status failed
 * @returns {{ tone: 'good' | 'progress' | 'neutral' | 'attention', text: string }} plain text, for label
 */
export function connectionLabel(status, S, failed = false) {
    if (failed || !status) {
        return failed ? { tone: 'attention', text: text(S, 'web.header.unknown') } : { tone: 'neutral', text: text(S, 'web.header.notConnected') };
    }

    if (!status.HasToken) {
        return { tone: 'neutral', text: text(S, 'web.header.notConnected') };
    }

    if (PROBLEM_STATES.has(status.ConnectionState) || status.Compatible === false) {
        return { tone: 'attention', text: text(S, 'web.header.attention') };
    }

    if (status.ConnectionState === 'Connected') {
        return { tone: 'good', text: status.BotUsername ? text(S, 'web.header.connected', { bot: status.BotUsername }) : text(S, 'web.header.connectedPlain') };
    }

    return { tone: 'progress', text: text(S, 'web.header.connecting') };
}

/**
 * Renders the dashboard shell: the header with the connection label, the sidebar of sections with their muted counts,
 * and one section per name holding today's containers. Only the active section is visible. The page fills the
 * containers, the label (#tgj-conn) and the counts (#tgj-nav-count-<name>) afterwards; this is written once.
 * @param {Record<string, string> | null} S
 * @param {{ active: string, connection: { tone: string, text: string }, counts?: Record<string, string>, version?: string }} options
 *   version is the plugin's, painted beside the title; empty until the first status arrives
 *   counts are plain text by section name, e.g. { destinations: '3', templates: '1 edited' }
 * @returns {string}
 */
export function renderShell(S, { active, connection, counts = {}, version = '' }) {
    const current = SECTIONS.includes(active) ? active : 'overview';
    const nav = SECTIONS.map(name => `<button type="button" id="tgj-nav-${name}" data-tgj-nav="${name}"`
        + `${name === current ? ' aria-current="page"' : ''}>`
        + `<span class="tgj-nav-label">${t(S, NAV_LABELS[name])}</span>`
        + `<span id="tgj-nav-count-${name}" class="tgj-nav-count">${esc(counts[name] ?? '')}</span></button>`);
    const sections = SECTIONS.map(name => `<section id="tgj-sec-${name}" data-tgj-section="${name}" aria-labelledby="tgj-nav-${name}"`
        + `${name === current ? '' : ' hidden'}>${SECTION_HOSTS[name].map(id => `<div id="${id}"></div>`).join('')}</section>`);
    return '<div class="tgj-shell">'
        + `<header class="tgj-header"><div class="tgj-header-brand"><h1 class="tgj-header-title">TgJelly</h1><span id="tgj-version">${versionHtml(version)}</span></div><span id="tgj-conn">${connectionHtml(connection)}</span></header>`
        + `<nav class="tgj-sidebar" aria-label="${t(S, 'web.nav.label')}">${nav.join('')}</nav>`
        + `<div class="tgj-content">${sections.join('')}</div>`
        + '</div>';
}

/** The tgj=<section> parameter in the URL hash. */
const HASH_SECTION = /([#?&])tgj=([^&]*)/;

/**
 * The section a URL hash names with tgj=<section>, or 'overview'. Jellyfin routes on the hash
 * (#/configurationpage?name=TgJelly), so the parameter sits beside the route's own.
 * @param {string | undefined} hash
 * @returns {string}
 */
export function sectionFromHash(hash) {
    const match = HASH_SECTION.exec(String(hash ?? ''));
    let name = '';
    try {
        name = match ? decodeURIComponent(match[2]) : '';
    } catch {
        name = '';
    }

    return SECTIONS.includes(name) ? name : 'overview';
}

/**
 * The hash with tgj=<name> set: replaced where it is, else added to the route's query, else the whole hash.
 * @param {string} hash
 * @param {string} name a SECTIONS name
 * @returns {string}
 */
export function hashWithSection(hash, name) {
    const current = String(hash ?? '');
    if (HASH_SECTION.test(current)) {
        return current.replace(HASH_SECTION, `$1tgj=${name}`);
    }

    if (current.startsWith('#/')) {
        return `${current}${current.includes('?') ? '&' : '?'}tgj=${name}`;
    }

    return current === '' || current === '#' ? `#tgj=${name}` : `${current}&tgj=${name}`;
}

/** The attributes that tell the page's fields apart (an id alone is enough). */
const FIELD_ATTRS = ['data-tgj-input', 'data-tgj-id', 'data-tgj-key', 'data-tgj-field', 'data-tgj-lib', 'data-tgj-group'];
const FIELD_TAGS = new Set(['INPUT', 'TEXTAREA', 'SELECT']);
// Input types whose value is typed text: their typed value outlives a render. (A checkbox or a select is a choice made
// at once, so a render shows the state it led to.)
const TYPED_INPUTS = new Set(['text', 'url', 'password', 'search', 'email', 'tel', 'number']);

const selectorValue = value => String(value).replace(/["\\]/g, c => `\\${c}`);

/**
 * A selector that finds this field again after its section is written: [id="…"], or its data-tgj-* attributes.
 * null when the field has none of them. It is also the key of state.pendingFields.
 * @param {{ getAttribute(name: string): string | null }} el
 * @returns {string | null}
 */
export function fieldSelector(el) {
    const id = el.getAttribute('id');
    if (id) {
        return `[id="${selectorValue(id)}"]`;
    }

    const parts = FIELD_ATTRS.map(name => [name, el.getAttribute(name)]).filter(([, value]) => value !== null && value !== '')
        .map(([name, value]) => `[${name}="${selectorValue(value)}"]`);
    return parts.length > 0 ? parts.join('') : null;
}

/** An input, textarea or select, or a switch (a button with role=switch): what a section write must not take away. */
const isField = el => FIELD_TAGS.has(String(el.tagName).toUpperCase())
    || (typeof el.getAttribute === 'function' && el.getAttribute('role') === 'switch');

const isTyped = el => el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && TYPED_INPUTS.has(String(el.type || 'text').toLowerCase()));

/**
 * Runs a write that may replace fields inside rootEl, so that typing is never interrupted (design §4, generalising
 * v0.3's template caret fix):
 * - a focused input, textarea, select or switch inside rootEl is focused again in its new copy, with the same selection and
 *   scroll; a typed field keeps what the admin typed since the last render (its value differs from its defaultValue);
 * - a field in pending (selectors from fieldSelector, e.g. a change whose auto-save hasn't gone out) keeps the value it
 *   had before the write, and does not take the focus.
 * A write that leaves a field in place (an unchanged section) changes nothing.
 * @param {Element | null} rootEl
 * @param {() => void} write
 * @param {{ pending?: Iterable<string> }} [options]
 */
export function keepFocus(rootEl, write, { pending = [] } = {}) {
    if (!rootEl || typeof rootEl.querySelector !== 'function') {
        write();
        return;
    }

    const doc = rootEl.ownerDocument || globalThis.document;
    const active = doc ? doc.activeElement : null;
    const kept = new Map();
    if (active && isField(active) && typeof rootEl.contains === 'function' && rootEl.contains(active)) {
        const key = fieldSelector(active);
        if (key) {
            kept.set(key, {
                el: active,
                focus: true,
                value: isTyped(active) && active.value !== active.defaultValue ? active.value : undefined,
                start: active.selectionStart,
                end: active.selectionEnd,
                direction: active.selectionDirection || 'none',
                top: active.scrollTop,
                left: active.scrollLeft,
            });
        }
    }

    for (const key of pending) {
        const el = rootEl.querySelector(key);
        if (el) {
            const entry = kept.get(key) ?? { el, focus: false };
            kept.set(key, { ...entry, value: el.value, checked: el.checked });
        }
    }

    write();
    for (const [key, saved] of kept) {
        const after = rootEl.querySelector(key);
        if (!after || after === saved.el) {
            continue;
        }

        if (saved.value !== undefined && after.value !== saved.value) {
            after.value = saved.value;
        }

        if (typeof saved.checked === 'boolean' && after.checked !== saved.checked) {
            after.checked = saved.checked;
        }

        if (!saved.focus) {
            continue;
        }

        if (typeof after.focus === 'function') {
            after.focus({ preventScroll: true });
        }

        if (typeof after.setSelectionRange === 'function' && typeof saved.start === 'number' && typeof saved.end === 'number') {
            try {
                after.setSelectionRange(saved.start, saved.end, saved.direction);
            } catch {
                // This input type has no selection (e.g. number): the focus is enough.
            }
        }

        after.scrollTop = saved.top;
        after.scrollLeft = saved.left;
    }
}

/**
 * Resolves once the stylesheet <link> has loaded or failed, or after ms. At once when there is no link (as in the
 * tests) or it has loaded already. The first show waits for it, so the page doesn't paint unstyled and then play every
 * transition from the browser's defaults.
 * @param {(EventTarget & { sheet?: unknown }) | null} link
 * @param {number} [ms]
 * @returns {Promise<void>}
 */
export function waitForStylesheet(link, ms = STYLESHEET_WAIT_MS) {
    if (!link || typeof link.addEventListener !== 'function' || link.sheet) {
        return Promise.resolve();
    }

    return new Promise(resolve => {
        let timer = null;
        const done = () => {
            clearTimeout(timer);
            link.removeEventListener('load', done);
            link.removeEventListener('error', done);
            resolve();
        };
        link.addEventListener('load', done);
        link.addEventListener('error', done);
        timer = setTimeout(done, ms);
    });
}

/**
 * The page controller (spec §11.1). Listeners are bound once, here; viewshow is idempotent.
 *
 * Extension points for the page sections (Tasks 10-12 add their entries in the marked places below):
 * - sections: name → { id, load, render }. load() fetches into state (forms through syncForm); render() returns the
 *   section's HTML from state. The framework renders each section into #id with its own error line appended.
 * - actions: data-tgj-act → (el, event), for clicks. Wrap the work in runAction(section, fn) so a failure shows in
 *   that section.
 * - inputs: data-tgj-input → (el, event), for both input and change events (check event.type when it matters).
 * Returns these, so tests can register sections; Jellyfin ignores the return value.
 * @param {HTMLElement} view
 * @param {object} params
 */
export default function TgJellyPage(view, params) {
    const api = createApi(window.ApiClient);
    const state = {
        strings: null,
        status: null,
        settings: null,
        destinations: null,
        libraries: null,
        links: null,
        templates: null,
        // Editor forms by name: { base, local, dirty, changed } (see syncFormData).
        forms: {},
        // errors: section name → HTML (escaped) shown under that section; statusFailed: the last GET Status failed.
        ui: {
            errors: {},
            statusFailed: false,
            // The connect card (§6.1): mode is null | 'new' | 'existing'; result is the last save's outcome.
            mode: null,
            changingToken: false,
            saving: false,
            setProfile: true,
            result: null,
            unreachable: false,
            confirmDisconnect: false,
            // The destinations section (§6.6.3): the open editor, the remove confirm, and the last test/check outcome per id;
            // destPresetAsk is { id, preset } while leaving Custom for that preset waits for Replace (design §6.1a).
            destEditing: null,
            destConfirm: null,
            destLeave: false,
            destResults: {},
            destPresetAsk: null,
            // The manual block (§6.5): the typed chat ID or link, the presets, and the last add's outcome (HTML).
            manualInput: '',
            manualPreset: 'Everything',
            manualResult: '',
            detectedPresets: {},
            // Diagnostics: the text when the clipboard refused it, and whether the last copy worked.
            diagText: '',
            diagCopied: false,
            // The templates section: the selected event key, the last validation and the last one without errors per
            // key, a reset in flight, the test send's destination, in-flight flag and outcome (HTML), and the
            // open reset question.
            tplKey: '',
            tplResults: {},
            tplGood: {},
            tplBusy: false,
            tplTestTo: '',
            tplTesting: false,
            tplTestResult: '',
            tplResetAsk: false,
        },
    };
    const lang = () => document.documentElement.lang || 'en';
    const byId = id => view.querySelector('#' + id);
    const rendered = new Map();
    const inFlight = new Map();
    let visible = false;
    let timer = null;
    let stringsLoad = null;
    // The token lives only here until it is sent: never in state, never rendered back into the page.
    const typed = { token: '', proxyUsername: undefined, proxyPassword: undefined };
    // Section load errors (set by reloadSection), so a later good load can clear them without touching action errors.
    const loadErrors = new Set();
    // The template preview, per event key: the newest Validate request's number (older replies are ignored) and the
    // text its kept result was for (a save checks exactly the text it sends); and the typing debounce.
    const tplSeqs = new Map();
    const tplChecked = new Map();
    let tplTimer = null;
    // Fields whose change hasn't been saved yet, by fieldSelector: a section write keeps their values (keepFocus).
    const pendingFields = new Set();
    // How many section writes are running (see renderSection and the focusout listener).
    let writing = 0;
    // The shell: written once, at the first render after the strings and the stylesheet; then the shown section.
    let shellDrawn = false;
    let activeSection = 'overview';
    let stylesheet = null;

    /**
     * Sets a container's HTML unless it's the HTML last written there. It compares with its own copy, not with
     * el.innerHTML, which the browser serializes differently (&#39; reads back as '), so an unchanged poll doesn't
     * replace a button the user is about to click.
     */
    const setHtml = (id, html) => {
        const el = byId(id);
        if (el && rendered.get(id) !== html) {
            el.innerHTML = html;
            rendered.set(id, html);
        }
    };

    /** The error line for a section, or ''. */
    const sectionError = name => (state.ui.errors[name] ? `<p class="tgj-error">${state.ui.errors[name]}</p>` : '');

    /** The connect card's view of the state: the ui flags, whether a token is stored, and the proxy form's ui. */
    const connectUi = () => ({ ...state.ui, hasToken: Boolean(state.status && state.status.HasToken), proxy: proxyUi() });
    const proxyUi = () => ({ form: state.forms.proxy });

    // The one Revision every save sends (design §6.4: settings, destinations and templates share it). It is the newest
    // Revision of any data the page took in: a section load, a save's reply or a 409's Current. So a save queued behind
    // another one sends the Revision that save moved to, not its own section's older copy, which would 409 on the page's
    // own change. The Status poll doesn't move it: a Revision the page holds no data for must still conflict.
    let knownRevision = 0;
    const noteRevision = dto => {
        if (dto && typeof dto.Revision === 'number' && dto.Revision > knownRevision) {
            knownRevision = dto.Revision;
        }
    };
    /** The Revision to send as ExpectedRevision with any save. */
    const pageRevision = () => knownRevision;

    // A section's reply older than the data it holds (a reload sent before a save and answered after it) is dropped:
    // taking it would show the old values, and the next save would lay its change over them and revert the newer one.
    const isOlder = (held, dto) => Boolean(held) && typeof dto.Revision === 'number' && dto.Revision < held.Revision;

    /** Takes a SettingsDto into the shared settings state and its editor forms (keeping unsaved edits). */
    const takeSettings = dto => {
        if (isOlder(state.settings, dto)) {
            return;
        }

        noteRevision(dto);
        state.settings = dto;
        syncForm('proxy', proxyFormData(dto));
        syncForm('settings', settingsFormData(dto));
    };

    /** The settings sections' views of the state. */
    const settingsUi = () => ({ form: state.forms.settings });

    // Every section that edits the settings model loads it together: one request while it runs.
    let settingsLoad = null;
    /** Loads GET Settings, the one settings model the proxy, messages, batching and update forms share. */
    const loadSettings = () => {
        settingsLoad ??= api.get('Settings').then(result => {
            if (result.status !== 200 || !result.body) {
                throw new Error('HTTP ' + result.status);
            }

            takeSettings(result.body);
        }).finally(() => {
            settingsLoad = null;
        });
        return settingsLoad;
    };

    // Sections that need the links share one request while it runs.
    let linksLoad = null;
    const loadLinks = () => {
        linksLoad ??= api.get('Links').then(result => {
            if (result.status !== 200 || !result.body) {
                throw new Error('HTTP ' + result.status);
            }

            state.links = result.body;
        }).finally(() => {
            linksLoad = null;
        });
        return linksLoad;
    };

    /** Takes a DestinationsDto into the state and the per-destination editor forms (keeping unsaved edits). */
    const takeDestinations = dto => {
        if (isOlder(state.destinations, dto)) {
            return;
        }

        noteRevision(dto);
        state.destinations = dto;
        const live = new Set(dto.Items.map(d => `dest:${d.Id}`));
        for (const key of Object.keys(state.forms).filter(k => k.startsWith('dest:') && !live.has(k))) {
            delete state.forms[key];
        }

        for (const dest of dto.Items) {
            syncForm(`dest:${dest.Id}`, destinationFormData(dest));
        }

        if (!dto.Items.some(d => d.Id === state.ui.destEditing)) {
            state.ui.destEditing = null;
        }

        if (!dto.Items.some(d => d.Id === state.ui.destConfirm)) {
            state.ui.destConfirm = null;
        }

        if (state.ui.destPresetAsk && !dto.Items.some(d => d.Id === state.ui.destPresetAsk.id)) {
            state.ui.destPresetAsk = null;
        }
    };

    /**
     * Takes a destination's PUT reply (a DestinationDto) in: the item, and the Revision the change moved to, which every
     * section's next save sends. The list takes that Revision only when the save was based on the list's own one;
     * otherwise the list was behind, and it reloads to catch up (keeping unsaved edits).
     */
    const takeDestination = dto => {
        noteRevision(dto);
        const list = state.destinations;
        if (!list || !list.Items.some(d => d.Id === dto.Id)) {
            return;
        }

        const current = typeof dto.Revision === 'number' && list.Revision === dto.Revision - 1;
        state.destinations = { ...list, Revision: current ? dto.Revision : list.Revision, Items: list.Items.map(d => (d.Id === dto.Id ? dto : d)) };
        if (!current) {
            reloadSection('destinations', { fresh: true });
        }
    };

    async function loadDestinations() {
        const [list, libraries] = await Promise.all([api.get('Destinations'), api.get('Libraries'), loadLinks()]);
        if (list.status !== 200 || !list.body || libraries.status !== 200 || !Array.isArray(libraries.body)) {
            throw new Error('HTTP ' + (list.status === 200 ? libraries.status : list.status));
        }

        state.libraries = libraries.body;
        takeDestinations(list.body);
    }

    /** Takes a TemplatesDto into the state and one editor form per event key (keeping unsaved edits). */
    const takeTemplates = dto => {
        if (isOlder(state.templates, dto)) {
            return;
        }

        noteRevision(dto);
        state.templates = dto;
        const live = new Set(dto.Items.map(i => `tpl:${i.EventKey}`));
        for (const key of Object.keys(state.forms).filter(k => k.startsWith('tpl:') && !live.has(k))) {
            delete state.forms[key];
        }

        for (const item of dto.Items) {
            syncForm(`tpl:${item.EventKey}`, templateFormData(item));
        }
    };

    /** The selected event key: the chosen one while it exists, else the first. */
    const templateKey = () => {
        const items = state.templates ? state.templates.Items : [];
        return items.some(i => i.EventKey === state.ui.tplKey) ? state.ui.tplKey : (items[0] ? items[0].EventKey : '');
    };

    /** The destination a test send goes to: the chosen one while it exists, else the first ('' when there is none). */
    const templateTestTarget = () => {
        const items = state.destinations ? state.destinations.Items : [];
        return items.some(d => d.Id === state.ui.tplTestTo) ? state.ui.tplTestTo : (items[0] ? items[0].Id : '');
    };

    /** The templates section's view of the state. */
    const templatesUi = () => {
        const key = templateKey();
        return {
            key,
            forms: state.forms,
            result: state.ui.tplResults[key] ?? null,
            good: state.ui.tplGood[key] ?? null,
            busy: state.ui.tplBusy,
            lang: lang(),
            destinations: state.destinations ? state.destinations.Items : [],
            testTo: templateTestTarget(),
            testing: state.ui.tplTesting,
            testResult: state.ui.tplTestResult,
            resetAsk: state.ui.tplResetAsk,
        };
    };

    /**
     * Shows the selected template's last validation in the preview pane, and turns Send test on or off to match. Only
     * the pane is replaced: the textarea keeps its focus and caret.
     */
    const showTemplatePreview = () => {
        const ui = templatesUi();
        const pane = byId('tgj-tpl-preview');
        if (pane) {
            pane.innerHTML = renderTemplatePreview(ui.result, state.strings, ui.lang, ui.good);
            // The section on the page no longer matches the copy setHtml keeps, so its next render must write.
            rendered.delete('tgj-templates');
        }

        const test = byId('tgj-tpl-test');
        if (test) {
            test.disabled = templateTestBlocked(ui);
        }
    };

    /**
     * Validates an event's current text (default: the selected event's) and keeps the result for the preview and for the
     * save's check. Every request gets the key's next number; a reply that is not for the newest request is not kept,
     * so a slow older reply can't overwrite a newer one.
     * @returns {Promise<{ text: string, reply: object, failure: string | null } | null>} the text checked, the reply (or
     *   { RequestError } HTML when the request failed) and then the plain-text reason; null without that template
     */
    const validateTemplate = async (key = templateKey()) => {
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
                reply = { RequestError: requestError(S, result) };
                failure = failureText(result);
            }
        } catch (err) {
            reply = { RequestError: t(S, 'web.requestFailed', { reason: err && err.message }) };
            failure = text(S, 'web.requestFailed', { reason: err && err.message });
        }

        if (seq === tplSeqs.get(key)) {
            state.ui.tplResults[key] = reply;
            tplChecked.set(key, value);
            if (!validationFailed(reply)) {
                state.ui.tplGood[key] = reply;
            }

            if (key === templateKey()) {
                showTemplatePreview();
            }
        }

        return { text: value, reply, failure };
    };

    /** Drops a pending debounced validation. */
    const cancelTemplateTimer = () => {
        if (tplTimer !== null) {
            clearTimeout(tplTimer);
            tplTimer = null;
        }
    };

    const sections = {
        status: {
            id: 'tgj-status',
            load: async () => {
                let failure = null;
                try {
                    const result = await api.get('Status');
                    if (result.status === 200 && result.body) {
                        state.status = result.body;
                    } else {
                        failure = 'HTTP ' + result.status;
                    }
                } catch (err) {
                    failure = err;
                }

                if (failure && !state.ui.statusFailed) {
                    console.error('TgJelly: could not load the status', failure);
                }

                state.ui.statusFailed = failure !== null;
            },
            render: () => {
                const S = state.strings;
                const html = state.status ? renderStatus(state.status, S, lang()) : '';
                return state.ui.statusFailed ? html + notice({ kind: 'error', html: t(S, 'web.status.failed') }) : html;
            },
        },
        connect: {
            id: 'tgj-connect',
            dependsOnStatus: true,
            load: async () => {},
            render: () => (state.status ? renderConnect(connectUi(), state.strings, state.links, state.settings) : ''),
        },
        bot: {
            id: 'tgj-bot',
            dependsOnStatus: true,
            load: loadLinks,
            render: () => renderBot(state.status, state.links, state.strings, state.ui),
        },
        proxy: {
            id: 'tgj-proxy',
            load: loadSettings,
            render: () => renderProxyForm(state.settings, state.strings, proxyUi()),
        },
        destinations: {
            id: 'tgj-destinations',
            load: loadDestinations,
            render: () => renderDestinations(state.destinations, state.libraries, state.links, state.strings, {
                editing: state.ui.destEditing,
                forms: state.forms,
                confirmRemove: state.ui.destConfirm,
                leave: state.ui.destLeave,
                results: state.ui.destResults,
                presetAsk: state.ui.destPresetAsk && state.ui.destPresetAsk.id,
            }),
        },
        // Reads the destinations state (same data as the Destinations section), so it has nothing to load itself.
        manual: {
            id: 'tgj-manual',
            load: async () => {},
            render: () => renderManual(state.destinations, state.strings, {
                input: state.ui.manualInput,
                preset: state.ui.manualPreset,
                result: state.ui.manualResult,
                detectedPresets: state.ui.detectedPresets,
            }),
        },
        messages: {
            id: 'tgj-messages',
            load: loadSettings,
            render: () => renderMessages(state.settings, state.strings, settingsUi()),
        },
        batching: {
            id: 'tgj-batching',
            load: loadSettings,
            render: () => renderBatching(state.settings, state.strings, settingsUi()),
        },
        updates: {
            id: 'tgj-updates',
            load: loadSettings,
            render: () => renderUpdates(state.settings, state.strings, settingsUi()),
        },
        diagnostics: {
            id: 'tgj-diagnostics',
            load: async () => {},
            render: () => renderDiagnostics(state.strings, { text: state.ui.diagText, copied: state.ui.diagCopied }),
        },
        templates: {
            id: 'tgj-templates',
            load: async () => {
                const result = await api.get('Templates');
                if (result.status !== 200 || !result.body) {
                    throw new Error('HTTP ' + result.status);
                }

                takeTemplates(result.body);
                // Opening the section shows the preview of the template in use at once.
                validateTemplate().catch(err => console.error('TgJelly: the template preview failed', err));
            },
            render: () => renderTemplates(state.templates, state.strings, templatesUi()),
        },
    };

    /** The sidebar counts, plain text by section: the destinations, and how many templates are edited. */
    const navCounts = () => {
        const S = state.strings;
        const counts = {};
        const destinationCount = state.destinations ? (state.destinations.Items ?? []).length : 0;
        if (destinationCount > 0) {
            counts.destinations = formatNumber(destinationCount, lang());
        }

        const edited = state.templates ? (state.templates.Items ?? []).filter(item => item.Edited).length : 0;
        if (edited > 0) {
            counts.templates = pluralText(S, 'web.nav.edited', edited, {}, lang());
        }

        return counts;
    };

    const connection = () => connectionLabel(state.status, state.strings, state.ui.statusFailed);
    const pluginVersion = () => (state.status && state.status.Version) || '';

    /** Shows one section and marks its sidebar button; with { remember: true } also records it in the URL hash. */
    const selectSection = (name, { remember = false } = {}) => {
        if (!SECTIONS.includes(name)) {
            return;
        }

        activeSection = name;
        for (const other of SECTIONS) {
            const section = byId(`tgj-sec-${other}`);
            if (section) {
                section.hidden = other !== name;
            }

            const button = byId(`tgj-nav-${other}`);
            if (button) {
                if (other === name) {
                    button.setAttribute('aria-current', 'page');
                } else {
                    button.removeAttribute('aria-current');
                }
            }
        }

        const location = window.location;
        const history = window.history;
        if (remember && location && history && typeof history.replaceState === 'function') {
            // replaceState: no history entry, and no hashchange, so Jellyfin's router doesn't navigate.
            history.replaceState(history.state, '', hashWithSection(location.hash, name));
        }
    };

    /**
     * Writes the shell into #tgjelly-root, once. The first section shown is the one in the URL hash, except that
     * Overview leads until a bot is connected (design §5.2).
     */
    const drawShell = () => {
        const root = byId('tgjelly-root');
        if (shellDrawn || !root || !state.strings) {
            return;
        }

        const connected = Boolean(state.status && state.status.HasToken);
        const first = connected ? sectionFromHash(window.location && window.location.hash) : 'overview';
        root.innerHTML = renderShell(state.strings, { active: first, connection: connection(), counts: navCounts(), version: pluginVersion() });
        shellDrawn = true;
        selectSection(first);
    };

    /** Brings the header label and the sidebar counts up to date (each written only when it changed). */
    const renderChrome = () => {
        if (!shellDrawn) {
            return;
        }

        setHtml('tgj-conn', connectionHtml(connection()));
        setHtml('tgj-version', versionHtml(pluginVersion()));
        const counts = navCounts();
        for (const name of SECTIONS) {
            setHtml(`tgj-nav-count-${name}`, esc(counts[name] ?? ''));
        }
    };

    const renderSection = name => {
        drawShell();
        const section = sections[name];
        // While the write runs, a focused field it replaces may fire focusout: that is not the admin leaving the field.
        writing++;
        try {
            keepFocus(byId(section.id), () => setHtml(section.id, section.render() + sectionError(name)), { pending: pendingFields });
        } finally {
            writing--;
        }

        paintCues();
        renderChrome();

        if (name === 'destinations') {
            // The manual block shows the same data (detected chats, the gate), so it follows every Destinations render;
            // the templates section lists the destinations for its test send.
            renderSection('manual');
            renderSection('templates');
        }

        if (name === 'proxy' || name === 'connect') {
            restoreSecrets();
        }

        if (name === 'diagnostics' && state.ui.diagText) {
            const area = byId('tgj-diagnostics-text');
            if (area && typeof area.select === 'function') {
                area.select();
            }
        }
    };

    /** The three sections that edit the settings form render together, so a save in one shows in the others. */
    const renderSettingsSections = () => {
        for (const name of ['messages', 'batching', 'updates']) {
            renderSection(name);
        }
    };

    /** Puts the typed proxy username and password back into the inputs after a render (never through HTML). */
    const restoreSecrets = () => {
        if (typeof view.querySelectorAll !== 'function') {
            return;
        }

        for (const field of ['proxyUsername', 'proxyPassword']) {
            for (const el of view.querySelectorAll(`[data-tgj-input="${field}"]`)) {
                el.value = typed[field] ?? '';
            }
        }
    };

    /** Sections that show state derived from Status (whether a token is stored) render again after it changes. */
    const renderDependents = () => {
        if (state.status && !state.status.HasToken) {
            state.ui.confirmDisconnect = false;
        }

        for (const [name, section] of Object.entries(sections)) {
            if (section.dependsOnStatus) {
                try {
                    renderSection(name);
                } catch (err) {
                    console.error(`TgJelly: the ${name} section could not be rendered`, err);
                }
            }
        }
    };

    /**
     * Re-runs one section's load, then renders it. Calls while a load runs share it (so polls can't overlap); pass
     * { fresh: true } after a change, to wait for a load that started before it and load again.
     */
    const reloadSection = async (name, { fresh = false } = {}) => {
        if (fresh) {
            await inFlight.get(name);
        }

        let pending = inFlight.get(name);
        if (!pending) {
            pending = (async () => {
                try {
                    await sections[name].load();
                    if (loadErrors.delete(name)) {
                        delete state.ui.errors[name];
                    }

                    renderSection(name);
                } catch (err) {
                    console.error(`TgJelly: the ${name} section failed to load or render`, err);
                    loadErrors.add(name);
                    const S = state.strings;
                    state.ui.errors[name] = t(S, 'web.requestFailed', { reason: err && err.message });
                    try {
                        renderSection(name);
                    } catch (renderErr) {
                        console.error(`TgJelly: the ${name} section could not be rendered`, renderErr);
                    }
                }

                if (name === 'status') {
                    renderDependents();
                    await syncRevisions();
                }
            })().finally(() => inFlight.delete(name));
            inFlight.set(name, pending);
        }

        return pending;
    };

    // The sections that hold a copy of the one settings Revision (spec §10.4), by the state their load fills.
    const REVISION_SECTIONS = { settings: ['proxy', 'messages', 'batching', 'updates'], destinations: ['destinations'], templates: ['templates'] };
    // The newest Revision the page has caught up with: the sections were loaded at it, or reloaded after it.
    let syncedRevision = 0;
    const newestRevision = () => Math.max(0, ...['status', ...Object.keys(REVISION_SECTIONS)]
        .map(key => (state[key] && typeof state[key].Revision === 'number' ? state[key].Revision : 0)));

    /**
     * When a save or the Status poll shows a newer Revision than the page has caught up with, reloads every section still
     * holding an older one. Every section checks ExpectedRevision against the same Revision, so without this a save in
     * one section would make the next save in another conflict with the page's own change, and a destination added in
     * the bot would never show. Unsaved edits survive: the loads go through syncForm.
     */
    const syncRevisions = async () => {
        const newest = newestRevision();
        if (newest <= syncedRevision) {
            return;
        }

        syncedRevision = newest;
        await Promise.all(Object.entries(REVISION_SECTIONS)
            .filter(([key]) => state[key] && state[key].Revision < newest)
            .flatMap(([, names]) => names.map(name => reloadSection(name, { fresh: true }))));
    };

    /**
     * Runs an action for a section: clears the section's error, then records a thrown error, or a returned API result
     * with a 3xx-5xx status (the server's Message when it sent one), and re-renders the section.
     * @param {string} name the section
     * @param {() => Promise<unknown>} fn
     */
    const runAction = async (name, fn) => {
        const S = state.strings;
        delete state.ui.errors[name];
        try {
            const result = await fn();
            if (result && typeof result.status === 'number' && result.status >= 300) {
                state.ui.errors[name] = requestError(S, result);
            }
        } catch (err) {
            console.error('TgJelly: action failed', err);
            state.ui.errors[name] = t(S, 'web.requestFailed', { reason: err && err.message });
        }

        await syncRevisions();
        renderSection(name);
    };

    /** Takes fresh server data into a form, keeping unsaved edits (spec §10.4). Call it from a section's load. */
    const syncForm = (name, data) => {
        state.forms[name] = syncFormData(state.forms[name], data);
        return state.forms[name];
    };

    /** Records an edit: sets path in the form's local values (when given) and marks the form dirty. */
    const markDirty = (name, path, value) => {
        const form = state.forms[name];
        if (!form) {
            return;
        }

        state.forms[name] = path === undefined ? { ...form, dirty: true } : editForm(form, path, value);
    };

    /** Drops unsaved edits, e.g. after a save: the form starts over from data (default: its current values). */
    const resetForm = (name, data = state.forms[name].local) => {
        state.forms[name] = syncFormData(null, data);
    };

    /** Renders every place the proxy form appears: Advanced and the "Can't reach Telegram?" panel. */
    const renderProxyForms = () => {
        renderSection('proxy');
        if (state.ui.unreachable) {
            renderSection('connect');
        }
    };

    const chooseMode = mode => {
        state.ui.mode = mode;
        state.ui.result = null;
        renderSection('connect');
    };

    const setConfirmDisconnect = value => {
        state.ui.confirmDisconnect = value;
        renderSection('bot');
    };

    /** Sends the token (spec §6.1) and acts on the outcome. Returns the API result only when it failed. */
    const postToken = async confirmSwitch => {
        const ui = state.ui;
        ui.saving = true;
        ui.result = null;
        renderSection('connect');
        let keepToken = false;
        try {
            let result;
            try {
                result = await api.post('Token', { Token: typed.token, SetProfile: ui.setProfile, ConfirmSwitch: confirmSwitch });
            } finally {
                ui.saving = false;
            }

            const reply = result.body;
            if (result.status !== 200 || !reply) {
                return result;
            }

            const message = reply.Message;
            if (reply.Outcome === 'NeedsConfirm') {
                keepToken = true;
                ui.result = { kind: 'NeedsConfirm', message };
                return undefined;
            }

            if (reply.Outcome === 'Connected') {
                Object.assign(ui, {
                    result: { kind: 'Connected', username: reply.BotUsername, webhookDeleted: Boolean(reply.WebhookDeleted) },
                    mode: null,
                    changingToken: false,
                    unreachable: false,
                });
                // Links first: the Open button needs the new bot's link.
                await Promise.all([reloadSection('status', { fresh: true }), reloadSection('bot', { fresh: true })]);
            } else {
                ui.result = { kind: 'Error', message };
                ui.unreachable = ui.unreachable || reply.Outcome === 'Unreachable';
            }

            return undefined;
        } finally {
            // The token is kept only for Switch bot: the re-render has emptied the input, so a stale one must not be resent.
            if (!keepToken) {
                typed.token = '';
            }
        }
    };

    // --- auto-save (plan 8, design §6): the one page-wide queue, the cues, and the saves of Messages and Advanced ---
    //
    // The pattern every auto-saved field follows (Tasks 7 and 8 reuse it):
    // - the control carries data-tgj-input (its handler, and how keepFocus finds it again) and data-tgj-save="<key>",
    //   the key of its save; its row passes the same key as cueKey, so the cue shows beside it;
    // - its input handler records the edit in the form (markDirty), then calls queueSave({ key, mode, fields, validate,
    //   send }) with the field's fieldSelector in fields;
    // - send() builds today's request from the form at the time it runs, with the current Revision, and maps the reply
    //   with saveResult(): 200 'saved', 409 'conflict' with a retry that takes body.Current and sends again, 400
    //   'invalid' with the server's message, anything else 'failed';
    // - focusout flushes the save named by data-tgj-save, unless the focus moves to another field with the same key (the
    //   proxy card's fields share one); Enter in an input does too; leaving the page flushes everything.

    /** The cue each data-tgj-cue spot shows, by key ({ state, text }), so a section write can paint it again. */
    const cues = new Map();
    // Per save key: the number of the newest change, and the fields it holds in pendingFields until the newest change is
    // saved. An unsaved change (waiting, in flight, invalid or failed) keeps them: a refresh never replaces its value.
    const changeSeq = new Map();
    const heldFields = new Map();

    /**
     * The markup a cue shows (design §3): the state word as a label, Saved good, Saving… progress and Not saved bad, and
     * after Not saved the reason as plain text, so it can be long. The states come from tgjelly-autosave.js, the words
     * from the strings.
     */
    const cueHtml = cue => {
        const S = state.strings;
        if (cue.state === 'saved') {
            return label(text(S, 'web.autosave.saved'), 'good');
        }

        if (cue.state === 'saving') {
            return label(text(S, 'web.autosave.saving'), 'progress');
        }

        if (cue.state === 'error') {
            const reason = cue.text === CONFLICT_KEY ? text(S, 'web.autosave.conflict') : cue.text ?? '';
            return `${label(text(S, 'web.autosave.notSaved'), 'bad')} <span class="tgj-cue-reason">${esc(reason)}</span>`;
        }

        return '';
    };

    // What each spot shows now: a spot is written only when its markup changes, so a repaint after another section's
    // write doesn't make its live region say the same words again.
    const painted = new WeakMap();

    /** Writes a key's cue into its spots, in place (the section is not written again). Idle clears class and markup. */
    const paintCue = key => {
        if (typeof view.querySelectorAll !== 'function') {
            return;
        }

        const cue = cues.get(key);
        const className = cue ? `tgj-cue tgj-cue--${cue.state}` : 'tgj-cue';
        const html = cue ? cueHtml(cue) : '';
        for (const spot of view.querySelectorAll(`[data-tgj-cue="${selectorValue(key)}"]`)) {
            if (spot.className !== className) {
                spot.className = className;
            }

            if ((painted.get(spot) ?? '') !== html) {
                spot.innerHTML = html;
                painted.set(spot, html);
            }
        }
    };

    /** After a section write, the new cue spots show the cues that are still up. */
    const paintCues = () => {
        for (const key of cues.keys()) {
            paintCue(key);
        }
    };

    const onCue = (key, cue) => {
        if (cue.state === 'idle') {
            cues.delete(key);
        } else {
            cues.set(key, cue);
        }

        paintCue(key);
    };

    /** Lets go of the fields a save key holds in pendingFields. */
    const releaseFields = key => {
        for (const field of heldFields.get(key) ?? []) {
            pendingFields.delete(field);
        }

        heldFields.delete(key);
    };

    const onSettled = (key, result) => {
        if (result.status === 'saved') {
            // Other sections hold the old Revision now (they share it); bring them up to date, keeping their edits.
            syncRevisions().catch(err => console.error('TgJelly: could not catch up with the new revision', err));
        }
    };

    const autosave = createAutosave({
        clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: handle => clearTimeout(handle) },
        onCue,
        onSettled,
    });

    /**
     * Queues the save of one change (design §6.1): mode 'now' (switches, selects), 'debounce' with delay (typed text and
     * numbers; flushed on blur too), or 'blur' (fields whose save has side effects: sent only on blur or Enter).
     * validate returns the reason (plain text) the value can't be saved, or null; a debounced value is checked when it is
     * due, so a half-typed number doesn't flash an error. fields are fieldSelector keys: kept as typed across section
     * writes (pendingFields) until the save settles.
     * @param {{ key: string, mode: 'now' | 'debounce' | 'blur', delay?: number, fields?: (string | null)[],
     *   validate?: () => string | null, send: () => Promise<object> }} job
     */
    const queueSave = ({ key, mode, delay, fields = [], validate = null, send }) => {
        const seq = (changeSeq.get(key) ?? 0) + 1;
        changeSeq.set(key, seq);
        const held = heldFields.get(key) ?? new Set();
        for (const field of fields.filter(Boolean)) {
            held.add(field);
            pendingFields.add(field);
        }

        heldFields.set(key, held);
        // Once this change is saved, and no newer one came in meanwhile, its fields follow the server again. A conflict's
        // retry is followed the same way.
        const settle = result => {
            if (result && result.status === 'conflict' && typeof result.retry === 'function') {
                const retry = result.retry;
                return { ...result, retry: async () => settle(await retry()) };
            }

            if (result && result.status === 'saved' && changeSeq.get(key) === seq) {
                releaseFields(key);
            }

            return result;
        };
        const dueCheck = mode === 'debounce' && validate;
        autosave.change({
            key,
            mode,
            delay,
            validate: dueCheck ? undefined : validate ?? undefined,
            send: async () => {
                const reason = dueCheck ? validate() : null;
                return reason ? { status: 'invalid', message: reason } : settle(await send());
            },
        });
    };

    /** The plain-text reason a request failed, for a cue: the server's Message, or the status code. */
    const failureText = result => (result.body && result.body.Message
        ? String(result.body.Message)
        : text(state.strings, 'web.requestFailed', { reason: result.status }));

    /**
     * Maps a save's reply to the queue's SaveResult: 200 with a body runs saved(body); 409 with Current gives a conflict
     * whose retry runs conflict(Current) (take the server data, re-apply the change) and sends again; 400 with Errors is
     * invalid with the server's messages (those for fields when any match them); anything else failed.
     * @param {{ status: number, body: any }} result
     * @param {{ saved: (body: any) => void, conflict: (current: any) => void, again: () => Promise<object>, fields?: string[] }} on
     */
    const saveResult = (result, { saved, conflict, again, fields = [] }) => {
        if (result.status === 200 && result.body) {
            saved(result.body);
            return { status: 'saved' };
        }

        if (result.status === 409 && result.body && result.body.Current) {
            return {
                status: 'conflict',
                retry: () => {
                    conflict(result.body.Current);
                    return again();
                },
            };
        }

        if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
            const errors = result.body.Errors;
            const mine = errors.filter(e => fields.some(f => e.Field === f || String(e.Field).startsWith(`${f}.`)));
            return { status: 'invalid', message: (mine.length > 0 ? mine : errors).map(e => String(e.Message ?? '')).join(' ') };
        }

        return { status: 'failed', message: failureText(result) };
    };

    /**
     * Sends one auto-save of the settings model: the fields at paths, laid over the saved settings, with the current
     * Revision (spec §10.4). Edits waiting in other fields stay edits, and so does a value typed while the request ran.
     * @param {string[]} paths
     */
    const sendSettings = async paths => {
        const form = state.forms.settings;
        const S = state.strings;
        if (!form || !state.settings) {
            return { status: 'failed', message: text(S, 'web.requestFailed', { reason: 'no settings' }) };
        }

        const request = settingsRequest(form, paths, pageRevision(), S);
        if (!request.body) {
            return { status: 'invalid', message: request.errors.map(e => e.Message).join(' ') };
        }

        const sent = overlayPaths(form.base, form.local, paths);
        return saveResult(await api.put('Settings', request.body), {
            fields: paths,
            saved: body => {
                // The sent values are now the saved ones; anything else that differs from them is still an edit.
                const now = state.forms.settings;
                state.forms.settings = syncFormData({ base: overlayPaths(now.base, sent, paths), local: now.local, dirty: true, changed: [] }, settingsFormData(body));
                takeSettings(body);
                renderSettingsSections();
            },
            conflict: current => {
                // The form keeps the admin's edits on top of the server data (syncFormData): the admin's value wins.
                takeSettings(current);
                renderSettingsSections();
            },
            again: () => sendSettings(paths),
        });
    };

    /** Queues the save of one settings field (its dotted path is also its save and cue key). */
    const saveSetting = (path, mode, el, { delay, validate } = {}) => queueSave({
        key: path,
        mode,
        delay,
        fields: el ? [fieldSelector(el)] : [],
        validate,
        send: () => sendSettings([path]),
    });

    /** Typed text and numbers save this long after the last keystroke (design §6.1). */
    const TYPING_SAVE_MS = 800;

    const publicUrlError = () => (validatePublicUrl(state.forms.settings && state.forms.settings.local.PublicUrl)
        ? null
        : text(state.strings, 'web.messages.urlInvalid'));

    /** A switch's new state on click: the opposite of the form's value at path. */
    const flipped = path => !pathValue(state.forms.settings && state.forms.settings.local, path);

    const proxyError = () => {
        const local = state.forms.proxy && state.forms.proxy.local;
        const withAddress = Boolean(local) && (local.Mode === 'Http' || local.Mode === 'Socks5');
        return withAddress && String(local.Url ?? '').trim() === '' ? text(state.strings, 'web.proxy.needsAddress') : null;
    };

    /**
     * Sends the proxy card as one save (spec §9.1, §10.4): an untouched username/password is omitted, a cleared one is "".
     * Every proxy save restarts the bot session, so it only runs when the focus leaves the card.
     */
    const sendProxy = async () => {
        const form = state.forms.proxy;
        if (!form || !state.settings) {
            return { status: 'failed', message: text(state.strings, 'web.requestFailed', { reason: 'no settings' }) };
        }

        const local = form.local;
        const withAddress = local.Mode === 'Http' || local.Mode === 'Socks5';
        const body = { ExpectedRevision: pageRevision(), Mode: local.Mode, Url: withAddress ? String(local.Url ?? '').trim() : '' };
        const secrets = { Username: typed.proxyUsername, Password: typed.proxyPassword };
        // An untouched field is omitted (the server keeps the stored value); a cleared one is "".
        for (const [name, value] of Object.entries(secrets)) {
            if (value !== undefined) {
                body[name] = value;
            }
        }

        const sent = structuredClone(local);
        return saveResult(await api.put('Proxy', body), {
            fields: ['Proxy'],
            saved: dto => {
                // The proxy form starts over from the saved data, keeping a value typed while the request ran; the
                // settings form keeps its unsaved edits on top of the fresh data.
                state.forms.proxy = syncFormData({ base: sent, local: state.forms.proxy.local, dirty: true, changed: [] }, proxyFormData(dto));
                takeSettings(dto);
                if (typed.proxyUsername === secrets.Username) {
                    typed.proxyUsername = undefined;
                }

                if (typed.proxyPassword === secrets.Password) {
                    typed.proxyPassword = undefined;
                }

                renderProxyForms();
                renderSettingsSections();
            },
            conflict: current => {
                takeSettings(current);
                renderProxyForms();
            },
            again: sendProxy,
        });
    };

    /** Queues the proxy card's save: one job, saved when the focus leaves the card (or on Enter). */
    const saveProxy = el => queueSave({ key: 'proxy', mode: 'blur', fields: [fieldSelector(el)], validate: proxyError, send: sendProxy });

    /**
     * Why an event's typed template can't be saved (design §6.3), or null: empty text never saves (the server would take
     * it as "remove the override"; Reset is the way back), nor does text whose latest validation had errors.
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
            return { status: 'failed', message: text(S, 'web.requestFailed', { reason: 'no template' }) };
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

        return saveResult(await api.put(`Templates/${encodeURIComponent(key)}`, { ExpectedRevision: pageRevision(), Text: value }), {
            fields: ['Text'],
            saved: dto => {
                // The sent text is now the saved one; text typed while the request ran is still an edit.
                const now = state.forms[name];
                state.forms[name] = { base: { Text: value }, local: now ? now.local : { Text: value }, dirty: true, changed: [] };
                takeTemplates(dto);
                renderSection('templates');
            },
            conflict: current => {
                // The form keeps the admin's text on top of the server data (syncFormData): the admin's value wins.
                takeTemplates(current);
                renderSection('templates');
            },
            again: () => sendTemplate(key),
        });
    };

    /** Queues the save of an event's typed template, 1 s after the last keystroke or when the textarea is left. */
    const saveTemplate = (key, el) => queueSave({
        key: templateSaveKey(key),
        mode: 'debounce',
        delay: TEMPLATE_SAVE_MS,
        fields: [fieldSelector(el)],
        validate: () => templateSaveError(key),
        send: () => sendTemplate(key),
    });

    const destination = id => (state.destinations ? state.destinations.Items.find(d => d.Id === id) : undefined);
    const destKey = id => `dest:${id}`;
    const idOf = el => el.getAttribute('data-tgj-id');

    /** A failed removal's reply (spec §10.4): a 409 shows the fresh data and says so; other failures go to runAction. */
    const removalReply = result => {
        const S = state.strings;
        if (result.status === 409 && result.body && result.body.Current) {
            takeDestinations(result.body.Current);
            state.ui.errors.destinations = t(S, 'web.dest.conflictPlain');
            return undefined;
        }

        return result.status >= 300 ? result : undefined;
    };

    /**
     * Sends one auto-save of a destination (design §6.1): what its form changed since the server data, with the current
     * Revision. Changes still waiting behind it go along, so a later job for this destination may find nothing left to
     * send. On success the form starts over from the reply, keeping a change made while the request ran.
     * @param {string} id
     */
    const sendDestination = async id => {
        const form = state.forms[destKey(id)];
        if (!form || !destination(id)) {
            return { status: 'failed', message: text(state.strings, 'web.requestFailed', { reason: 'no destination' }) };
        }

        const body = destinationChanges(form.base, form.local, pageRevision());
        if (!body) {
            return { status: 'saved' };
        }

        const sent = structuredClone(form.local);
        return saveResult(await api.put(`Destinations/${encodeURIComponent(id)}`, body), {
            fields: Object.keys(body).filter(key => key !== 'ExpectedRevision'),
            saved: dto => {
                const now = state.forms[destKey(id)];
                state.forms[destKey(id)] = syncFormData({ base: sent, local: now ? now.local : sent, dirty: true, changed: [] }, destinationFormData(dto));
                takeDestination(dto);
                renderSection('destinations');
            },
            conflict: current => {
                // The form keeps the admin's edits on top of the server data (syncFormData): the admin's value wins.
                takeDestinations(current);
                renderSection('destinations');
            },
            again: () => sendDestination(id),
        });
    };

    /** Queues the save of one destination control (switches and the preset save at once); field is its fieldSelector. */
    const saveDestination = (id, what, field) => queueSave({
        key: destSaveKey(id, what),
        mode: 'now',
        fields: [field],
        send: () => sendDestination(id),
    });

    /** The preset select of a destination's editor, as fieldSelector finds it. */
    const presetField = id => fieldSelector({ getAttribute: name => ({ 'data-tgj-input': 'destPreset', 'data-tgj-id': id })[name] ?? null });

    /** Switches a destination to a preset (its events with it), shows it, and saves it. */
    const applyPreset = (id, preset) => {
        const events = state.destinations.Catalog.Presets[preset];
        editDestination(id, events ? { Preset: preset, Events: [...events] } : { Preset: preset });
        // Rendered before the save holds the select, so the write shows the new preset rather than keeping the old one.
        renderSection('destinations');
        saveDestination(id, 'Preset', presetField(id));
    };

    const destinationOutcome = (id, html) => {
        state.ui.destResults[id] = html;
    };

    const openManual = () => {
        selectSection('advanced', { remember: true });
        const manual = byId('tgj-manual');
        if (manual && typeof manual.scrollIntoView === 'function') {
            manual.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    };

    /** Records editor values for a destination (the caller renders and saves). */
    const editDestination = (id, edits) => {
        for (const [path, value] of Object.entries(edits)) {
            markDirty(destKey(id), path, value);
        }
    };

    /** The manual block's reply to an add or set-up (spec §6.5): shows the outcome and reloads; other failures go to runAction. */
    const manualReply = async result => {
        const S = state.strings;
        const body = result.body;
        if (result.status === 200 && body) {
            const outcome = body.Outcome === 'AlreadyExists' ? t(S, 'web.manual.AlreadyExists') : t(S, 'web.manual.Added');
            const test = body.Outcome === 'AlreadyExists' ? ''
                : ' ' + (body.TestSent ? t(S, 'web.dest.test.ok') : t(S, 'web.dest.test.failed', { error: body.TestError }));
            state.ui.manualResult = `<p>${outcome}${test}</p>`;
            state.ui.manualInput = '';
            await reloadSection('destinations', { fresh: true });
            return undefined;
        }

        if (result.status === 400 && body && Array.isArray(body.Errors)) {
            state.ui.manualResult = body.Errors.map(e => `<p class="tgj-error">${esc(e.Message)}</p>`).join('');
            return undefined;
        }

        if (result.status === 409 && body && body.Current) {
            takeDestinations(body.Current);
            state.ui.manualResult = `<p class="tgj-error">${t(S, 'web.dest.conflictPlain')}</p>`;
            renderSection('destinations');
            return undefined;
        }

        return result;
    };

    const detectedId = el => {
        const id = idOf(el);
        return /^-?\d+$/.test(id) ? id : null;
    };

    const toggled = (list, value, on) => (on ? [...new Set([...list, value])] : list.filter(x => x !== value));

    const actions = {
        dismiss: el => runAction('status', async () => {
            const result = await api.post('Status/Dismiss', { NoticeId: el.getAttribute('data-tgj-id') });
            await reloadSection('status', { fresh: true });
            return result;
        }),
        takeover: () => runAction('status', async () => {
            const result = await api.post('Webhook/TakeOver');
            await reloadSection('status', { fresh: true });
            return result;
        }),
        connectNew: () => chooseMode('new'),
        connectExisting: () => chooseMode('existing'),
        changeToken: () => {
            state.ui.changingToken = true;
            state.ui.result = null;
            chooseMode('existing');
        },
        cancelToken: () => {
            typed.token = '';
            Object.assign(state.ui, { changingToken: false, mode: null, result: null, unreachable: false });
            renderSection('connect');
        },
        saveToken: () => runAction('connect', () => postToken(false)),
        switchBot: () => (typed.token ? runAction('connect', () => postToken(true)) : undefined),
        cancelSwitch: () => {
            typed.token = '';
            state.ui.result = null;
            renderSection('connect');
        },
        disconnect: () => setConfirmDisconnect(true),
        disconnectNo: () => setConfirmDisconnect(false),
        disconnectYes: () => runAction('bot', async () => {
            state.ui.saving = true;
            renderSection('bot');
            try {
                const result = await api.del('Token');
                if (result.status >= 300) {
                    return result;
                }

                Object.assign(state.ui, { confirmDisconnect: false, result: null, mode: null, changingToken: false });
                await Promise.all([reloadSection('status', { fresh: true }), reloadSection('bot', { fresh: true })]);
                return result;
            } finally {
                state.ui.saving = false;
            }
        }),
        edit: el => {
            const id = idOf(el);
            state.ui.destEditing = state.ui.destEditing === id ? null : id;
            state.ui.destConfirm = null;
            state.ui.destPresetAsk = null;
            renderSection('destinations');
        },
        // Leaving Custom for a preset, confirmed: the preset's events replace the hand-picked ones.
        presetReplace: el => {
            const ask = state.ui.destPresetAsk;
            state.ui.destPresetAsk = null;
            if (ask && ask.id === idOf(el) && state.forms[destKey(ask.id)]) {
                applyPreset(ask.id, ask.preset);
            } else {
                renderSection('destinations');
            }
        },
        presetCancel: () => {
            state.ui.destPresetAsk = null;
            renderSection('destinations');
        },
        // A leftover library is removed from the filter, and that saves at once (the Libraries title shows the cue).
        removeLibrary: el => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (form) {
                const gone = el.getAttribute('data-tgj-lib');
                editDestination(id, { LibraryIds: form.local.LibraryIds.filter(x => x !== gone) });
            }

            renderSection('destinations');
            if (form) {
                saveDestination(id, 'libraries', null);
            }
        },
        testDest: el => runAction('destinations', async () => {
            const id = idOf(el);
            const result = await api.post(`Destinations/${encodeURIComponent(id)}/Test`);
            if (result.status !== 200 || !result.body) {
                return result;
            }

            const S = state.strings;
            destinationOutcome(id, result.body.Sent ? t(S, 'web.dest.test.ok') : t(S, 'web.dest.test.failed', { error: result.body.Error }));
            await reloadSection('destinations', { fresh: true });
            return undefined;
        }),
        checkDest: el => runAction('destinations', async () => {
            const id = idOf(el);
            const result = await api.post(`Destinations/${encodeURIComponent(id)}/Check`);
            if (result.status !== 200 || !result.body) {
                return result;
            }

            const S = state.strings;
            const health = result.body.Health;
            destinationOutcome(id, result.body.Error ? esc(result.body.Error) : healthText(health && health.Status, S));
            await reloadSection('destinations', { fresh: true });
            return undefined;
        }),
        remove: el => {
            Object.assign(state.ui, { destConfirm: idOf(el), destLeave: false });
            renderSection('destinations');
        },
        removeNo: () => {
            state.ui.destConfirm = null;
            renderSection('destinations');
        },
        removeYes: el => runAction('destinations', async () => {
            const id = idOf(el);
            // Like every action that sends the Revision: the saves before it go first, so it sends the one they moved to
            // and doesn't conflict with the page's own change.
            await autosave.flush();
            const revision = pageRevision();
            const result = await api.del(`Destinations/${encodeURIComponent(id)}?leave=${state.ui.destLeave}&expectedRevision=${revision}`);
            state.ui.destConfirm = null;
            if (result.status === 204 || result.status === 200) {
                delete state.forms[destKey(id)];
                delete state.ui.destResults[id];
                await reloadSection('destinations', { fresh: true });
                return undefined;
            }

            return removalReply(result);
        }),
        'open-manual': openManual,
        manualAdd: () => runAction('manual', async () => {
            const value = state.ui.manualInput.trim();
            if (!value || !state.destinations) {
                return undefined;
            }

            state.ui.manualResult = '';
            const target = /^-?\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? { ChatId: Number(value) } : { TopicLink: value };
            await autosave.flush();
            return manualReply(await api.post('Destinations', { ExpectedRevision: pageRevision(), ...target, Preset: state.ui.manualPreset }));
        }),
        detectedSetUp: el => runAction('manual', async () => {
            const id = detectedId(el);
            if (id === null || !state.destinations) {
                return undefined;
            }

            state.ui.manualResult = '';
            const preset = state.ui.detectedPresets[id] ?? 'Everything';
            await autosave.flush();
            return manualReply(await api.post(`DetectedChats/${id}`, { ExpectedRevision: pageRevision(), Preset: preset }));
        }),
        detectedDismiss: el => runAction('manual', async () => {
            const id = detectedId(el);
            if (id === null) {
                return undefined;
            }

            state.ui.manualResult = '';
            const result = await api.del(`DetectedChats/${id}`);
            if (result.status !== 204 && result.status !== 200) {
                return result;
            }

            await reloadSection('destinations', { fresh: true });
            return undefined;
        }),
        // A whole URL, chosen with a button: it saves at once (the field itself saves on blur or Enter).
        useSuggested: () => {
            if (state.settings && state.settings.PublicUrlSuggestion) {
                markDirty('settings', 'PublicUrl', state.settings.PublicUrlSuggestion);
                // The suggestion replaces whatever was typed and waiting, so the field shows it.
                releaseFields('PublicUrl');
                renderSection('messages');
                saveSetting('PublicUrl', 'now', null, { validate: publicUrlError });
            }
        },
        copyDiagnostics: () => runAction('diagnostics', async () => {
            Object.assign(state.ui, { diagText: '', diagCopied: false });
            const result = await api.get('Diagnostics');
            if (result.status !== 200 || !result.body) {
                return result;
            }

            const text = String(result.body.Text ?? '');
            try {
                await navigator.clipboard.writeText(text);
                state.ui.diagCopied = true;
            } catch {
                state.ui.diagText = text;
            }

            return undefined;
        }),
        // Reset asks first, inline: Reset or Cancel.
        tplReset: () => {
            state.ui.tplResetAsk = true;
            renderSection('templates');
        },
        tplResetCancel: () => {
            state.ui.tplResetAsk = false;
            renderSection('templates');
        },
        tplResetConfirm: () => runAction('templates', async () => {
            const key = templateKey();
            const name = `tpl:${key}`;
            if (!state.templates) {
                return undefined;
            }

            const S = state.strings;
            // The reset replaces the typed text: a save still waiting for it is dropped, and one in flight is waited
            // for, so the text doesn't come back and the reset sends the Revision that save moved to.
            const saveKey = templateSaveKey(key);
            autosave.cancel(saveKey);
            releaseFields(saveKey);
            state.ui.tplBusy = true;
            renderSection('templates');
            await autosave.flush(saveKey);
            let result;
            try {
                result = await api.del(`Templates/${encodeURIComponent(key)}?expectedRevision=${pageRevision()}`);
            } finally {
                state.ui.tplBusy = false;
                state.ui.tplResetAsk = false;
            }

            if (result.status === 200 && result.body) {
                // The edits are gone: the form starts over from the built-in text, and the preview follows it.
                cancelTemplateTimer();
                delete state.forms[name];
                takeTemplates(result.body);
                validateTemplate().catch(err => console.error('TgJelly: the template preview failed', err));
                return undefined;
            }

            if (result.status === 409 && result.body && result.body.Current) {
                takeTemplates(result.body.Current);
                state.ui.errors.templates = t(S, 'web.templates.conflict');
                return undefined;
            }

            return result;
        }),
        // Sends the typed text, rendered with sample values, to the chosen destination; the outcome shows by the button.
        tplTest: () => runAction('templates', async () => {
            const key = templateKey();
            const form = state.forms[`tpl:${key}`];
            const target = templateTestTarget();
            if (!form || !target) {
                return undefined;
            }

            const S = state.strings;
            state.ui.tplTesting = true;
            state.ui.tplTestResult = '';
            renderSection('templates');
            let result;
            try {
                result = await api.post('Templates/Test', { EventKey: key, Text: String(form.local.Text ?? ''), DestinationId: target });
            } finally {
                state.ui.tplTesting = false;
            }

            if (result.status !== 200 || !result.body) {
                state.ui.tplTestResult = requestError(S, result);
                return undefined;
            }

            const reply = result.body;
            const health = reply.Health;
            state.ui.tplTestResult = reply.Sent
                ? t(S, 'web.templates.sent')
                : (reply.Error ? esc(reply.Error) : healthText(health && health.Status, S));
            // The send may have changed the destination's health.
            await reloadSection('destinations', { fresh: true });
            return undefined;
        }),
    };

    const inputs = {
        token: el => {
            typed.token = String(el.value ?? '').trim();
        },
        setProfile: el => {
            state.ui.setProfile = Boolean(el.checked);
        },
        // The proxy fields save together when the focus leaves the card (see the focusout listener).
        proxyMode: (el, event) => {
            markDirty('proxy', 'Mode', el.value);
            saveProxy(el);
            if (event.type === 'change') {
                renderProxyForms();
            }
        },
        // Typed fields act on input only: the change a browser fires on Enter or blur repeats a value already queued, and
        // queueing it again after Enter's save would send it twice (a second bot restart, for the proxy).
        proxyUrl: (el, event) => {
            if (event.type === 'input') {
                markDirty('proxy', 'Url', el.value);
                saveProxy(el);
            }
        },
        proxyUsername: (el, event) => {
            if (event.type === 'input') {
                typed.proxyUsername = String(el.value ?? '');
                saveProxy(el);
            }
        },
        proxyPassword: (el, event) => {
            if (event.type === 'input') {
                typed.proxyPassword = String(el.value ?? '');
                saveProxy(el);
            }
        },
        // Switches are buttons: they reach here on click. Each saves at once, with its cue beside it.
        destEnabled: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (form && event.type === 'click') {
                markDirty(destKey(id), 'Enabled', !form.local.Enabled);
                renderSection('destinations');
                saveDestination(id, 'Enabled', fieldSelector(el));
            }
        },
        // A select fires input and then change; the change is the choice. Between presets, or into Custom, it saves at
        // once; leaving Custom asks first (design §6.1a), and until Replace the select shows Custom and nothing is sent.
        destPreset: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const preset = el.value;
            if (event.type !== 'change' || !form || !destination(id) || !PRESETS.includes(preset)) {
                return;
            }

            if (preset === form.local.Preset) {
                state.ui.destPresetAsk = null;
                renderSection('destinations');
            } else if (form.local.Preset === 'Custom') {
                state.ui.destPresetAsk = { id, preset };
                renderSection('destinations');
            } else {
                state.ui.destPresetAsk = null;
                applyPreset(id, preset);
            }
        },
        destEvent: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const key = el.getAttribute('data-tgj-key');
            const keys = state.destinations ? state.destinations.Catalog.EventKeys : [];
            if (!form || event.type !== 'click' || !keys.includes(key)) {
                return;
            }

            const chosen = toggled(form.local.Events, key, !form.local.Events.includes(key));
            editDestination(id, { Events: keys.filter(k => chosen.includes(k)), Preset: 'Custom' });
            renderSection('destinations');
            saveDestination(id, `event:${key}`, fieldSelector(el));
        },
        destLibrary: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const lib = el.getAttribute('data-tgj-lib');
            if (!form || event.type !== 'click' || !lib) {
                return;
            }

            const on = !form.local.LibraryIds.some(x => sameLibrary(x, lib));
            editDestination(id, { LibraryIds: on ? [...form.local.LibraryIds, lib] : form.local.LibraryIds.filter(x => !sameLibrary(x, lib)) });
            renderSection('destinations');
            saveDestination(id, `lib:${lib}`, fieldSelector(el));
        },
        destSilent: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const group = el.getAttribute('data-tgj-group');
            if (!form || event.type !== 'click' || !state.destinations.Catalog.Groups.includes(group)) {
                return;
            }

            editDestination(id, { SilentGroups: toggled(form.local.SilentGroups, group, !form.local.SilentGroups.includes(group)) });
            renderSection('destinations');
            saveDestination(id, `silent:${group}`, fieldSelector(el));
        },
        // Part of the remove question: it saves nothing.
        destLeave: (el, event) => {
            if (event.type === 'click') {
                state.ui.destLeave = !state.ui.destLeave;
                renderSection('destinations');
            }
        },
        manualInput: el => {
            state.ui.manualInput = String(el.value ?? '');
        },
        manualPreset: el => {
            if (PRESETS.includes(el.value)) {
                state.ui.manualPreset = el.value;
            }
        },
        detectedPreset: el => {
            const id = detectedId(el);
            if (id !== null && PRESETS.includes(el.value)) {
                state.ui.detectedPresets[id] = el.value;
            }
        },
        // A partial URL can pass the rules, and would go live in message buttons: this saves on blur or Enter only.
        publicUrl: (el, event) => {
            if (event.type === 'input') {
                markDirty('settings', 'PublicUrl', String(el.value ?? ''));
                saveSetting('PublicUrl', 'blur', el, { validate: publicUrlError });
            }
        },
        // Switches are buttons: they reach here on click.
        msgToggle: (el, event) => {
            const key = el.getAttribute('data-tgj-key');
            if (MESSAGE_TOGGLES.includes(key) && event.type === 'click') {
                const path = `Message.${key}`;
                markDirty('settings', path, flipped(path));
                renderSection('messages');
                saveSetting(path, 'now', el);
            }
        },
        batching: (el, event) => {
            const field = BATCHING_FIELDS.find(f => f.key === el.getAttribute('data-tgj-key'));
            if (field && event.type === 'input') {
                const path = `Batching.${field.key}`;
                const typedText = String(el.value ?? '').trim();
                // A value that isn't a whole number stays text, so its save can say so instead of sending it.
                markDirty('settings', path, /^-?\d+$/.test(typedText) ? Number(typedText) : typedText);
                saveSetting(path, 'debounce', el, {
                    delay: TYPING_SAVE_MS,
                    // Checked as it would be sent: this value with the saved others (the server's cross-field rule too).
                    validate: () => batchingError(field, overlayPaths(state.forms.settings.base, state.forms.settings.local, [path]).Batching, state.strings),
                });
            }
        },
        updEnabled: (el, event) => {
            if (event.type === 'click') {
                markDirty('settings', 'UpdateCheck.Enabled', flipped('UpdateCheck.Enabled'));
                renderSection('updates');
                saveSetting('UpdateCheck.Enabled', 'now', el);
            }
        },
        // A select fires input and then change; the change is the choice.
        updMode: (el, event) => {
            if (UPDATE_MODES.includes(el.value) && event.type === 'change') {
                markDirty('settings', 'UpdateCheck.Mode', el.value);
                saveSetting('UpdateCheck.Mode', 'now', el);
            }
        },
        // Typing records the edit; after a pause it refreshes the preview pane (only the pane: re-rendering would move the
        // caret), and after a longer one the text saves itself (design §6.1). Typed text acts on input only.
        tplText: (el, event) => {
            const own = el.getAttribute('data-tgj-key');
            const key = state.templates && state.templates.Items.some(i => i.EventKey === own) ? own : templateKey();
            if (event.type !== 'input' || !state.forms[`tpl:${key}`]) {
                return;
            }

            markDirty(`tpl:${key}`, 'Text', String(el.value ?? ''));
            cancelTemplateTimer();
            tplTimer = setTimeout(() => {
                tplTimer = null;
                validateTemplate(key).catch(err => console.error('TgJelly: the template preview failed', err));
            }, PREVIEW_DEBOUNCE_MS);
            saveTemplate(key, el);
        },
        // A select fires input and then change; the change is the choice.
        tplEvent: (el, event) => {
            if (event.type === 'change' && state.templates && state.templates.Items.some(i => i.EventKey === el.value)) {
                cancelTemplateTimer();
                Object.assign(state.ui, { tplKey: el.value, tplResetAsk: false, tplTestResult: '' });
                renderSection('templates');
                return validateTemplate();
            }

            return undefined;
        },
        tplTestTo: el => {
            state.ui.tplTestTo = String(el.value ?? '');
        },
    };

    const stopPolling = () => {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    };

    const tick = () => {
        if (!view.isConnected || view.classList.contains('hide')) {
            stopPolling();
            return;
        }

        if (!document.hidden && !inFlight.has('status')) {
            reloadSection('status');
        }
    };

    const startPolling = () => {
        stopPolling();
        if (visible) {
            timer = setInterval(tick, POLL_MS);
        }
    };

    /** Loads the strings once; on failure shows the one hard-coded line and returns false. */
    const ensureStrings = async () => {
        if (state.strings) {
            return true;
        }

        stringsLoad ??= api.get('Strings?lang=' + encodeURIComponent(lang())).then(result => {
            if (result.status !== 200 || !result.body) {
                throw new Error('HTTP ' + result.status);
            }

            return result.body;
        });

        try {
            state.strings = await stringsLoad;
        } catch (err) {
            stringsLoad = null;
            console.error('TgJelly: could not load the page strings', err);
            const root = byId('tgjelly-root');
            if (root && !shellDrawn) {
                root.textContent = 'Could not load TgJelly.';
            }

            return false;
        }

        return true;
    };

    const show = async () => {
        visible = true;
        stopPolling();
        stylesheet ??= waitForStylesheet(byId('tgjelly-css'));
        const [ready] = await Promise.all([ensureStrings(), stylesheet]);
        if (!ready) {
            return;
        }

        startPolling();
        await reloadSection('status');
        await Promise.all(Object.keys(sections).filter(name => name !== 'status').map(name => reloadSection(name)));
        syncedRevision = Math.max(syncedRevision, newestRevision());
    };

    const hide = () => {
        visible = false;
        stopPolling();
        cancelTemplateTimer();
        // Leaving the page saves what is still waiting (a debounce, or a field saved on blur that still has the focus).
        autosave.flush().catch(logFailure);
    };

    /** Finds the handler for an event through the target's closest [attribute]; null when there is none. */
    const findHandler = (event, attribute, handlers) => {
        const el = event.target && event.target.closest ? event.target.closest(`[${attribute}]`) : null;
        const handler = el ? handlers[el.getAttribute(attribute)] : null;
        return handler && state.strings ? { el, run: () => Promise.resolve().then(() => handler(el, event)) } : null;
    };

    const logFailure = err => console.error('TgJelly: handler failed', err);

    view.addEventListener('viewshow', show);
    view.addEventListener('viewbeforehide', hide);
    view.addEventListener('viewhide', hide);
    view.addEventListener('viewdestroy', hide);

    view.addEventListener('click', event => {
        const found = findHandler(event, 'data-tgj-act', actions);
        if (!found) {
            return;
        }

        event.preventDefault();
        if (found.el.disabled) {
            return;
        }

        found.el.disabled = true;
        found.run().catch(logFailure).finally(() => {
            found.el.disabled = false;
        });
    });

    const onInput = event => {
        const found = findHandler(event, 'data-tgj-input', inputs);
        if (found) {
            found.run().catch(logFailure);
        }
    };
    view.addEventListener('change', onInput);
    view.addEventListener('input', onInput);

    // A switch (a button with role=switch and data-tgj-input) goes to its input handler on click.
    view.addEventListener('click', event => {
        const found = findHandler(event, 'data-tgj-input', inputs);
        if (found && found.el.getAttribute('role') === 'switch' && !found.el.disabled) {
            event.preventDefault();
            found.run().catch(logFailure);
        }
    });

    /** The save key of the auto-saved field an event is on (its data-tgj-save), or null. */
    const saveKeyOf = target => {
        const el = target && target.closest ? target.closest('[data-tgj-save]') : null;
        return el ? el.getAttribute('data-tgj-save') : null;
    };

    // Input handlers run a microtask after their event (findHandler), so a flush waits one too: the change it saves is
    // queued first.
    const flushSoon = key => Promise.resolve().then(() => autosave.flush(key)).catch(logFailure);

    // Leaving an auto-saved field saves it now (design §6.1). Fields that share a key save when the focus leaves all
    // of them: the proxy card saves once, when the focus leaves the card.
    view.addEventListener('focusout', event => {
        const key = writing > 0 || !state.strings ? null : saveKeyOf(event.target);
        if (key && saveKeyOf(event.relatedTarget) !== key) {
            flushSoon(key);
        }
    });

    // Enter in a one-line field saves it too.
    view.addEventListener('keydown', event => {
        const key = event.key === 'Enter' && state.strings ? saveKeyOf(event.target) : null;
        if (key && String(event.target.tagName).toUpperCase() === 'INPUT') {
            flushSoon(key);
        }
    });

    // A spoiler in the template preview is revealed (and hidden again) on click, as in Telegram.
    view.addEventListener('click', event => {
        const spoiler = event.target && event.target.closest ? event.target.closest('.tgj-tg-spoiler') : null;
        if (spoiler) {
            spoiler.classList.toggle('tgj-tg-revealed');
        }
    });

    // A sidebar button, or any control carrying data-tgj-nav (e.g. an Overview summary row), shows that section.
    view.addEventListener('click', event => {
        const el = event.target && event.target.closest ? event.target.closest('[data-tgj-nav]') : null;
        if (el) {
            event.preventDefault();
            selectSection(el.getAttribute('data-tgj-nav'), { remember: true });
        }
    });

    return { api, state, sections, actions, inputs, reloadSection, runAction, syncForm, markDirty, resetForm, pendingFields, queueSave, pageRevision };
}
