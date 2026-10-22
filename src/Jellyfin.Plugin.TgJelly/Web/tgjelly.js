// TgJelly dashboard page (spec §11.1). Loaded through data-controller="__plugin/tgjelly.js".
//
// Layout of this module:
// - pure helpers and render functions are named exports, so tests/web can call them without a DOM;
// - every render function returns HTML built only from esc()/t()/plural() output, so server text is always escaped;
// - the default export is the page controller: it owns the state, the loaders, the click actions and the Status poller.
// The strings table is always called S, so the key check in tests/web can find the key at every t() and plural() call.

const POLL_MS = 3000;
// The template preview validates this long after the last keystroke.
const PREVIEW_DEBOUNCE_MS = 500;

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
function button(label, act, { primary = false, disabled = false, id = null } = {}) {
    return `<button ${BUTTON} class="raised${primary ? ' button-submit' : ''} emby-button" data-tgj-act="${act}"`
        + `${id === null ? '' : ` data-tgj-id="${esc(id)}"`}${disabled ? ' disabled' : ''}><span>${label}</span></button>`;
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
 *   editor state: local.Mode and local.Url. The proxy username and password are not part of it.
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
    // Username and password are never written into the HTML (spec §10.2): the controller puts typed text back into the
    // inputs after a render.
    const field = (name, path, label, saved, type, value) => `<div class="inputContainer${changed(path)}">`
        + `<input is="emby-input" type="${type}" label="${label}" autocomplete="off" data-tgj-input="${name}"`
        + `${saved ? ` placeholder="${t(S, 'web.proxy.saved')}"` : ''}${value === undefined ? '' : ` value="${esc(value)}"`} /></div>`;
    const withAddress = local.Mode === 'Http' || local.Mode === 'Socks5';
    const parts = [
        `<h3>${t(S, 'web.proxy.title')}</h3>`,
        `<div class="selectContainer${changed('Mode')}"><select is="emby-select" class="emby-select-withcolor emby-select" `
        + `label="${t(S, 'web.proxy.mode')}" data-tgj-input="proxyMode">${options.join('')}</select></div>`,
    ];
    if (withAddress) {
        parts.push(field('proxyUrl', 'Url', t(S, 'web.proxy.url'), false, 'text', local.Url ?? ''));
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
        return result.webhookDeleted
            ? `<div class="verticalSection"><p class="fieldDescription">${t(S, 'web.connect.webhookRemoved')}</p></div>`
            : '';
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
        + `<p><b>@${esc(bot)}</b></p>`
        + `<p class="fieldDescription">${t(S, 'web.bot.access')}</p>`
        + `<div>${telegramLink(links && links.OpenBot, t(S, 'web.bot.open'))}`
        + `${button(t(S, 'web.bot.change'), 'changeToken', { disabled: busy })}${disconnect}</div></div>`;
}

/** The proxy editor's data from a SettingsDto. Username and Password are absent until the user types in them. */
export function proxyFormData(settings) {
    return { Mode: settings.ProxyMode, Url: settings.ProxyUrl };
}

const PRESETS = ['Everything', 'NewMedia', 'AdminAlerts', 'Custom'];

/** The editor's data for one destination from the DestinationDto: the fields Save can send. */
export function destinationFormData(dest) {
    return {
        Preset: dest.Preset,
        Events: [...(dest.Events ?? [])],
        LibraryIds: [...(dest.LibraryIds ?? [])],
        SilentGroups: [...(dest.SilentGroups ?? [])],
    };
}

/**
 * The body of the PUT that Save sends: only the fields that differ from base, plus ExpectedRevision (spec §7.2, §10.4).
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

    for (const key of ['LibraryIds', 'SilentGroups']) {
        if (differs(key)) {
            body[key] = local[key];
        }
    }

    return Object.keys(body).length > 1 ? body : null;
}

const libraryKey = id => String(id).replaceAll('-', '').toLowerCase();
const sameLibrary = (a, b) => libraryKey(a) === libraryKey(b);

/** A health badge: the status text, plus a second badge when photos are blocked. */
function healthBadges(health, S) {
    const status = health && health.Status ? health.Status : 'Ok';
    const badge = key => `<span class="tgj-badge">${t(S, key)}</span>`;
    return badge(`dest.health.${status}`) + (health && health.PhotosBlocked && status !== 'PhotosBlocked' ? ' ' + badge('dest.health.PhotosBlocked') : '');
}

/**
 * The editor under a destination's row (spec §7.2, §7.3): preset, event switches by group, libraries, silent groups,
 * and the Save / Send test / Check again / Remove buttons.
 * @param {object} dest the DestinationDto
 * @param {{ EventKeys: string[], Groups: string[] }} catalog
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object, changed: string[] }, confirmRemove?: boolean, leave?: boolean, result?: string }} ui
 *   form defaults to the destination's own values; result is HTML shown under the buttons
 * @returns {string} HTML
 */
export function renderEditor(dest, catalog, libraries, S, ui) {
    const local = (ui.form && ui.form.local) || destinationFormData(dest);
    const changedPaths = (ui.form && ui.form.changed) || [];
    const changed = path => (changedPaths.includes(path) ? ' tgj-changed' : '');
    const id = esc(dest.Id);
    const checkbox = (input, attrs, checked, label) => '<label class="emby-checkbox-label">'
        + `<input is="emby-checkbox" type="checkbox" data-tgj-input="${input}" data-tgj-id="${id}"${attrs}${checked ? ' checked' : ''} />`
        + `<span>${label}</span></label>`;
    const parts = [];

    const options = PRESETS.map(p => `<option value="${p}"${local.Preset === p ? ' selected' : ''}>${t(S, `dest.preset.${p}`)}</option>`);
    parts.push(`<div class="selectContainer${changed('Preset')}"><select is="emby-select" class="emby-select-withcolor emby-select" `
        + `label="${t(S, 'web.dest.preset')}" data-tgj-input="destPreset" data-tgj-id="${id}">${options.join('')}</select></div>`);

    const events = new Set(local.Events);
    parts.push(`<h4>${t(S, 'dest.events.title')}</h4><div class="${changed('Events').trim()}">`);
    for (const group of catalog.Groups) {
        const keys = catalog.EventKeys.filter(k => k.startsWith(group + '.'));
        parts.push(`<h5>${t(S, `groups.${group}`)}</h5>`
            + keys.map(k => checkbox('destEvent', ` data-tgj-key="${esc(k)}"`, events.has(k), t(S, `dest.event.${k}`))).join(''));
    }

    parts.push('</div>');

    const chosen = new Set(local.LibraryIds.map(libraryKey));
    parts.push(`<h4>${t(S, 'dest.libraries.title')}</h4><div class="${changed('LibraryIds').trim()}">`);
    if (dest.LibraryFilterBroken) {
        parts.push(`<p class="tgj-error">${t(S, 'web.dest.librariesRemoved')}</p>`);
    }

    parts.push(libraries.map(l => checkbox('destLibrary', ` data-tgj-lib="${esc(l.Id)}"`, chosen.has(libraryKey(l.Id)), esc(l.Name))).join(''));
    for (const gone of (dest.RemovedLibraryIds ?? []).filter(x => local.LibraryIds.some(y => sameLibrary(x, y)))) {
        parts.push(`<div class="tgj-removed-library"><span>${t(S, 'dest.libraries.removed', { id: gone })}</span> `
            + `<button ${BUTTON} class="raised emby-button" data-tgj-act="removeLibrary" data-tgj-id="${id}" data-tgj-lib="${esc(gone)}">`
            + `<span>${t(S, 'web.dest.removeLibrary')}</span></button></div>`);
    }

    parts.push(`<p class="fieldDescription">${t(S, chosen.size === 0 ? 'dest.libraries.all' : 'dest.libraries.hint')}</p></div>`);

    const silent = new Set(local.SilentGroups);
    parts.push(`<h4>${t(S, 'dest.silent.title')}</h4><div class="${changed('SilentGroups').trim()}">`
        + catalog.Groups.map(g => checkbox('destSilent', ` data-tgj-group="${esc(g)}"`, silent.has(g), t(S, `groups.${g}`))).join('')
        + `<p class="fieldDescription">${t(S, 'dest.silent.hint')}</p></div>`);

    parts.push(`<div>${button(t(S, 'web.dest.save'), 'saveDest', { primary: true, id: dest.Id })}`
        + `${button(t(S, 'dest.btn.test'), 'testDest', { id: dest.Id })}${button(t(S, 'dest.btn.check'), 'checkDest', { id: dest.Id })}`
        + `${ui.confirmRemove ? '' : button(t(S, 'dest.btn.remove'), 'remove', { id: dest.Id })}</div>`);
    if (ui.confirmRemove) {
        parts.push(`<div class="tgj-confirm"><p>${t(S, 'web.dest.confirmRemove', { title: dest.Title })}</p>`
            + checkbox('destLeave', '', ui.leave, t(S, 'web.dest.leave'))
            + `<div>${button(t(S, 'dest.btn.remove'), 'removeYes', { primary: true, id: dest.Id })}${button(t(S, 'dest.btn.cancel'), 'removeNo', { id: dest.Id })}</div></div>`);
    }

    if (ui.result) {
        parts.push(`<p class="fieldDescription">${ui.result}</p>`);
    }

    return `<div class="tgj-editor">${parts.join('')}</div>`;
}

/**
 * The Destinations section (spec §6.6 item 3): one row per destination, its editor when open, Add destination and the
 * "Set up manually" link once the manual block is unlocked (spec §6.5).
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

    const parts = [`<h2 class="sectionTitle">${t(S, 'web.dest.title')}</h2>`];
    if (dto.Items.length === 0) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.dest.empty')}</p>`);
    }

    for (const dest of dto.Items) {
        parts.push('<div class="tgj-destination">'
            + `<div class="tgj-destination-row"><strong>${esc(dest.Title)}</strong> `
            + `<span class="fieldDescription">${t(S, `dest.kind.${dest.Kind}`)}</span> ${healthBadges(dest.Health, S)} `
            + '<label class="emby-checkbox-label">'
            + `<input is="emby-checkbox" type="checkbox" data-tgj-act="enabled" data-tgj-id="${esc(dest.Id)}"${dest.Enabled ? ' checked' : ''} />`
            + `<span>${t(S, 'web.dest.enabled')}</span></label> `
            + `${button(t(S, ui.editing === dest.Id ? 'web.dest.close' : 'web.dest.edit'), 'edit', { id: dest.Id })}</div>`);
        if (ui.editing === dest.Id) {
            parts.push(renderEditor(dest, dto.Catalog, libraries ?? [], S, {
                form: (ui.forms ?? {})[`dest:${dest.Id}`],
                confirmRemove: ui.confirmRemove === dest.Id,
                leave: ui.leave,
                result: (ui.results ?? {})[dest.Id],
            }));
        } else if ((ui.results ?? {})[dest.Id]) {
            parts.push(`<p class="fieldDescription">${ui.results[dest.Id]}</p>`);
        }

        parts.push('</div>');
    }

    parts.push(`<div>${telegramLink(links && links.StartAdd, t(S, 'web.dest.add'))}</div>`);
    if (dto.ManualUnlocked) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.dest.manualHint')} `
            + `<button is="emby-button" type="button" class="button-link" data-tgj-act="open-manual"><strong>${t(S, 'web.dest.manualLink')}</strong></button></p>`);
    }

    return `<div class="verticalSection">${parts.join('')}</div>`;
}


/** A preset select for the manual block; data is the extra attribute text that says which control it is. */
function presetSelect(S, input, selected, extra = '') {
    const options = PRESETS.filter(p => p !== 'Custom').map(p => `<option value="${p}"${selected === p ? ' selected' : ''}>${t(S, `dest.preset.${p}`)}</option>`);
    return `<div class="selectContainer"><select is="emby-select" class="emby-select-withcolor emby-select" label="${t(S, 'web.dest.preset')}" `
        + `data-tgj-input="${input}"${extra}>${options.join('')}</select></div>`;
}

/**
 * The manual block under Advanced (spec §6.5): "Add chat by ID" and the detected chats. Only when the gate is unlocked.
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

    const parts = [`<h2 class="sectionTitle">${t(S, 'web.manual.title')}</h2>`,
        `<div class="inputContainer"><input is="emby-input" type="text" label="${t(S, 'web.manual.label')}" autocomplete="off" `
        + `data-tgj-input="manualInput" value="${esc(ui.input ?? '')}" /></div>`,
        presetSelect(S, 'manualPreset', ui.preset ?? 'Everything'),
        `<div>${button(t(S, 'web.manual.add'), 'manualAdd', { primary: true })}</div>`];
    if (ui.result) {
        parts.push(`<div class="tgj-manual-result">${ui.result}</div>`);
    }

    const detected = dto.DetectedChats ?? [];
    if (detected.length > 0) {
        parts.push(`<h3>${t(S, 'detected.title')}</h3>`);
        for (const chat of detected) {
            const id = String(chat.ChatId);
            parts.push(`<div class="tgj-detected"><p>${t(S, 'detected.line', { title: chat.Title })}</p>`
                + presetSelect(S, 'detectedPreset', (ui.detectedPresets ?? {})[id] ?? 'Everything', ` data-tgj-id="${esc(id)}"`)
                + `<div>${button(t(S, 'web.manual.setUp'), 'detectedSetUp', { primary: true, id })}`
                + `${button(t(S, 'web.manual.dismiss'), 'detectedDismiss', { id })}</div></div>`);
        }
    }

    return `<div class="verticalSection">${parts.join('')}</div>`;
}


const MESSAGE_TOGGLES = ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks', 'AnnounceUpgrades'];
const UPDATE_MODES = ['SameMajor', 'AnyNewer'];
const SAVE_LABELS = { messages: 'web.messages.save', batching: 'web.batching.save', updates: 'web.updates.save' };
const SETTINGS_SECTIONS = ['messages', 'batching', 'updates'];

/** The batching fields with their allowed ranges (spec §10.1) and the strings that label and describe them. */
const BATCHING_FIELDS = [
    { key: 'QuietWindowSeconds', min: 5, max: 3600, label: 'web.batching.quiet.label', hint: 'web.batching.quiet.hint' },
    { key: 'MaxHoldMinutes', min: 1, max: 240, label: 'web.batching.maxHold.label', hint: 'web.batching.maxHold.hint' },
    { key: 'ReleaseQuietSeconds', min: 5, max: 600, label: 'web.batching.releaseQuiet.label', hint: 'web.batching.releaseQuiet.hint' },
    { key: 'DigestThreshold', min: 2, max: 100, label: 'web.batching.digest.label', hint: 'web.batching.digest.hint' },
    { key: 'LateEditHours', min: 0, max: 24, label: 'web.batching.lateEdit.label', hint: 'web.batching.lateEdit.hint' },
    { key: 'ReadyTimeoutMinutes', min: 1, max: 120, label: 'web.batching.readyTimeout.label', hint: 'web.batching.readyTimeout.hint' },
];

/** Which section shows an error for a settings field path ('' when none of the three does). */
function settingsSectionOf(field) {
    if (field === 'PublicUrl' || String(field).startsWith('Message.')) {
        return 'messages';
    }

    return String(field).startsWith('Batching.') ? 'batching' : String(field).startsWith('UpdateCheck.') ? 'updates' : '';
}

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

/** The top-level settings fields each section owns (spec §6.6): the paths its Save sends from the form. */
const SECTION_FIELDS = { messages: ['PublicUrl', 'Message'], batching: ['Batching'], updates: ['UpdateCheck'] };

/** Copies the fields one section owns from source onto a copy of target (both settings form data). */
function overlaySection(target, source, section) {
    const out = structuredClone(target);
    for (const field of SECTION_FIELDS[section]) {
        out[field] = structuredClone(source[field]);
    }

    return out;
}

/**
 * The PUT Settings body for one section's Save: that section's fields from the form's values laid over the saved ones
 * (the form's base), so edits waiting in the other sections are not sent. Also the client-side errors that stop it
 * (a batching value that isn't a whole number). Ranges are checked by the server.
 * @param {{ base: object, local: object }} form
 * @param {string} section messages | batching | updates
 * @returns {{ body: object | null, errors: { Field: string, Html: string }[] }}
 */
export function settingsRequest(form, section, revision, S) {
    const data = overlaySection(form.base, form.local, section);
    const errors = section !== 'batching' ? [] : BATCHING_FIELDS
        .filter(f => !Number.isInteger(data.Batching[f.key]))
        .map(f => ({ Field: `Batching.${f.key}`, Html: t(S, 'web.settings.notNumber') }));
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

/** The shared pieces of the three settings sections: highlight, error lines under a field, and the section's own errors. */
function settingsHelpers(ui, section) {
    const errors = ui.errors ?? [];
    const line = e => `<p class="tgj-error">${e.Html ?? esc(e.Message)}</p>`;
    return {
        changed: path => ((ui.form.changed ?? []).includes(path) ? ' tgj-changed' : ''),
        fieldErrors: path => errors.filter(e => e.Field === path).map(line).join(''),
        // An error for a field this section doesn't show goes under the Save the user pressed.
        general: () => (ui.section === section ? errors.filter(e => settingsSectionOf(e.Field) !== section).map(line).join('') : ''),
        save: S => button(t(S, SAVE_LABELS[section]), 'saveSettings', { primary: true, disabled: ui.saving, id: section }),
    };
}

/**
 * The Messages section (spec §6.6 item 4, §8.3, §8.4): the public URL and the six message toggles.
 * @param {object | null} settings the SettingsDto
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object, changed: string[] }, errors?: { Field: string, Message?: string, Html?: string }[],
 *   saving?: boolean, section?: string }} ui form is state.forms.settings
 * @returns {string} HTML
 */
export function renderMessages(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const h = settingsHelpers(ui, 'messages');
    const local = ui.form.local;
    const url = String(local.PublicUrl ?? '');
    const parts = [`<h2 class="sectionTitle">${t(S, 'web.messages.title')}</h2>`,
        `<div class="inputContainer${h.changed('PublicUrl')}"><input is="emby-input" type="text" label="${t(S, 'web.messages.url')}" autocomplete="off" `
        + `data-tgj-input="publicUrl" data-tgj-field="PublicUrl" value="${esc(url)}" />`
        + `<div class="fieldDescription">${t(S, 'web.messages.urlHint')}</div></div>`,
        h.fieldErrors('PublicUrl')];
    const suggestion = settings.PublicUrlSuggestion;
    if (suggestion && suggestion !== url) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.messages.suggested', { url: suggestion })} <strong>${t(S, 'web.messages.verify')}</strong> `
            + `${button(t(S, 'web.messages.useSuggested'), 'useSuggested')}</p>`);
    }

    // The server judged the saved value; once the user types another one, that verdict no longer applies.
    if (settings.PublicUrl && !settings.PublicUrlValid && url === settings.PublicUrl) {
        parts.push(`<p class="tgj-error">${t(S, 'web.messages.urlInvalid')}</p>`);
    }

    for (const key of MESSAGE_TOGGLES) {
        parts.push(`<label class="emby-checkbox-label${h.changed(`Message.${key}`)}"><input is="emby-checkbox" type="checkbox" data-tgj-input="msgToggle" `
            + `data-tgj-key="${key}" data-tgj-field="Message.${key}"${local.Message && local.Message[key] ? ' checked' : ''} />`
            + `<span>${t(S, `settings.msg.${key}`)}</span></label>${h.fieldErrors(`Message.${key}`)}`);
    }

    parts.push(h.general(), `<div>${h.save(S)}</div>`);
    return `<div class="verticalSection">${parts.join('')}</div>`;
}

/**
 * The batching form under Advanced (spec §10.1): six numeric fields, each with its range in the description.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {object} ui as for renderMessages
 * @returns {string} HTML
 */
export function renderBatching(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const h = settingsHelpers(ui, 'batching');
    const parts = [`<h3>${t(S, 'web.batching.title')}</h3>`];
    for (const f of BATCHING_FIELDS) {
        const path = `Batching.${f.key}`;
        parts.push(`<div class="inputContainer${h.changed(path)}"><input is="emby-input" type="number" label="${t(S, f.label)}" `
            + `min="${f.min}" max="${f.max}" step="1" data-tgj-input="batching" data-tgj-key="${f.key}" data-tgj-field="${path}" `
            + `value="${esc(ui.form.local.Batching[f.key])}" />`
            + `<div class="fieldDescription">${t(S, f.hint, { min: f.min, max: f.max })}</div></div>${h.fieldErrors(path)}`);
    }

    parts.push(h.general(), `<div>${h.save(S)}</div>`);
    return `<div class="verticalSection">${parts.join('')}</div>`;
}

/**
 * The update-check form under Advanced (spec §7.6): the enabled checkbox and the mode.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {object} ui as for renderMessages
 * @returns {string} HTML
 */
export function renderUpdates(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const h = settingsHelpers(ui, 'updates');
    const local = ui.form.local.UpdateCheck;
    const options = UPDATE_MODES.map(mode => `<option value="${mode}"${local.Mode === mode ? ' selected' : ''}>${t(S, `settings.updates.mode.${mode}`)}</option>`);
    const parts = [`<h3>${t(S, 'web.updates.title')}</h3>`,
        `<p class="fieldDescription">${t(S, 'settings.updates.hint')}</p>`,
        `<label class="emby-checkbox-label${h.changed('UpdateCheck.Enabled')}"><input is="emby-checkbox" type="checkbox" data-tgj-input="updEnabled" `
        + `data-tgj-field="UpdateCheck.Enabled"${local.Enabled ? ' checked' : ''} /><span>${t(S, 'settings.updates.enabled')}</span></label>`,
        h.fieldErrors('UpdateCheck.Enabled'),
        `<div class="selectContainer${h.changed('UpdateCheck.Mode')}"><select is="emby-select" class="emby-select-withcolor emby-select" `
        + `label="${t(S, 'web.updates.mode')}" data-tgj-input="updMode" data-tgj-field="UpdateCheck.Mode">${options.join('')}</select></div>`,
        h.fieldErrors('UpdateCheck.Mode'),
        h.general(),
        `<div>${h.save(S)}</div>`];
    return `<div class="verticalSection">${parts.join('')}</div>`;
}

const TEMPLATES_DOCS = 'https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md';

/** The template form data for one item (what Save sends besides the revision). */
export function templateFormData(item) {
    return { Text: String(item.Text ?? '') };
}

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

    return `<details class="tgj-quote"><summary>${nodesHtml(summary)}</summary>${nodesHtml(body)}</details>`;
}

/** The safe preview markup for one closed Telegram element. */
function previewElement(name, attrs, children) {
    const inner = nodesHtml(children);
    if (PREVIEW_SAME.has(name)) {
        return `<${name}>${inner}</${name}>`;
    }

    switch (name) {
        case 'tg-spoiler':
            return `<span class="tgj-spoiler">${inner}</span>`;
        case 'span':
            return String(attrs.class ?? '').trim() === 'tg-spoiler' ? `<span class="tgj-spoiler">${inner}</span>` : inner;
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
 * span.tgj-spoiler; blockquote (an expandable one as details/summary); a as a new-tab link for http:, https: and tg:
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
        parts.push(`<p class="fieldDescription">${esc(w)}</p>`);
    }

    const failed = Boolean(result.RequestError) || (result.Errors ?? []).length > 0;
    const shown = failed ? lastGood : result;
    if (shown) {
        parts.push(`<div class="tgj-tg${failed ? ' tgj-stale' : ''}">`
            + (shown.Poster ? `<div class="tgj-tg-poster">${t(S, 'web.templates.poster')}</div>` : '')
            + `<div class="tgj-tg-text">${previewHtml(shown.Html)}</div>${previewButtons(shown.Buttons)}</div>`);
    }

    if (!result.RequestError && (!failed || result.Length > 0)) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.templates.length', { length: formatNumber(result.Length ?? 0, lang), limit: formatNumber(result.Limit ?? 0, lang) })}</p>`);
    }

    return parts.join('');
}

/** Whether a validation result stops a test send: it had errors, or the request failed. */
const validationFailed = result => Boolean(result && (result.RequestError || (result.Errors ?? []).length > 0));

/** Whether Send test is off: no destination, the last validation failed, or a send or save is in flight. */
export function templateTestBlocked(ui) {
    return (ui.destinations ?? []).length === 0 || validationFailed(ui.result) || Boolean(ui.testing) || Boolean(ui.busy);
}

/**
 * The Message templates section (spec §8.5): every message comes from a template, and this edits the one in use per
 * event, with a live Telegram-like preview, a test send and a reset to the built-in template.
 * @param {object | null} dto the TemplatesDto
 * @param {Record<string, string>} S
 * @param {{ key?: string, forms?: Record<string, { local: object, changed: string[] }>, result?: object | null,
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
    const changed = path => (form && (form.changed ?? []).includes(path) ? ' tgj-changed' : '');
    const options = dto.Items.map(i => `<option value="${esc(i.EventKey)}"${i === item ? ' selected' : ''}>${i.Edited ? '● ' : ''}${esc(i.Label)}</option>`);
    const parts = [`<h2 class="sectionTitle">${t(S, 'web.templates.title')}</h2>`,
        `<p class="fieldDescription">${t(S, 'web.templates.intro')}</p>`,
        `<div class="tgj-tpl-head"><div class="selectContainer"><select is="emby-select" class="emby-select-withcolor emby-select" `
        + `label="${t(S, 'web.templates.event')}" data-tgj-input="tplEvent">${options.join('')}</select></div>`
        + `<span class="tgj-badge">${item.Edited ? t(S, 'web.templates.edited') : t(S, 'web.templates.builtIn')}</span></div>`];
    if (item.Flag) {
        // A truncated template was sent, only shortened; any other flag means the built-in template was sent instead.
        parts.push(item.Flag.Kind === 'truncated'
            ? `<p class="tgj-error">${t(S, 'web.templates.flagTruncated')}</p>`
            : `<p class="tgj-error">${t(S, 'web.templates.flagError')} ${esc(item.Flag.Error)}</p>`);
    }

    const rows = (item.Fields ?? []).map(f => `<tr><td><code>${esc(f.Name)}</code></td><td>${esc(f.Type)}</td>`
        + `<td>${f.Optional ? t(S, 'web.templates.optional') : ''}</td><td>${esc(f.Description)}</td></tr>`);
    parts.push(`<div class="tgj-tpl-grid"><div class="tgj-tpl-edit">`
        + `<div class="${changed('Text').trim()}"><textarea id="tgj-tpl-text" class="tgj-template-text" data-tgj-input="tplText" rows="12" spellcheck="false">${esc(local.Text)}</textarea></div>`
        + `<details><summary>${t(S, 'web.templates.fields')}</summary><table class="tgj-template-fields">${rows.join('')}</table></details>`
        + `<p class="fieldDescription"><a is="emby-linkbutton" href="${TEMPLATES_DOCS}" target="_blank" rel="noopener noreferrer">${t(S, 'web.templates.docs')}</a></p></div>`
        + `<div id="tgj-tpl-preview" class="tgj-tpl-preview">${renderTemplatePreview(ui.result ?? null, S, lang, ui.good ?? null)}</div></div>`);

    const destinations = ui.destinations ?? [];
    const chosen = destinations.some(d => d.Id === ui.testTo) ? ui.testTo : (destinations[0] ? destinations[0].Id : '');
    const testDisabled = templateTestBlocked(ui);
    const testButton = `<button ${BUTTON} id="tgj-tpl-test" class="raised emby-button" data-tgj-act="tplTest"${testDisabled ? ' disabled' : ''}>`
        + `<span>${t(S, 'web.templates.test')}</span></button>`;
    const target = destinations.length > 0
        ? `<div class="selectContainer"><select is="emby-select" class="emby-select-withcolor emby-select" label="${t(S, 'web.templates.testTo')}" `
          + `data-tgj-input="tplTestTo">${destinations.map(d => `<option value="${esc(d.Id)}"${d.Id === chosen ? ' selected' : ''}>${esc(d.Title)}</option>`).join('')}</select></div>`
        : `<p class="fieldDescription">${t(S, 'web.templates.noDestinations')}</p>`;
    parts.push(`<div class="tgj-tpl-test">${target}${testButton}`
        + `${ui.testResult ? `<span class="tgj-tpl-test-result">${ui.testResult}</span>` : ''}</div>`,
        `<p class="fieldDescription">${t(S, 'web.templates.testNote')}</p>`);

    const save = button(t(S, 'web.templates.save'), 'tplSave', { primary: true, disabled: ui.busy });
    if (item.Edited && ui.resetAsk) {
        parts.push(`<div class="tgj-notice"><span class="tgj-notice-text">${t(S, 'web.templates.resetAsk')}</span>`
            + `${button(t(S, 'web.templates.resetConfirm'), 'tplResetConfirm', { disabled: ui.busy })}${button(t(S, 'web.templates.resetCancel'), 'tplResetCancel')}</div>`,
            `<div>${save}</div>`);
    } else {
        parts.push(`<div>${item.Edited ? button(t(S, 'web.templates.reset'), 'tplReset', { disabled: ui.busy }) : ''}${save}</div>`);
    }

    return `<div class="verticalSection">${parts.join('')}</div>`;
}

/**
 * The diagnostics block under Advanced (spec §12): Copy diagnostics, and the text itself when the clipboard refused it.
 * @param {Record<string, string>} S
 * @param {{ text?: string, copied?: boolean }} ui
 * @returns {string} HTML
 */
export function renderDiagnostics(S, ui) {
    const parts = [`<h3>${t(S, 'web.diag.title')}</h3>`, `<div>${button(t(S, 'web.diag.copy'), 'copyDiagnostics')}</div>`];
    if (ui.copied) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.diag.copied')}</p>`);
    }

    if (ui.text) {
        parts.push(`<p class="fieldDescription">${t(S, 'web.diag.manual')}</p>`
            + `<textarea id="tgj-diagnostics-text" readonly rows="12" style="width:100%">${esc(ui.text)}</textarea>`);
    }

    return `<div class="verticalSection">${parts.join('')}</div>`;
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
            // The proxy form (§9.1): its own messages and in-flight flag.
            proxyErrors: [],
            proxySaving: false,
            // The destinations section (§6.6.3): the open editor, the remove confirm, and the last test/check outcome per id.
            destEditing: null,
            destConfirm: null,
            destLeave: false,
            destResults: {},
            // The manual block (§6.5): the typed chat ID or link, the presets, and the last add's outcome (HTML).
            manualInput: '',
            manualPreset: 'Everything',
            manualResult: '',
            detectedPresets: {},
            // The settings sections (§6.6.4-5): the last save's field errors, the section whose Save was pressed, in flight.
            settingsErrors: [],
            settingsSection: '',
            settingsSaving: false,
            // Diagnostics: the text when the clipboard refused it, and whether the last copy worked.
            diagText: '',
            diagCopied: false,
            // The templates section: the selected event key, the last validation and the last one without errors per
            // key, a save or reset in flight, the test send's destination, in-flight flag and outcome (HTML), and the
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
    // The template preview: the newest Validate request's number (older replies are ignored), and the typing debounce.
    let tplSeq = 0;
    let tplTimer = null;

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

    /**
     * Runs a write that may replace the template textarea. When the textarea has the focus, the new one gets it back with
     * the same selection and scroll position, so a render while the admin types (a Destinations render, a revision sync)
     * doesn't move the caret. The text itself is safe: it renders from the form state.
     * @param {() => void} write
     */
    const keepTemplateCaret = write => {
        const before = byId('tgj-tpl-text');
        const doc = (before && before.ownerDocument) || globalThis.document;
        if (!before || !doc || doc.activeElement !== before) {
            write();
            return;
        }

        const saved = {
            start: before.selectionStart,
            end: before.selectionEnd,
            direction: before.selectionDirection || 'none',
            top: before.scrollTop,
            left: before.scrollLeft,
        };
        write();
        const after = byId('tgj-tpl-text');
        if (!after || after === before) {
            return;
        }

        if (typeof after.focus === 'function') {
            after.focus({ preventScroll: true });
        }

        if (typeof after.setSelectionRange === 'function' && typeof saved.start === 'number' && typeof saved.end === 'number') {
            after.setSelectionRange(saved.start, saved.end, saved.direction);
        }

        after.scrollTop = saved.top;
        after.scrollLeft = saved.left;
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
        syncForm('settings', settingsFormData(dto));
    };

    /** The settings sections' views of the state. */
    const settingsUi = () => ({
        form: state.forms.settings,
        errors: state.ui.settingsErrors,
        saving: state.ui.settingsSaving,
        section: state.ui.settingsSection,
    });

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
     * Validates the selected template's current text and shows the result in the preview. Every request gets the next
     * number; a reply that is not for the newest request is ignored, so a slow older reply can't overwrite a newer one.
     */
    const validateTemplate = async () => {
        const key = templateKey();
        const form = state.forms[`tpl:${key}`];
        if (!key || !form) {
            return;
        }

        const seq = ++tplSeq;
        const S = state.strings;
        let reply;
        try {
            const result = await api.post('Templates/Validate', { EventKey: key, Text: String(form.local.Text ?? '') });
            reply = result.status === 200 && result.body ? result.body : { RequestError: requestError(S, result) };
        } catch (err) {
            reply = { RequestError: t(S, 'web.requestFailed', { reason: err && err.message }) };
        }

        if (seq !== tplSeq) {
            return;
        }

        state.ui.tplResults[key] = reply;
        if (!validationFailed(reply)) {
            state.ui.tplGood[key] = reply;
        }

        if (key === templateKey()) {
            showTemplatePreview();
        }
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

    const renderSection = name => {
        const section = sections[name];
        const write = () => setHtml(section.id, section.render() + sectionError(name));
        if (name === 'templates') {
            keepTemplateCaret(write);
        } else {
            write();
        }

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
            const text = byId('tgj-diagnostics-text');
            if (text && typeof text.select === 'function') {
                text.select();
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
        // An untouched field is omitted (the server keeps the stored value); a cleared one is "".
        if (typed.proxyUsername !== undefined) {
            body.Username = typed.proxyUsername;
        }

        if (typed.proxyPassword !== undefined) {
            body.Password = typed.proxyPassword;
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
            // The proxy form starts over; the settings form keeps its unsaved edits on top of the fresh data.
            resetForm('proxy', proxyFormData(result.body));
            takeSettings(result.body);
            typed.proxyUsername = undefined;
            typed.proxyPassword = undefined;
        } else if (result.status === 409 && result.body && result.body.Current) {
            takeSettings(result.body.Current);
            ui.proxyErrors = [t(S, 'web.proxy.conflict')];
        } else if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
            ui.proxyErrors = result.body.Errors.map(e => esc(e.Message));
        } else {
            ui.proxyErrors = [requestError(S, result)];
        }

        renderProxyForms();
        renderSettingsSections();
        return undefined;
    };

    /** Saves the settings form (spec §10.4): the one PUT Settings behind the Save of all three sections. */
    const saveSettings = async section => {
        const ui = state.ui;
        const form = state.forms.settings;
        if (!form || !state.settings) {
            return undefined;
        }

        const S = state.strings;
        const request = settingsRequest(form, section, state.settings.Revision, S);
        ui.settingsSection = section;
        ui.settingsErrors = request.errors;
        if (!request.body) {
            renderSettingsSections();
            return undefined;
        }

        ui.settingsSaving = true;
        renderSettingsSections();
        let result;
        try {
            result = await api.put('Settings', request.body);
        } finally {
            ui.settingsSaving = false;
        }

        let failed;
        if (result.status === 200 && result.body) {
            // This section's fields are saved: the rest of the form keeps its edits, and so does a field of this section
            // that was typed into while the request ran (its base is what was sent, so a later value counts as an edit).
            const now = state.forms.settings;
            const sent = overlaySection(form.base, form.local, section);
            const others = Object.keys(SECTION_FIELDS).filter(name => name !== section).flatMap(name => SECTION_FIELDS[name]);
            const kept = (now.changed ?? []).filter(path => others.includes(path.split('.')[0]));
            state.forms.settings = syncFormData({ base: overlaySection(now.base, sent, section), local: now.local, dirty: true, changed: kept }, settingsFormData(result.body));
            takeSettings(result.body);
        } else if (result.status === 409 && result.body && result.body.Current) {
            takeSettings(result.body.Current);
            ui.settingsErrors = [{ Field: '', Html: t(S, 'web.settings.conflict') }];
        } else if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
            ui.settingsErrors = result.body.Errors;
        } else {
            failed = result;
        }

        renderSettingsSections();
        return failed;
    };

    const destination = id => (state.destinations ? state.destinations.Items.find(d => d.Id === id) : undefined);
    const destKey = id => `dest:${id}`;
    const idOf = el => el.getAttribute('data-tgj-id');

    /**
     * Handles a destination change's reply (spec §10.4): a 409 takes the fresh data in and keeps the unsaved edits, a 400
     * shows the server's messages. Returns the result only for the other failures, which runAction shows.
     */
    const destinationReply = (result, id) => {
        const S = state.strings;
        if (result.status === 409 && result.body && result.body.Current) {
            const pending = Boolean(state.forms[destKey(id)] && state.forms[destKey(id)].dirty);
            takeDestinations(result.body.Current);
            state.ui.errors.destinations = t(S, pending ? 'web.dest.conflict' : 'web.dest.conflictPlain');
            return undefined;
        }

        if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
            state.ui.errors.destinations = result.body.Errors.map(e => esc(e.Message)).join(' ');
            return undefined;
        }

        return result.status >= 300 ? result : undefined;
    };

    /** PUTs a change to one destination; on success the section starts over from the server's data (keepForm: not the editor). */
    const putDestination = async (id, body, { keepForm = false } = {}) => {
        const result = await api.put(`Destinations/${encodeURIComponent(id)}`, body);
        if (result.status === 200) {
            if (!keepForm) {
                delete state.forms[destKey(id)];
            }

            await reloadSection('destinations', { fresh: true });
            return undefined;
        }

        return destinationReply(result, id);
    };

    const destinationOutcome = (id, html) => {
        state.ui.destResults[id] = html;
    };

    const openManual = () => {
        const advanced = byId('tgj-advanced');
        if (advanced) {
            advanced.open = true;
        }

        const manual = byId('tgj-manual');
        if (manual && typeof manual.scrollIntoView === 'function') {
            manual.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    };

    /** Sets one editor value for a destination and re-renders the section. */
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
                : ' ' + (body.TestSent ? t(S, 'dest.test.ok') : t(S, 'dest.test.failed', { error: body.TestError }));
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
        saveProxy: () => runAction('proxy', saveProxy),
        edit: el => {
            const id = idOf(el);
            state.ui.destEditing = state.ui.destEditing === id ? null : id;
            state.ui.destConfirm = null;
            renderSection('destinations');
        },
        enabled: el => runAction('destinations', () => {
            const dest = destination(idOf(el));
            return dest
                ? putDestination(dest.Id, { ExpectedRevision: state.destinations.Revision, Enabled: !dest.Enabled }, { keepForm: true })
                : undefined;
        }),
        saveDest: el => runAction('destinations', () => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            const body = form && state.destinations
                ? destinationChanges(destinationFormData(destination(id)), form.local, state.destinations.Revision)
                : null;
            return body ? putDestination(id, body) : undefined;
        }),
        removeLibrary: el => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (form) {
                const gone = el.getAttribute('data-tgj-lib');
                editDestination(id, { LibraryIds: form.local.LibraryIds.filter(x => x !== gone) });
            }

            renderSection('destinations');
        },
        testDest: el => runAction('destinations', async () => {
            const id = idOf(el);
            const result = await api.post(`Destinations/${encodeURIComponent(id)}/Test`);
            if (result.status !== 200 || !result.body) {
                return result;
            }

            const S = state.strings;
            destinationOutcome(id, result.body.Sent ? t(S, 'dest.test.ok') : t(S, 'dest.test.failed', { error: result.body.Error }));
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
            destinationOutcome(id, result.body.Error ? esc(result.body.Error) : t(S, `dest.health.${health && health.Status ? health.Status : 'Ok'}`));
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
            const revision = state.destinations ? state.destinations.Revision : 0;
            const result = await api.del(`Destinations/${encodeURIComponent(id)}?leave=${state.ui.destLeave}&expectedRevision=${revision}`);
            state.ui.destConfirm = null;
            if (result.status === 204 || result.status === 200) {
                delete state.forms[destKey(id)];
                delete state.ui.destResults[id];
                await reloadSection('destinations', { fresh: true });
                return undefined;
            }

            return destinationReply(result, id);
        }),
        'open-manual': openManual,
        manualAdd: () => runAction('manual', async () => {
            const value = state.ui.manualInput.trim();
            if (!value || !state.destinations) {
                return undefined;
            }

            state.ui.manualResult = '';
            const target = /^-?\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? { ChatId: Number(value) } : { TopicLink: value };
            return manualReply(await api.post('Destinations', { ExpectedRevision: state.destinations.Revision, ...target, Preset: state.ui.manualPreset }));
        }),
        detectedSetUp: el => runAction('manual', async () => {
            const id = detectedId(el);
            if (id === null || !state.destinations) {
                return undefined;
            }

            state.ui.manualResult = '';
            const preset = state.ui.detectedPresets[id] ?? 'Everything';
            return manualReply(await api.post(`DetectedChats/${id}`, { ExpectedRevision: state.destinations.Revision, Preset: preset }));
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
        saveSettings: el => {
            const section = SETTINGS_SECTIONS.includes(idOf(el)) ? idOf(el) : 'messages';
            return runAction(section, () => saveSettings(section));
        },
        useSuggested: () => {
            if (state.settings && state.settings.PublicUrlSuggestion) {
                markDirty('settings', 'PublicUrl', state.settings.PublicUrlSuggestion);
            }

            renderSection('messages');
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
        tplSave: () => runAction('templates', async () => {
            const key = templateKey();
            const name = `tpl:${key}`;
            const form = state.forms[name];
            if (!form || !state.templates) {
                return undefined;
            }

            const S = state.strings;
            state.ui.tplBusy = true;
            renderSection('templates');
            let result;
            try {
                result = await api.put(`Templates/${encodeURIComponent(key)}`,
                    { ExpectedRevision: state.templates.Revision, Text: String(form.local.Text ?? '') });
            } finally {
                state.ui.tplBusy = false;
            }

            if (result.status === 200 && result.body) {
                // Saved: this key starts over from the server's data; other keys keep their unsaved text.
                delete state.forms[name];
                takeTemplates(result.body);
                return undefined;
            }

            if (result.status === 409 && result.body && result.body.Current) {
                takeTemplates(result.body.Current);
                state.ui.errors.templates = t(S, 'web.templates.conflict');
                return undefined;
            }

            if (result.status === 400 && result.body && Array.isArray(result.body.Errors)) {
                state.ui.errors.templates = result.body.Errors.map(e => esc(e.Message)).join(' ');
                return undefined;
            }

            return result;
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
            state.ui.tplBusy = true;
            renderSection('templates');
            let result;
            try {
                result = await api.del(`Templates/${encodeURIComponent(key)}?expectedRevision=${state.templates.Revision}`);
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
                : (reply.Error ? esc(reply.Error) : t(S, `dest.health.${health && health.Status ? health.Status : 'Ok'}`));
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
        proxyMode: (el, event) => {
            markDirty('proxy', 'Mode', el.value);
            if (event.type === 'change') {
                renderProxyForms();
            }
        },
        proxyUrl: el => markDirty('proxy', 'Url', el.value),
        proxyUsername: el => {
            typed.proxyUsername = String(el.value ?? '');
        },
        proxyPassword: el => {
            typed.proxyPassword = String(el.value ?? '');
        },
        destPreset: (el, event) => {
            const id = idOf(el);
            const dest = destination(id);
            const preset = el.value;
            if (!dest || !PRESETS.includes(preset)) {
                return;
            }

            const events = state.destinations.Catalog.Presets[preset];
            editDestination(id, events ? { Preset: preset, Events: [...events] } : { Preset: preset });
            if (event.type === 'change') {
                renderSection('destinations');
            }
        },
        destEvent: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (!form) {
                return;
            }

            const keys = state.destinations.Catalog.EventKeys;
            const chosen = toggled(form.local.Events, el.getAttribute('data-tgj-key'), Boolean(el.checked));
            editDestination(id, { Events: keys.filter(k => chosen.includes(k)), Preset: 'Custom' });
            if (event.type === 'change') {
                renderSection('destinations');
            }
        },
        destLibrary: (el, event) => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (form) {
                const lib = el.getAttribute('data-tgj-lib');
                editDestination(id, { LibraryIds: toggled(form.local.LibraryIds, lib, Boolean(el.checked)) });
                if (event.type === 'change') {
                    renderSection('destinations');
                }
            }
        },
        destSilent: el => {
            const id = idOf(el);
            const form = state.forms[destKey(id)];
            if (form) {
                const group = el.getAttribute('data-tgj-group');
                editDestination(id, { SilentGroups: toggled(form.local.SilentGroups, group, Boolean(el.checked)) });
            }
        },
        destLeave: el => {
            state.ui.destLeave = Boolean(el.checked);
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
        publicUrl: el => markDirty('settings', 'PublicUrl', String(el.value ?? '')),
        msgToggle: el => {
            const key = el.getAttribute('data-tgj-key');
            if (MESSAGE_TOGGLES.includes(key)) {
                markDirty('settings', `Message.${key}`, Boolean(el.checked));
            }
        },
        batching: el => {
            const key = el.getAttribute('data-tgj-key');
            if (BATCHING_FIELDS.some(f => f.key === key)) {
                const text = String(el.value ?? '').trim();
                // A value that isn't a whole number stays text, so Save can say so instead of sending it.
                markDirty('settings', `Batching.${key}`, /^-?\d+$/.test(text) ? Number(text) : text);
            }
        },
        updEnabled: el => markDirty('settings', 'UpdateCheck.Enabled', Boolean(el.checked)),
        updMode: el => {
            if (UPDATE_MODES.includes(el.value)) {
                markDirty('settings', 'UpdateCheck.Mode', el.value);
            }
        },
        // Typing only records the edit and, after a pause, refreshes the preview pane: re-rendering would move the caret.
        tplText: (el, event) => {
            markDirty(`tpl:${templateKey()}`, 'Text', String(el.value ?? ''));
            if (event.type === 'input') {
                cancelTemplateTimer();
                tplTimer = setTimeout(() => {
                    tplTimer = null;
                    validateTemplate().catch(err => console.error('TgJelly: the template preview failed', err));
                }, PREVIEW_DEBOUNCE_MS);
            }
        },
        tplEvent: el => {
            if (state.templates && state.templates.Items.some(i => i.EventKey === el.value)) {
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
        syncedRevision = Math.max(syncedRevision, newestRevision());
    };

    const hide = () => {
        visible = false;
        stopPolling();
        cancelTemplateTimer();
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

    // A spoiler in the template preview is revealed (and hidden again) on click, as in Telegram.
    view.addEventListener('click', event => {
        const spoiler = event.target && event.target.closest ? event.target.closest('.tgj-spoiler') : null;
        if (spoiler) {
            spoiler.classList.toggle('tgj-revealed');
        }
    });

    return { api, state, sections, actions, inputs, reloadSection, runAction, syncForm, markDirty, resetForm };
}
