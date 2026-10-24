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
    esc, t, text, plural, pluralText, formatNumber, formatTime,
    label, card, row, actionButton, inlineConfirm, notice, emptyState, textField, switchControl, selectField, numberField,
    httpFailure,
} from './configurationpage?name=tgjelly-ui.js';
import { createAutosave, CONFLICT_KEY } from './configurationpage?name=tgjelly-autosave.js';
import { createMessages, messageFormData, messageRequest } from './configurationpage?name=tgjelly-messages.js';
import { createLog } from './configurationpage?name=tgjelly-log.js';

// These live in the sibling modules; they stay named exports here for the page's callers and tests.
export { esc, t, text, plural, pluralText, formatNumber, formatTime };
export { previewHtml, renderTemplateCheck, renderTemplatePreview } from './configurationpage?name=tgjelly-preview.js';

const POLL_MS = 3000;

/** Connection states shown in the error color (the ConnectionState names from GET Status). */
const PROBLEM_STATES = new Set(['TokenRejected', 'WebhookConflict', 'ConflictingInstance', 'Unreachable', 'InvalidBaseUrl', 'Incompatible']);

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

const POLL_SECONDS = POLL_MS / 1000;

/** The plain-text cause of a thrown error: the browser's or network's own words, or a note that it gave none. */
const errorReason = err => (err && err.message ? String(err.message) : 'the browser gave no details');

/**
 * The plain-text reason a failed API result gives: the server's Message with the HTTP status, or, when the server sent
 * no message, the status and where to look next.
 * @param {Record<string, string>} S
 * @param {{ status: number, body: any }} result
 * @returns {string}
 */
function failureReason(S, result) {
    if (result.body && result.body.Message) {
        return text(S, 'web.request.http', { message: result.body.Message, status: result.status });
    }

    if (result.status === 404) {
        return text(S, 'web.request.notFound');
    }

    return result.status === 401 || result.status === 403
        ? text(S, 'web.request.denied', { status: result.status })
        : text(S, 'web.request.httpBare', { status: result.status });
}

/** Whether a thrown error is the browser's or network's failure to reach the server (fetch rejects with a TypeError such as "Failed to fetch"; other TypeErrors are code bugs). */
const isNetworkError = err => Boolean(err) && (err.name === 'NetworkError' || err.name === 'AbortError'
    || (err instanceof TypeError && /failed to fetch|networkerror|load failed|network error/i.test(err.message)));

/**
 * The plain-text cue for a thrown error: "could not reach Jellyfin" for a network failure, otherwise the error as an
 * unexpected one, each with what to do next.
 */
export function thrownText(S, err) {
    return isNetworkError(err)
        ? text(S, 'web.requestFailed', { reason: errorReason(err) })
        : text(S, 'web.unexpectedError', { reason: errorReason(err) });
}

/** The same as HTML, escaped. */
export function thrownError(S, err) {
    return esc(thrownText(S, err));
}

/** The short cause of a failed Status request for the header: "HTTP 503: message", or the network's own words. */
const statusReason = (S, result) => (result.body && result.body.Message
    ? text(S, 'web.header.reasonHttp', { status: result.status, message: result.body.Message })
    : text(S, 'web.header.reasonHttpBare', { status: result.status }));

/**
 * The error text for a failed API result: the server's Message and the HTTP status; without a message, the status and
 * what to check.
 * @param {Record<string, string>} S
 * @param {{ status: number, body: any }} result
 * @returns {string} HTML
 */
export function requestError(S, result) {
    return esc(failureReason(S, result));
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
function telegramLink(url, label, { primary = false } = {}) {
    return typeof url === 'string' && url.startsWith('https://t.me/')
        ? `<a class="tgj-btn${primary ? ' tgj-btn--primary' : ''}" href="${esc(url)}" target="_blank" rel="noopener noreferrer">${label}</a>`
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

    // Hiding the credentials changes nothing: what was typed or saved stays (design §7).
    if (local.Mode === 'Http' || local.Mode === 'Socks5') {
        rows.push(field('proxyUsername', 'web.proxy.username', 'text', '', settings.HasProxyUsername ? text(S, 'web.proxy.saved') : ''));
        rows.push(field('proxyPassword', 'web.proxy.password', 'password', '', settings.HasProxyPassword ? text(S, 'web.proxy.saved') : ''));
    }

    rows.push(`<p class="tgj-row-hint">${t(S, 'web.proxy.typesHint')}</p>`);
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
 * @param {{ bare?: boolean }} [options] bare: the body only, for the Bot card while the token is being changed
 * @returns {string} HTML
 */
export function renderConnect(ui, S, links, settings = null, { bare = false } = {}) {
    const result = ui.result;
    const busy = Boolean(ui.saving);
    if (result && result.kind === 'Connected') {
        return result.webhookDeleted ? notice({ kind: 'info', html: t(S, 'web.connect.webhookRemoved') }) : '';
    }

    if (ui.hasToken && !ui.changingToken) {
        return '';
    }

    const title = text(S, 'web.connect.title');
    const wrap = body => (bare ? body : card({ title, body }));
    if (result && result.kind === 'NeedsConfirm') {
        return wrap(`<p>${esc(result.message)}</p><div class="tgj-actions">`
            + actionButton({ label: text(S, 'web.connect.switch'), act: 'switchBot', kind: 'primary', disabled: busy })
            + actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelSwitch', disabled: busy })
            + '</div>');
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

    return wrap(parts.join(''));
}

/**
 * The Bot card (spec §6.6 item 2, §6.4): the username, the access reminder, Open in Telegram, Change token and
 * Disconnect bot with its inline confirmation.
 * @param {object} status the StatusDto
 * @param {{ OpenBot?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ confirmDisconnect?: boolean, removeDestinations?: boolean, saving?: boolean }} ui
 * @param {string} [changeHtml] the change-token form, shown in place of the card's actions while the token is changed
 * @returns {string} HTML
 */
export function renderBot(status, links, S, ui, changeHtml = '') {
    if (!status || !status.HasToken) {
        return '';
    }

    const busy = Boolean(ui.saving);
    // The switch is part of the question, not a setting: it saves nothing (like "Bot leaves the chat" on a destination).
    const disconnect = ui.confirmDisconnect
        ? row({
            label: text(S, 'web.bot.disconnect.removeDestinations'),
            control: switchControl({ input: 'disconnectRemoveDestinations', checked: Boolean(ui.removeDestinations), label: text(S, 'web.bot.disconnect.removeDestinations') }),
        }) + `<p class="tgj-row-hint">${t(S, 'web.bot.disconnect.removeHint')}</p>` + inlineConfirm({
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
            + (changeHtml
                ? `<div class="tgj-bot-change">${changeHtml}</div>`
                : `<div class="tgj-actions">${telegramLink(links && links.OpenBot, t(S, 'web.bot.open'))}`
                    + `${actionButton({ label: text(S, 'web.bot.change'), act: 'changeToken', disabled: busy })}${disconnect}</div>`),
    });
}

/** The proxy editor's data from a SettingsDto. Username and Password are absent until the user types in them. */
export function proxyFormData(settings) {
    return { Mode: settings.ProxyMode, Url: settings.ProxyUrl };
}

/** The editor's data for one destination from the DestinationDto: the fields its auto-saves can send. */
export function destinationFormData(dest) {
    return {
        Enabled: Boolean(dest.Enabled),
        Events: [...(dest.Events ?? [])],
        LibraryIds: [...(dest.LibraryIds ?? [])],
        SilentGroups: [...(dest.SilentGroups ?? [])],
    };
}

/**
 * The body of a destination's PUT: only the fields that differ from base, plus ExpectedRevision (spec §7.2, §10.4).
 * @param {object} base the form data the edit started from
 * @param {object} local the form's current data
 * @param {number} revision
 * @returns {object | null} null when nothing changed
 */
export function destinationChanges(base, local, revision) {
    const body = { ExpectedRevision: revision };
    const differs = key => !sameValue(local[key], base[key]);
    for (const key of ['Events', 'LibraryIds', 'SilentGroups', 'Enabled']) {
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

/** The health as labels, only when something is wrong (a healthy destination shows none): the status, plus "Photos blocked" when photos are blocked too. */
function healthLabels(health, S) {
    const status = health && health.Status ? health.Status : 'Ok';
    const asLabel = ({ tone, text: words }) => label(words, tone);
    return (status === 'Ok' ? '' : asLabel(healthState(status, S)))
        + (health && health.PhotosBlocked && status !== 'PhotosBlocked' ? asLabel(healthState('PhotosBlocked', S)) : '');
}

/** The save key (and cue key) of one control in a destination's editor: 'dest:<id>:<what>'. */
const destSaveKey = (id, what) => `dest:${id}:${what}`;

/**
 * The editor card for one destination (spec §7.2, §7.3; design §6.1): the event switches by group,
 * the libraries and the silent groups. Every control saves itself at once (data-tgj-save, with its cue beside it);
 * Send test, Check again and Remove stay actions, and Remove asks inline first.
 * @param {object} dest the DestinationDto
 * @param {{ EventKeys: string[], Groups: string[] }} catalog
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object }, confirmRemove?: boolean, leave?: boolean, result?: string }} ui
 *   form defaults to the destination's own values; result is HTML shown under the buttons
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

    const events = new Set(local.Events);
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.events.title')}</h3>`);
    for (const group of catalog.Groups) {
        const keys = catalog.EventKeys.filter(k => k.startsWith(group + '.'));
        parts.push(`<div class="tgj-subcard" data-tgj-group-card="${esc(group)}"><h3 class="tgj-subcard-title">${t(S, `web.event.group.${group}`)}</h3>`
            + keys.map(k => toggle('destEvent', `event:${k}`, events.has(k), text(S, `web.event.${k}`), { 'data-tgj-key': k })).join('')
            + '</div>');
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

/** A destination's kind as plain text: "Channel", or "Forum" for a supergroup with topics. */
const destinationMeta = (dest, S) => text(S, dest.IsForum ? 'dest.kind.Forum' : `dest.kind.${dest.Kind}`);

/**
 * The Destinations section (spec §6.6 item 3; design §5.2): a card with one row per destination (its title, the kind
 * muted, the health as coloured words, the Sending switch and Edit), then the open destination's editor card.
 * Add destination, and "Set up manually" once the manual block is unlocked (spec §6.5), close the list, or make its
 * empty state.
 * @param {object | null} dto the DestinationsDto
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {{ StartAdd?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ editing?: string | null, forms?: Record<string, object>, confirmRemove?: string | null, leave?: boolean,
 *   results?: Record<string, string> }} ui forms is keyed 'dest:<id>' like the controller's state.forms
 * @returns {string} HTML
 */
export function renderDestinations(dto, libraries, links, S, ui) {
    if (!dto) {
        return '';
    }

    const forms = ui.forms ?? {};
    const add = telegramLink(links && links.StartAdd, t(S, 'web.dest.add'), { primary: true })
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
        }));
    }

    return `<div class="tgj-stack">${cards.join('')}</div>`;
}


/**
 * The manual card under Advanced (spec §6.5): "Add chat by ID" and the detected chats. Only when the gate is unlocked.
 * These are actions (Add, Set up, Dismiss), not settings: nothing here auto-saves.
 * @param {object | null} dto the DestinationsDto
 * @param {Record<string, string>} S
 * @param {{ input?: string, result?: string, busy?: boolean }} ui
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
                control: actionButton({ label: text(S, 'web.manual.setUp'), act: 'detectedSetUp', kind: 'primary', id })
                    + actionButton({ label: text(S, 'web.manual.dismiss'), act: 'detectedDismiss', id }),
            }));
        }
    }

    return card({ title: text(S, 'web.manual.title'), body: parts.join('') });
}

/** The three update kinds (design §7): the settings path's last part, the switch's input name and its strings. */
const UPDATE_KINDS = [
    { key: 'Fixes', input: 'updFixes', label: 'web.updates.fixes', hint: 'web.updates.fixesHint' },
    { key: 'NewReleases', input: 'updNewReleases', label: 'web.updates.newReleases', hint: 'web.updates.newReleasesHint' },
    { key: 'NewMajor', input: 'updNewMajor', label: 'web.updates.newMajor', hint: 'web.updates.newMajorHint' },
];

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
    const check = settings.UpdateCheck ?? {};
    return {
        PublicUrl: settings.PublicUrl ?? '',
        Message: messageFormData(settings.Message),
        Batching: { ...(settings.Batching ?? {}) },
        UpdateCheck: {
            Enabled: Boolean(check.Enabled),
            Fixes: check.Fixes !== false,
            NewReleases: check.NewReleases !== false,
            NewMajor: Boolean(check.NewMajor),
        },
    };
}

/** The UpdateCheck a PUT sends. */
function updateCheckRequest(check) {
    return { Enabled: check.Enabled, Fixes: check.Fixes, NewReleases: check.NewReleases, NewMajor: check.NewMajor };
}

/** The value at a dotted path ('UpdateCheck.Enabled') of a nested object; undefined when a part is missing. */
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
 * @param {string[]} paths e.g. ['Message.Switches.media.movie.ShowPoster']
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
                Message: messageRequest(data.Message, paths),
                Batching: data.Batching,
                UpdateCheck: updateCheckRequest(data.UpdateCheck),
            },
            errors: [],
        };
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
 * The batching card under Advanced (spec §10.1): six whole-number fields, each with its range in the explanation.
 * Each saves 800 ms after the last keystroke, or when the field is left.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is state.forms.settings
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
 * The update-check card under Advanced (spec §7.6): the Check switch and the three kinds, each saved at once.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is state.forms.settings
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
    }) + UPDATE_KINDS.map(kind => {
        const path = `UpdateCheck.${kind.key}`;
        const name = text(S, kind.label);
        return row({
            label: name,
            hint: text(S, kind.hint),
            cueKey: path,
            control: switchControl({
                input: kind.input,
                checked: Boolean(local[kind.key]),
                label: name,
                attrs: { 'data-tgj-field': path, 'data-tgj-save': path },
            }),
        });
    }).join('');
    return card({ title: text(S, 'web.updates.title'), description: text(S, 'settings.updates.hint'), body });
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

    const info = notice({ kind: 'info', html: `<p>${t(S, 'web.diag.contains')}</p><p>${t(S, 'web.diag.never')}</p><p>${t(S, 'web.diag.why')}</p>` });
    return card({ title: text(S, 'web.diag.title'), body: info + parts.join('') });
}

// --- the shell (plan 8, design §5.1-5.2): header, sidebar, one section shown at a time ---

/** The dashboard's sections, in sidebar order; each label is web.nav.<name>. */
export const SECTIONS = ['overview', 'destinations', 'messages', 'advanced', 'log'];
const NAV_LABELS = {
    overview: 'web.nav.overview',
    destinations: 'web.nav.destinations',
    messages: 'web.nav.messages',
    advanced: 'web.nav.advanced',
    log: 'web.nav.log',
};

/** Today's section containers, by the dashboard section that holds them (design §5.2). */
const SECTION_HOSTS = {
    // Connect precedes Status: with no bot it leads (design §5.2), and with a bot it is empty, so Status leads.
    overview: ['tgj-connect', 'tgj-status', 'tgj-bot'],
    destinations: ['tgj-destinations'],
    // Messages holds the templates too (plan 9, design §6.1), and Batching sits in Advanced after Update checks (§7).
    messages: ['tgj-messages'],
    advanced: ['tgj-proxy', 'tgj-updates', 'tgj-batching', 'tgj-manual', 'tgj-diagnostics'],
    // The Log (plan 9, design §8.1) is last: after Advanced in the sidebar, the last tab on a phone.
    log: ['tgj-log'],
};

/** How long the first show waits for tgjelly.css before showing the page unstyled. */
const STYLESHEET_WAIT_MS = 1000;

/** The header's connection label: a label's HTML, a word in an outline and no symbol. */
const connectionHtml = connection => label(connection.text, connection.tone);

/**
 * The release an assembly version stands for: "1.0.4.0" is v0.4 and "2.1.12.0" is v1.12 (build/package.py
 * track_version). A version without four numeric parts shows as it is.
 * @param {string} version the status's Version
 * @returns {string}
 */
export function releaseName(version) {
    const parts = String(version ?? '').split('.');
    return parts.length === 4 && parts.every(part => /^\d+$/.test(part)) ? `v${parts[1]}.${parts[2]}` : `v${version}`;
}

/** The project's GitHub releases; a release name links to its tag there. */
const RELEASES_URL = 'https://github.com/MrSnowball-dev/tgjelly_plugin/releases';

/**
 * The GitHub page for a plugin version: its release, or the releases list when the version has no release name.
 * @param {string} version
 * @returns {string}
 */
export function releaseUrl(version) {
    const name = releaseName(version);
    // v0.0 is a development build, which has no release page.
    return /^v\d+\.\d+$/.test(name) && name !== 'v0.0' ? `${RELEASES_URL}/tag/${name}` : RELEASES_URL;
}

/**
 * The header's version label, a link to its release page in a new tab, with the full version and build target as its
 * tooltip; '' before the version is known.
 */
const versionHtml = (S, version, buildTarget) => (version
    ? `<a class="tgj-version-link" href="${esc(releaseUrl(version))}" target="_blank" rel="noopener noreferrer"`
        + ` title="${esc(text(S, 'web.header.versionTitle', { version, target: buildTarget ?? '' }))}">`
        + `${label(releaseName(version), 'neutral')}</a>`
    : '');

/**
 * The header's connection label for a Status: "Connected · @bot", "Not connected", "Connecting", or a warning when the
 * connection needs attention or the status can't be read.
 * @param {object | null} status the last GET Status body
 * @param {Record<string, string> | null} S
 * @param {string | false} [failed] why the last GET Status failed (plain text), or false
 * @returns {{ tone: 'good' | 'progress' | 'neutral' | 'attention', text: string }} plain text, for label
 */
export function connectionLabel(status, S, failed = false) {
    if (failed || !status) {
        return failed ? { tone: 'attention', text: text(S, 'web.header.unknown', { reason: failed, seconds: POLL_SECONDS }) } : { tone: 'neutral', text: text(S, 'web.header.notConnected') };
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
 * A sidebar count as a label: the edited-templates count in the accent tone (like the Edited badge), others neutral.
 * @param {string} name the section
 * @param {string | undefined} text
 * @returns {string} HTML, or '' without a count
 */
export function navCountHtml(name, text) {
    return text ? label(text, name === 'messages' ? 'accent' : 'neutral') : '';
}

/**
 * Renders the dashboard shell: the header with the connection label, the sidebar of sections with their muted counts,
 * and one section per name holding today's containers. Only the active section is visible. The page fills the
 * containers, the label (#tgj-conn) and the counts (#tgj-nav-count-<name>) afterwards; this is written once.
 * @param {Record<string, string> | null} S
 * @param {{ active: string, connection: { tone: string, text: string }, counts?: Record<string, string>, version?: string }} options
 *   version is the plugin's, painted beside the title; empty until the first status arrives
 *   counts are plain text by section name, e.g. { destinations: '3', messages: '1 edited' }
 * @returns {string}
 */
export function renderShell(S, { active, connection, counts = {}, version = '', buildTarget = '' }) {
    const current = SECTIONS.includes(active) ? active : 'overview';
    const nav = SECTIONS.map(name => `<button type="button" id="tgj-nav-${name}" data-tgj-nav="${name}"`
        + `${name === current ? ' aria-current="page"' : ''}>`
        + `<span class="tgj-nav-label">${t(S, NAV_LABELS[name])}</span>`
        + `<span id="tgj-nav-count-${name}" class="tgj-nav-count">${navCountHtml(name, counts[name])}</span></button>`);
    const sections = SECTIONS.map(name => `<section id="tgj-sec-${name}" data-tgj-section="${name}" aria-labelledby="tgj-nav-${name}"`
        + `${name === current ? '' : ' hidden'}>${SECTION_HOSTS[name].map(id => `<div id="${id}"></div>`).join('')}</section>`);
    return '<div class="tgj-shell">'
        + `<header class="tgj-header"><div class="tgj-header-brand"><h1 class="tgj-header-title">TgJelly</h1><span id="tgj-version">${versionHtml(S, version, buildTarget)}</span></div><span id="tgj-conn">${connectionHtml(connection)}</span></header>`
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

    // Templates became part of Messages (plan 9): an old link to them opens Messages.
    const current = name === 'templates' ? 'messages' : name;
    return SECTIONS.includes(current) ? current : 'overview';
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
        // errors: section name → HTML (escaped) shown under that section; statusFailed: why the last GET Status failed (text), or false.
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
            removeDestinations: false,
            // The destinations section (§6.6.3): the open editor, the remove confirm, and the last test/check outcome per id.
            destEditing: null,
            destConfirm: null,
            destLeave: false,
            destResults: {},
            // The manual block (§6.5): the typed chat ID or link, and the last add's outcome (HTML).
            manualInput: '',
            manualResult: '',
            // Diagnostics: the text when the clipboard refused it, and whether the last copy worked.
            diagText: '',
            diagCopied: false,
            // The Messages section adds its own (tgjelly-messages.js, createMessages).
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
    const loggedLoadFailures = new Map();
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
                throw new Error(httpFailure(result));
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
                throw new Error(httpFailure(result));
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
        if (list.status !== 200 || !list.body) {
            throw new Error(httpFailure(list));
        }

        if (libraries.status !== 200 || !Array.isArray(libraries.body)) {
            throw new Error(httpFailure(libraries));
        }

        state.libraries = libraries.body;
        takeDestinations(list.body);
    }

    // The Messages section (design §6.1-6.2) lives in tgjelly-messages.js. It gets the page's state and save machinery;
    // the functions are looked up when called, so ones defined further down are fine.
    const messages = createMessages({
        view,
        state,
        api,
        byId,
        lang,
        forget: id => rendered.delete(id),
        renderSection: name => renderSection(name),
        reloadSection: (name, options) => reloadSection(name, options),
        runAction: (name, fn) => runAction(name, fn),
        markDirty: (name, path, value) => markDirty(name, path, value),
        syncForm: (name, data) => syncForm(name, data),
        noteRevision,
        isOlder,
        pageRevision,
        queueSave: job => queueSave(job),
        saveResult: (result, on) => saveResult(result, on),
        failureText: result => failureText(result),
        releaseFields: key => releaseFields(key),
        flush: key => autosave.flush(key),
        cancelSave: key => autosave.cancel(key),
        loadSettings: () => loadSettings(),
        saveSetting: (path, mode, el, options) => saveSetting(path, mode, el, options),
        requestError,
        thrownError,
        thrownText,
        healthText,
        fieldSelector,
    });

    // The Log section (design §8.1) lives in tgjelly-log.js. It loads only while it is shown (selectSection, the poll).
    const logSection = createLog({
        state,
        api,
        lang,
        renderSection: name => renderSection(name),
        reloadSection: (name, options) => reloadSection(name, options),
        runAction: (name, fn) => runAction(name, fn),
    });

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
                        failure = statusReason(state.strings, result);
                    }
                } catch (err) {
                    failure = errorReason(err);
                }

                if (failure && failure !== state.ui.statusFailed) {
                    console.error('TgJelly: could not load the status', failure);
                }

                state.ui.statusFailed = failure ?? false;
            },
            render: () => {
                const S = state.strings;
                const html = state.status ? renderStatus(state.status, S, lang()) : '';
                return state.ui.statusFailed ? html + notice({ kind: 'error', html: t(S, 'web.status.failed', { reason: state.ui.statusFailed, seconds: POLL_SECONDS }) }) : html;
            },
        },
        connect: {
            id: 'tgj-connect',
            dependsOnStatus: true,
            load: async () => {},
            // While a connected bot's token is being changed, the form sits in the Bot card instead (see bot).
            render: () => (state.status && !(state.status.HasToken && state.ui.changingToken)
                ? renderConnect(connectUi(), state.strings, state.links, state.settings)
                : ''),
        },
        bot: {
            id: 'tgj-bot',
            dependsOnStatus: true,
            load: loadLinks,
            render: () => renderBot(state.status, state.links, state.strings, state.ui,
                state.ui.changingToken ? renderConnect(connectUi(), state.strings, state.links, state.settings, { bare: true }) : ''),
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
            }),
        },
        // Reads the destinations state (same data as the Destinations section), so it has nothing to load itself.
        manual: {
            id: 'tgj-manual',
            load: async () => {},
            render: () => renderManual(state.destinations, state.strings, {
                input: state.ui.manualInput,
                result: state.ui.manualResult,
            }),
        },
        // The settings and the templates (tgjelly-messages.js).
        messages: messages.section,
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
        log: logSection.section,
    };

    /** The sidebar counts, plain text by section: the destinations, and how many templates are edited (under Messages). */
    const navCounts = () => {
        const S = state.strings;
        const counts = {};
        const destinationCount = state.destinations ? (state.destinations.Items ?? []).length : 0;
        if (destinationCount > 0) {
            counts.destinations = formatNumber(destinationCount, lang());
        }

        const edited = state.templates ? (state.templates.Items ?? []).filter(item => item.Edited).length : 0;
        if (edited > 0) {
            counts.messages = pluralText(S, 'web.nav.edited', edited, {}, lang());
        }

        return counts;
    };

    const connection = () => connectionLabel(state.status, state.strings, state.ui.statusFailed);
    const pluginVersion = () => (state.status && state.status.Version) || '';
    const pluginTarget = () => (state.status && state.status.BuildTarget) || '';

    /**
     * Shows one section and marks its sidebar button; with { remember: true } (the admin chose it) also records it in the
     * URL hash, and brings the Log up to date when it is the one opened.
     */
    const selectSection = (name, { remember = false } = {}) => {
        if (!SECTIONS.includes(name)) {
            return;
        }

        const previous = activeSection;
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

        // The Log loads only while it is shown: opening it brings it up to date at once, then the poll keeps it so. (On
        // the first show, show() loads it.)
        if (remember && name === 'log' && previous !== 'log') {
            reloadSection('log');
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
        root.innerHTML = renderShell(state.strings, { active: first, connection: connection(), counts: navCounts(), version: pluginVersion(), buildTarget: pluginTarget() });
        shellDrawn = true;
        selectSection(first);
    };

    /** Brings the header label and the sidebar counts up to date (each written only when it changed). */
    const renderChrome = () => {
        if (!shellDrawn) {
            return;
        }

        setHtml('tgj-conn', connectionHtml(connection()));
        setHtml('tgj-version', versionHtml(state.strings, pluginVersion(), pluginTarget()));
        const counts = navCounts();
        for (const name of SECTIONS) {
            setHtml(`tgj-nav-count-${name}`, navCountHtml(name, counts[name]));
        }
    };

    const renderSection = name => {
        drawShell();
        const section = sections[name];
        const host = byId(section.id);
        // While the change-token form sits in the Bot card, its errors show there too, not above Status.
        const tokenInBot = state.status && state.status.HasToken && state.ui.changingToken;
        const error = name === 'connect' && tokenInBot ? '' : sectionError(name) + (name === 'bot' && tokenInBot ? sectionError('connect') : '');
        const write = () => setHtml(section.id, section.render() + error);
        // While the write runs, a focused field it replaces may fire focusout: that is not the admin leaving the field.
        // A section with its own write (the Log) keeps its place around it.
        writing++;
        try {
            keepFocus(host, section.write ? () => section.write(host, write) : write, { pending: pendingFields });
        } finally {
            writing--;
        }

        paintCues();
        renderChrome();

        if (name === 'messages') {
            messages.placePreview();
        }

        if (name === 'destinations') {
            // The manual block shows the same data (detected chats, the gate), so it follows every Destinations render;
            // the Messages section lists the destinations for its test send, and the Log's filter offers them.
            renderSection('manual');
            renderSection('messages');
            renderSection('log');
        }

        // The change-token form lives in the Bot card, so a connect re-render must reach it there too.
        if (name === 'connect' && state.status && state.status.HasToken) {
            renderSection('bot');
        }

        if (name === 'proxy' || name === 'connect' || name === 'bot') {
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
                    loggedLoadFailures.delete(name);
                    if (loadErrors.delete(name)) {
                        delete state.ui.errors[name];
                    }

                    renderSection(name);
                } catch (err) {
                    // One diagnostic per distinct failure: a failing poll would otherwise log every 3 s.
                    const reason = errorReason(err);
                    if (loggedLoadFailures.get(name) !== reason) {
                        loggedLoadFailures.set(name, reason);
                        console.error(`TgJelly: the ${name} section failed to load or render`, err);
                    }

                    loadErrors.add(name);
                    const S = state.strings;
                    state.ui.errors[name] = t(S, 'web.loadFailed', { reason, seconds: POLL_SECONDS });
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

    // The sections that hold a copy of the one settings Revision (spec §10.4), by the state their load fills. Messages
    // loads both the settings and the templates.
    const REVISION_SECTIONS = { settings: ['proxy', 'messages', 'batching', 'updates'], destinations: ['destinations'], templates: ['messages'] };
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
        const behind = new Set(Object.entries(REVISION_SECTIONS)
            .filter(([key]) => state[key] && state[key].Revision < newest)
            .flatMap(([, names]) => names));
        await Promise.all([...behind].map(name => reloadSection(name, { fresh: true })));
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
            state.ui.errors[name] = thrownError(S, err);
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
        state.ui.removeDestinations = false;
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
        describeError: err => (isNetworkError(err)
            ? text(state.strings, 'web.autosave.network', { reason: errorReason(err) })
            : text(state.strings, 'web.unexpectedError', { reason: errorReason(err) })),
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
    const failureText = result => failureReason(state.strings, result);

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
            return { status: 'failed', message: text(S, 'web.request.noSettings') };
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

    /** Queues the save of one settings field; its save and cue key is its dotted path unless key is given. */
    const saveSetting = (path, mode, el, { delay, validate, key = path } = {}) => queueSave({
        key,
        mode,
        delay,
        fields: el ? [fieldSelector(el)] : [],
        validate,
        send: () => sendSettings([path]),
    });

    /** Typed text and numbers save this long after the last keystroke (design §6.1). */
    const TYPING_SAVE_MS = 800;

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
            return { status: 'failed', message: text(state.strings, 'web.request.noSettings') };
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
            return { status: 'failed', message: text(state.strings, 'web.request.noDestination') };
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

    /** Queues the save of one destination control (switches save at once); field is its fieldSelector. */
    const saveDestination = (id, what, field) => queueSave({
        key: destSaveKey(id, what),
        mode: 'now',
        fields: [field],
        send: () => sendDestination(id),
    });

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
                const result = await api.del(`Token?removeDestinations=${state.ui.removeDestinations}`);
                if (result.status >= 300) {
                    return result;
                }

                Object.assign(state.ui, { confirmDisconnect: false, removeDestinations: false, result: null, mode: null, changingToken: false });
                await Promise.all([reloadSection('status', { fresh: true }), reloadSection('bot', { fresh: true }), reloadSection('destinations', { fresh: true })]);
                return result;
            } finally {
                state.ui.saving = false;
            }
        }),
        edit: el => {
            const id = idOf(el);
            state.ui.destEditing = state.ui.destEditing === id ? null : id;
            state.ui.destConfirm = null;
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
            return manualReply(await api.post('Destinations', { ExpectedRevision: pageRevision(), ...target }));
        }),
        detectedSetUp: el => runAction('manual', async () => {
            const id = detectedId(el);
            if (id === null || !state.destinations) {
                return undefined;
            }

            state.ui.manualResult = '';
            await autosave.flush();
            return manualReply(await api.post(`DetectedChats/${id}`, { ExpectedRevision: pageRevision() }));
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
        destEvent: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const key = el.getAttribute('data-tgj-key');
            const keys = state.destinations ? state.destinations.Catalog.EventKeys : [];
            if (!form || event.type !== 'click' || !keys.includes(key)) {
                return;
            }

            const chosen = toggled(form.local.Events, key, !form.local.Events.includes(key));
            editDestination(id, { Events: keys.filter(k => chosen.includes(k)) });
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
        // Part of the disconnect question: it saves nothing.
        disconnectRemoveDestinations: (el, event) => {
            if (event.type === 'click') {
                state.ui.removeDestinations = !state.ui.removeDestinations;
                renderSection('bot');
            }
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
        ...Object.fromEntries(UPDATE_KINDS.map(kind => [kind.input, (el, event) => {
            if (event.type === 'click') {
                const path = `UpdateCheck.${kind.key}`;
                markDirty('settings', path, flipped(path));
                renderSection('updates');
                saveSetting(path, 'now', el);
            }
        }])),
    };

    // The Messages section's own clicks and inputs (tgjelly-messages.js).
    Object.assign(actions, messages.actions);
    Object.assign(inputs, messages.inputs);
    // The Log section's (tgjelly-log.js).
    Object.assign(actions, logSection.actions);
    Object.assign(inputs, logSection.inputs);

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

        // The Log refreshes with the poll, only while it is shown.
        if (!document.hidden && activeSection === 'log' && !inFlight.has('log')) {
            reloadSection('log');
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
                throw new Error(httpFailure(result));
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
                root.textContent = `Could not load TgJelly: ${errorReason(err)}. Reload the page; if it keeps failing, check the Jellyfin log.`;
            }

            return false;
        }

        return true;
    };

    // The preview follows the page's scrolling, which happens outside this view (Jellyfin's own scroller or the
    // window), so it listens on the document while the page is shown.
    const scrollDoc = view.ownerDocument;
    const scrollWin = scrollDoc && scrollDoc.defaultView;
    let placing = false;
    const onPageScroll = () => {
        if (placing) {
            return;
        }

        placing = true;
        const run = () => {
            placing = false;
            messages.placePreview();
        };
        if (scrollWin && typeof scrollWin.requestAnimationFrame === 'function') {
            scrollWin.requestAnimationFrame(run);
        } else {
            run();
        }
    };

    const show = async () => {
        visible = true;
        scrollDoc?.addEventListener?.('scroll', onPageScroll, { capture: true, passive: true });
        scrollWin?.addEventListener?.('resize', onPageScroll, { passive: true });
        stopPolling();
        stylesheet ??= waitForStylesheet(byId('tgjelly-css'));
        const [ready] = await Promise.all([ensureStrings(), stylesheet]);
        if (!ready) {
            return;
        }

        startPolling();
        await reloadSection('status');
        // The Log loads only when it is the section shown (selectSection loads it when the admin opens it).
        await Promise.all(Object.keys(sections).filter(name => name !== 'status' && (name !== 'log' || activeSection === 'log')).map(name => reloadSection(name)));
        syncedRevision = Math.max(syncedRevision, newestRevision());
    };

    const hide = () => {
        visible = false;
        scrollDoc?.removeEventListener?.('scroll', onPageScroll, { capture: true });
        scrollWin?.removeEventListener?.('resize', onPageScroll);
        stopPolling();
        messages.hide();
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

        // A quick, repeatable action (inserting into the template, opening a table) never disables its button: that
        // would take the keyboard focus from it.
        if (found.el.getAttribute('data-tgj-instant') !== null) {
            found.run().catch(logFailure);
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

    // Pressing a field or format cell of the template tables must not take the focus (and the caret) from the textarea.
    view.addEventListener('mousedown', event => {
        if (event.target && event.target.closest && event.target.closest('[data-tgj-insert]')) {
            event.preventDefault();
        }
    });

    // The hover link between the template and its preview (design §6.5) follows the pointer; its mirror follows the
    // textarea's scrolling (scroll events don't bubble, so this listens on the way down).
    for (const type of ['mouseover', 'mousemove', 'mouseout']) {
        view.addEventListener(type, messages.hover);
    }

    view.addEventListener('scroll', messages.scroll, true);

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
