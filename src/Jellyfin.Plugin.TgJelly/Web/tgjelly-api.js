// TgJelly dashboard: the API client, form helpers and the error texts every section shows.

import { esc, text } from './configurationpage?name=tgjelly-ui.js';

export const POLL_MS = 3000;

/** Connection states shown in the error color (the ConnectionState names from GET Status). */
export const PROBLEM_STATES = new Set(['TokenRejected', 'WebhookConflict', 'ConflictingInstance', 'Unreachable', 'InvalidBaseUrl', 'Incompatible']);

export function isPlainObject(value) {
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

export function sameValue(a, b) {
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

export const POLL_SECONDS = POLL_MS / 1000;

/** The plain-text cause of a thrown error: the browser's or network's own words, or a note that it gave none. */
export const errorReason = err => (err && err.message ? String(err.message) : 'the browser gave no details');

/**
 * The plain-text reason a failed API result gives: the server's Message with the HTTP status, or, when the server sent
 * no message, the status and where to look next.
 * @param {Record<string, string>} S
 * @param {{ status: number, body: any }} result
 * @returns {string}
 */
export function failureReason(S, result) {
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
export const isNetworkError = err => Boolean(err) && (err.name === 'NetworkError' || err.name === 'AbortError'
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
export const statusReason = (S, result) => (result.body && result.body.Message
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
