// TgJelly dashboard: the shell — header, sidebar, sections, the URL hash and keeping focus across re-renders.

import { esc, label, t, text } from './configurationpage?name=tgjelly-ui.js';
import { POLL_SECONDS, PROBLEM_STATES } from './configurationpage?name=tgjelly-api.js';

// --- the shell: header, sidebar, one section shown at a time ---

/** The dashboard's sections, in sidebar order; each label is web.nav.<name>. */
export const SECTIONS = ['overview', 'destinations', 'messages', 'advanced', 'log'];
const NAV_LABELS = {
    overview: 'web.nav.overview',
    destinations: 'web.nav.destinations',
    messages: 'web.nav.messages',
    advanced: 'web.nav.advanced',
    log: 'web.nav.log',
};

/** Today's section containers, by the dashboard section that holds them. */
const SECTION_HOSTS = {
    // Connect precedes Status: with no bot it leads, and with a bot it is empty, so Status leads.
    // Status always leads; Connect follows it until a bot is connected.
    overview: ['tgj-status', 'tgj-connect', 'tgj-bot'],
    destinations: ['tgj-destinations'],
    // Messages holds the templates too, and Batching sits in Advanced after Update checks.
    messages: ['tgj-messages'],
    advanced: ['tgj-token', 'tgj-proxy', 'tgj-updates', 'tgj-batching', 'tgj-manual', 'tgj-diagnostics'],
    // The Log is last: after Advanced in the sidebar, the last tab on a phone.
    log: ['tgj-log'],
};

/** How long the first show waits for tgjelly.css before showing the page unstyled. */
const STYLESHEET_WAIT_MS = 1000;

/** The header's connection label: a label's HTML, a word in an outline and no symbol. */
export const connectionHtml = connection => label(connection.text, connection.tone);

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
export const versionHtml = (S, version, buildTarget) => (version
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

    // Templates became part of Messages: an old link to them opens Messages.
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

export const selectorValue = value => String(value).replace(/["\\]/g, c => `\\${c}`);

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
 * Runs a write that may replace fields inside rootEl, so that typing is never interrupted (generalising
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
