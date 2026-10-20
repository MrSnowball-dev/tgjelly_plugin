// TgJelly dashboard page (spec §11.1). Loaded through data-controller="__plugin/tgjelly.js".
//
// Layout of this module:
// - pure helpers and render functions are named exports, so tests/web can call them without a DOM;
// - every render function returns HTML built only from esc()/t()/plural() output, so server text is always escaped;
// - the default export is the page controller: it owns the state, the loaders, the click actions and the Status poller.
// The strings table is always called S, so the key check in tests/web can find the key at every t() and plural() call.

const POLL_MS = 3000;

/** Connection states shown in the error color (the ConnectionState names from GET Status). */
const PROBLEM_STATES = new Set(['TokenRejected', 'WebhookConflict', 'ConflictingInstance', 'Unreachable', 'InvalidBaseUrl', 'Incompatible']);

const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

/**
 * Escapes a value for HTML text and attribute values. null and undefined give ''.
 * @param {unknown} value
 * @returns {string}
 */
export function esc(value) {
    return value === null || value === undefined ? '' : String(value).replace(/[&<>"']/g, c => ESCAPES[c]);
}

/**
 * Looks up a string and fills its {name} placeholders. The result is HTML: the text and the args are both escaped.
 * A missing key gives the key itself; a placeholder without an arg stays as it is.
 * @param {Record<string, string> | null} S the string table from GET Strings
 * @param {string} key
 * @param {Record<string, unknown>} [args]
 * @returns {string}
 */
export function t(S, key, args = {}) {
    const text = S && Object.hasOwn(S, key) ? String(S[key]) : key;
    let html = '';
    let last = 0;
    for (const match of text.matchAll(/\{(\w+)\}/g)) {
        html += esc(text.slice(last, match.index)) + esc(Object.hasOwn(args, match[1]) ? args[match[1]] : match[0]);
        last = match.index + match[0].length;
    }

    return html + esc(text.slice(last));
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
    let category;
    try {
        category = new Intl.PluralRules(lang).select(count);
    } catch {
        category = new Intl.PluralRules('en').select(count);
    }

    const chosen = S && Object.hasOwn(S, `${key}.${category}`) ? `${key}.${category}` : `${key}.other`;
    return t(S, chosen, { ...args, count: formatNumber(count, lang) });
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
    const parts = [`<h2 class="sectionTitle">${t(S, 'web.status.title')}</h2>`];

    if (!status.Compatible) {
        parts.push(`<p class="tgj-compat tgj-error">${esc(status.CompatMessage)}</p>`);
    } else {
        const problem = PROBLEM_STATES.has(status.ConnectionState) ? ' class="tgj-error"' : '';
        parts.push(`<p${problem}>${esc(status.ConnectionMessage)}</p>`);
        if (status.ConnectionState === 'WebhookConflict') {
            parts.push('<button is="emby-button" type="button" class="raised button-submit emby-button" data-tgj-act="takeover">'
                + `<span>${t(S, 'web.status.takeOver')}</span></button>`);
        }
    }

    parts.push(`<p class="fieldDescription">${t(S, 'web.status.counts', {
        pending: formatNumber(status.PendingMedia ?? 0, lang),
        outbox: formatNumber(status.OutboxCount ?? 0, lang),
        dropped: formatNumber(dropped, lang),
    })}</p>`);

    for (const notice of status.Notices ?? []) {
        parts.push('<div class="tgj-notice">'
            + `<span class="tgj-notice-text">${esc(notice.Text)}</span>`
            + `<button is="emby-button" type="button" class="raised emby-button" data-tgj-act="dismiss" data-tgj-id="${esc(notice.Id)}">`
            + `<span>${t(S, 'web.status.dismiss')}</span></button></div>`);
    }

    const errors = status.LastErrors ?? [];
    if (errors.length > 0) {
        const items = [...errors].reverse().map(e => `<li>${t(S, 'web.status.error', {
            time: formatTime(e.At, lang),
            source: e.Source,
            message: e.Message,
        })}</li>`);
        parts.push(`<h3>${t(S, 'web.status.errors')}</h3><ul class="tgj-errors fieldDescription">${items.join('')}</ul>`);
    }

    parts.push(`<p class="fieldDescription">${t(S, 'web.status.version', { version: status.Version, target: status.BuildTarget })}</p>`);
    return `<div class="verticalSection">${parts.join('')}</div>`;
}

const BUTTON = 'is="emby-button" type="button"';

/** A button for a data-tgj-act action; disabled while a request is in flight. */
function button(label, act, { primary = false, disabled = false } = {}) {
    return `<button ${BUTTON} class="raised${primary ? ' button-submit' : ''} emby-button" data-tgj-act="${act}"`
        + `${disabled ? ' disabled' : ''}><span>${label}</span></button>`;
}

/** A link to Telegram, or '' when the URL isn't one the server should have sent. */
function telegramLink(url, label) {
    return typeof url === 'string' && url.startsWith('https://t.me/')
        ? `<a is="emby-linkbutton" class="raised emby-button" href="${esc(url)}" target="_blank" rel="noopener noreferrer"><span>${label}</span></a>`
        : '';
}

/**
 * The proxy form (spec §6.6 item 5, §9.1): used under Advanced and in the "Can't reach Telegram?" panel.
 * @param {object | null} settings the SettingsDto (null until it loads)
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object, changed: string[] }, proxyErrors?: string[], saving?: boolean }} ui form is the
 *   editor state: local.Mode and local.Url, plus Username and Password only once the user typed in them.
 * @returns {string} HTML
 */
export function renderProxyForm(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local;
    const changed = path => (ui.form.changed.includes(path) ? ' tgj-changed' : '');
    const modes = [
        ['System', t(S, 'web.proxy.modeSystem')],
        ['None', t(S, 'web.proxy.modeNone')],
        ['Http', t(S, 'web.proxy.modeHttp')],
        ['Socks5', t(S, 'web.proxy.modeSocks5')],
    ];
    const options = modes.map(([value, label]) => `<option value="${value}"${local.Mode === value ? ' selected' : ''}>${label}</option>`);
    const field = (name, path, label, saved, type) => `<div class="inputContainer${changed(path)}">`
        + `<input is="emby-input" type="${type}" label="${label}" autocomplete="off" data-tgj-input="${name}"`
        + `${saved ? ` placeholder="${t(S, 'web.proxy.saved')}"` : ''} value="${esc(local[path] ?? '')}" /></div>`;
    const withAddress = local.Mode === 'Http' || local.Mode === 'Socks5';
    const parts = [
        `<h3>${t(S, 'web.proxy.title')}</h3>`,
        `<div class="selectContainer${changed('Mode')}"><select is="emby-select" class="emby-select-withcolor emby-select" `
        + `label="${t(S, 'web.proxy.mode')}" data-tgj-input="proxyMode">${options.join('')}</select></div>`,
    ];
    if (withAddress) {
        parts.push(field('proxyUrl', 'Url', t(S, 'web.proxy.url'), false, 'text'));
    }

    parts.push(field('proxyUsername', 'Username', t(S, 'web.proxy.username'), settings.HasProxyUsername, 'text'));
    parts.push(field('proxyPassword', 'Password', t(S, 'web.proxy.password'), settings.HasProxyPassword, 'password'));

    for (const message of ui.proxyErrors ?? []) {
        parts.push(`<p class="tgj-error">${message}</p>`);
    }

    parts.push(button(t(S, 'web.proxy.save'), 'saveProxy', { primary: true, disabled: ui.saving }));
    return `<div class="verticalSection tgj-proxy-form">${parts.join('')}</div>`;
}

/**
 * The token form (spec §6.1): a password input that is never pre-filled, the two notes for an existing bot, the
 * profile checkbox and Save.
 */
function renderTokenForm(ui, S) {
    const busy = ui.saving ? ' disabled' : '';
    const notes = ui.mode === 'existing'
        ? `<p class="fieldDescription">${t(S, 'web.connect.noteRestrict')}</p><p class="fieldDescription">${t(S, 'web.connect.noteOther')}</p>`
        : '';
    return '<div class="tgj-token-form">'
        + `<div class="inputContainer"><input is="emby-input" type="password" label="${t(S, 'web.connect.tokenLabel')}" `
        + `autocomplete="off" data-tgj-input="token"${busy} /></div>`
        + notes
        + '<label class="emby-checkbox-label"><input is="emby-checkbox" type="checkbox" data-tgj-input="setProfile"'
        + `${ui.setProfile ? ' checked' : ''}${busy} /><span>${t(S, 'web.connect.setProfile')}</span></label>`
        + `<div>${button(t(S, 'web.connect.save'), 'saveToken', { primary: true, disabled: ui.saving })}`
        + `${ui.hasToken ? button(t(S, 'web.connect.cancel'), 'cancelToken', { disabled: ui.saving }) : ''}</div></div>`;
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
    const busy = ui.saving;
    if (result && result.kind === 'Connected') {
        return `<div class="verticalSection"><p>${t(S, 'web.connect.connected', { bot: result.username })}</p>`
            + (result.webhookDeleted ? `<p class="fieldDescription">${t(S, 'web.connect.webhookRemoved')}</p>` : '')
            + `${telegramLink(links && links.OpenBot, t(S, 'web.connect.open', { bot: result.username }))}</div>`;
    }

    if (ui.hasToken && !ui.changingToken) {
        return '';
    }

    const parts = [`<h2 class="sectionTitle">${t(S, 'web.connect.title')}</h2>`];
    if (result && result.kind === 'NeedsConfirm') {
        parts.push(`<p>${esc(result.message)}</p>`
            + `<div>${button(t(S, 'web.connect.switch'), 'switchBot', { primary: true, disabled: busy })}`
            + `${button(t(S, 'web.connect.cancel'), 'cancelSwitch', { disabled: busy })}</div>`);
        return `<div class="verticalSection">${parts.join('')}</div>`;
    }

    if (!ui.mode) {
        parts.push(`<div>${button(t(S, 'web.connect.createNew'), 'connectNew', { primary: true, disabled: busy })}`
            + `${button(t(S, 'web.connect.haveBot'), 'connectExisting', { disabled: busy })}`
            + `${ui.hasToken ? button(t(S, 'web.connect.cancel'), 'cancelToken', { disabled: busy }) : ''}</div>`);
    } else {
        if (ui.mode === 'new') {
            parts.push('<ol class="tgj-steps">'
                + `<li>${t(S, 'web.connect.step1')} ${telegramLink('https://t.me/BotFather', t(S, 'web.connect.openBotFather'))}</li>`
                + `<li>${t(S, 'web.connect.step2')}</li><li>${t(S, 'web.connect.step3')}</li></ol>`);
        }

        parts.push(renderTokenForm(ui, S));
    }

    if (result && result.kind === 'Error') {
        parts.push(`<p class="tgj-error">${esc(result.message)}</p>`);
    }

    if (ui.unreachable) {
        parts.push(`<div class="tgj-unreachable"><h3>${t(S, 'web.connect.unreachableTitle')}</h3>`
            + `<p class="fieldDescription">${t(S, 'web.connect.unreachableHint')}</p>${renderProxyForm(settings, S, ui.proxy ?? {})}</div>`);
    }

    return `<div class="verticalSection">${parts.join('')}</div>`;
}

/**
 * The Bot section (spec §6.6 item 2, §6.4): the username, the access reminder, Change token and Disconnect bot with
 * its inline two-step confirm.
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

    const busy = ui.saving;
    const bot = status.BotUsername;
    const disconnect = ui.confirmDisconnect
        ? `<p>${t(S, 'web.bot.confirmDisconnect')}</p>${button(t(S, 'web.bot.confirmYes'), 'disconnectYes', { primary: true, disabled: busy })}`
            + button(t(S, 'web.connect.cancel'), 'disconnectNo', { disabled: busy })
        : button(t(S, 'web.bot.disconnect'), 'disconnect', { disabled: busy });
    return '<div class="verticalSection">'
        + `<h2 class="sectionTitle">${t(S, 'web.bot.title')}</h2>`
        + `<p>@${esc(bot)}</p>`
        + `<p class="fieldDescription">${t(S, 'web.bot.access', { bot })}</p>`
        + `<div>${telegramLink(links && links.OpenBot, t(S, 'web.connect.open', { bot }))}`
        + `${button(t(S, 'web.bot.change'), 'changeToken', { disabled: busy })}${disconnect}</div></div>`;
}

/** The proxy editor's data from a SettingsDto. Username and Password are absent until the user types in them. */
export function proxyFormData(settings) {
    return { Mode: settings.ProxyMode, Url: settings.ProxyUrl };
}


/**
 * The page controller (spec §11.1). Listeners are bound once, here; viewshow is idempotent.
 *
 * Extension points for the page sections (Tasks 8-12 add their entries in the marked places below):
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
            // The proxy form (§9.1): its own messages and in-flight flag.
            proxyErrors: [],
            proxySaving: false,
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
    const typed = { token: '' };
    // Section load errors (set by reloadSection), so a later good load can clear them without touching action errors.
    const loadErrors = new Set();

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
    const proxyUi = () => ({
        form: state.forms.proxy,
        proxyErrors: state.ui.proxyErrors,
        saving: state.ui.proxySaving,
    });

    /** Takes a SettingsDto into the shared settings state and its editor forms (keeping unsaved edits). */
    const takeSettings = dto => {
        state.settings = dto;
        syncForm('proxy', proxyFormData(dto));
    };

    /** Loads GET Settings, the one settings model every settings section shares. */
    async function loadSettings() {
        const result = await api.get('Settings');
        if (result.status !== 200 || !result.body) {
            throw new Error('HTTP ' + result.status);
        }

        takeSettings(result.body);
    }

    async function loadLinks() {
        const result = await api.get('Links');
        if (result.status !== 200 || !result.body) {
            throw new Error('HTTP ' + result.status);
        }

        state.links = result.body;
    }

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
                return state.ui.statusFailed ? html + `<p class="tgj-error">${t(S, 'web.status.failed')}</p>` : html;
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
        // Tasks 9-12: add sections here, in page order.
    };

    const renderSection = name => {
        const section = sections[name];
        setHtml(section.id, section.render() + sectionError(name));
    };

    /** Sections that show state derived from Status (whether a token is stored) render again after it changes. */
    const renderDependents = () => {
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
                }
            })().finally(() => inFlight.delete(name));
            inFlight.set(name, pending);
        }

        return pending;
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
            ui.result = { kind: 'NeedsConfirm', message };
            return undefined;
        }

        typed.token = '';
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
    };

    /** Saves the proxy form (spec §9.1, §10.4): an untouched username/password is omitted, a cleared one is "". */
    const saveProxy = async () => {
        const ui = state.ui;
        const form = state.forms.proxy;
        if (!form || !state.settings) {
            return undefined;
        }

        const local = form.local;
        const withAddress = local.Mode === 'Http' || local.Mode === 'Socks5';
        const body = { ExpectedRevision: state.settings.Revision, Mode: local.Mode, Url: withAddress ? String(local.Url ?? '').trim() : '' };
        for (const field of ['Username', 'Password']) {
            if (typeof local[field] === 'string') {
                body[field] = local[field];
            }
        }

        ui.proxySaving = true;
        ui.proxyErrors = [];
        renderProxyForms();
        let result;
        try {
            result = await api.put('Proxy', body);
        } finally {
            ui.proxySaving = false;
        }

        const S = state.strings;
        if (result.status === 200 && result.body) {
            state.settings = result.body;
            resetForm('proxy', proxyFormData(result.body));
        } else if (result.status === 409 && result.body && result.body.Current) {
            takeSettings(result.body.Current);
            ui.proxyErrors = [t(S, 'web.proxy.conflict')];
        } else if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
            ui.proxyErrors = result.body.Errors.map(e => esc(e.Message));
        } else {
            ui.proxyErrors = [requestError(S, result)];
        }

        renderProxyForms();
        return undefined;
    };

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
        saveProxy: () => runAction('proxy', saveProxy),
        // Tasks 9-12: add click actions here.
    };

    const inputs = {
        token: el => {
            typed.token = String(el.value ?? '').trim();
        },
        setProfile: el => {
            state.ui.setProfile = Boolean(el.checked);
        },
        proxyMode: (el, event) => {
            markDirty('proxy', 'Mode', el.value);
            if (event.type === 'change') {
                renderProxyForms();
            }
        },
        proxyUrl: el => markDirty('proxy', 'Url', el.value),
        proxyUsername: el => markDirty('proxy', 'Username', el.value),
        proxyPassword: el => markDirty('proxy', 'Password', el.value),
        // Tasks 9-12: add input/change handlers here.
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
            const el = byId('tgj-status');
            if (el) {
                el.textContent = 'Could not load TgJelly.';
                rendered.delete('tgj-status');
            }

            return false;
        }

        const S = state.strings;
        setHtml('tgj-advanced-title', t(S, 'web.advanced'));
        const advanced = byId('tgj-advanced');
        if (advanced) {
            advanced.hidden = false;
        }

        return true;
    };

    const show = async () => {
        visible = true;
        stopPolling();
        if (!(await ensureStrings())) {
            return;
        }

        startPolling();
        await reloadSection('status');
        await Promise.all(Object.keys(sections).filter(name => name !== 'status').map(name => reloadSection(name)));
    };

    const hide = () => {
        visible = false;
        stopPolling();
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

    return { api, state, sections, actions, inputs, reloadSection, runAction, syncForm, markDirty, resetForm };
}
