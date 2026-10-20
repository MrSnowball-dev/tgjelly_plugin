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

/**
 * The page controller (spec §11.1). Listeners are bound once, here; viewshow is idempotent.
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
        // statusError is HTML (escaped) shown under the status, e.g. a failed take over; statusFailed: the last GET Status failed.
        ui: { statusError: '', statusFailed: false },
    };
    const lang = () => document.documentElement.lang || 'en';
    const byId = id => view.querySelector('#' + id);
    let visible = false;
    let timer = null;
    let polling = false;
    let stringsLoad = null;

    /** Sets a container's HTML, skipping identical HTML so a poll doesn't steal focus or flicker. */
    const setHtml = (id, html) => {
        const el = byId(id);
        if (el && el.innerHTML !== html) {
            el.innerHTML = html;
        }
    };

    const renderStatusSection = () => {
        const S = state.strings;
        let html = state.status ? renderStatus(state.status, S, lang()) : '';
        if (state.ui.statusFailed) {
            html += `<p class="tgj-error">${t(S, 'web.status.failed')}</p>`;
        }

        if (state.ui.statusError) {
            html += `<p class="tgj-error">${state.ui.statusError}</p>`;
        }

        setHtml('tgj-status', html);
    };

    const loadStatus = async () => {
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
        renderStatusSection();
    };

    // Section loaders, run after Status on every viewshow. Each is `async () => {}`: fetch into state, then render its
    // container. Tasks 8-12 add theirs here. A loader that throws is logged and doesn't stop the others.
    const loaders = [];

    // Click actions, keyed by data-tgj-act. Each is `async (el, event) => {}`; el is the [data-tgj-act] element.
    // Tasks 8-12 add theirs here. A thrown error is logged and shown under the status.
    const actions = {
        dismiss: async el => {
            const result = await api.post('Status/Dismiss', { NoticeId: el.getAttribute('data-tgj-id') });
            state.ui.statusError = result.status < 300 ? '' : requestError(state.strings, result);
            await loadStatus();
        },
        takeover: async () => {
            const result = await api.post('Webhook/TakeOver');
            state.ui.statusError = result.status < 300 ? '' : requestError(state.strings, result);
            await loadStatus();
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

        if (document.hidden || polling) {
            return;
        }

        polling = true;
        loadStatus().finally(() => {
            polling = false;
        });
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
        await loadStatus();
        await Promise.all(loaders.map(load => load().catch(err => console.error('TgJelly: a section failed to load', err))));
    };

    const hide = () => {
        visible = false;
        stopPolling();
    };

    view.addEventListener('viewshow', show);
    view.addEventListener('viewbeforehide', hide);
    view.addEventListener('viewhide', hide);
    view.addEventListener('viewdestroy', hide);

    view.addEventListener('click', event => {
        const el = event.target && event.target.closest ? event.target.closest('[data-tgj-act]') : null;
        const action = el ? actions[el.getAttribute('data-tgj-act')] : null;
        if (!action || !state.strings) {
            return;
        }

        event.preventDefault();
        if (el.disabled) {
            return;
        }

        el.disabled = true;
        Promise.resolve()
            .then(() => action(el, event))
            .catch(err => {
                console.error('TgJelly: action failed', err);
                const S = state.strings;
                state.ui.statusError = t(S, 'web.requestFailed', { reason: err && err.message });
                renderStatusSection();
            })
            .finally(() => {
                el.disabled = false;
            });
    });
}
