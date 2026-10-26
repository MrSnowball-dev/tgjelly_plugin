// TgJelly dashboard page. Loaded through data-controller="__plugin/tgjelly.js".
//
// Layout of this module:
// - pure helpers and render functions are named exports, so tests/web can call them without a DOM;
// - every render function returns HTML built only from esc()/t()/plural() output, so server text is always escaped;
// - the default export is the page controller: it owns the state, the loaders, the click actions and the Status poller.
// The strings table is always called S, so the key check in tests/web can find the key at every t() and plural() call.
//
// Sibling modules are imported by the URL Jellyfin serves them under; tests/web/jellyfin-resolve.mjs maps it
// to the file for Node.

import {
    esc, formatNumber, httpFailure, label, notice, pluralText, t, text,
} from './configurationpage?name=tgjelly-ui.js';
import {
    BATCHING_FIELDS, UPDATE_KINDS, batchingError, overlayPaths, pathValue, renderBatching, renderDiagnostics,
    renderToken, renderUpdates, settingsFormData, settingsRequest,
} from './configurationpage?name=tgjelly-advanced.js';
import {
    POLL_MS, POLL_SECONDS, createApi, editForm, errorReason, failureReason, isNetworkError, requestError, statusReason,
    syncFormData, thrownError, thrownText,
} from './configurationpage?name=tgjelly-api.js';
import { CONFLICT_KEY, createAutosave } from './configurationpage?name=tgjelly-autosave.js';
import {
    destSaveKey, destinationChanges, destinationFormData, healthText, renderDestinations, renderManual, routeMap,
    sameLibrary,
} from './configurationpage?name=tgjelly-destinations.js';
import { createLog } from './configurationpage?name=tgjelly-log.js';
import { createMessages } from './configurationpage?name=tgjelly-messages.js';
import {
    proxyFormData, renderBot, renderConnect, renderProxyForm, renderStatus,
} from './configurationpage?name=tgjelly-overview.js';
import {
    SECTIONS, connectionHtml, connectionLabel, fieldSelector, hashWithSection, keepFocus, navCountHtml, renderShell,
    sectionFromHash, selectorValue, versionHtml, waitForStylesheet,
} from './configurationpage?name=tgjelly-shell.js';

// The sections' modules and a few helpers stay reachable through this module, for the page's callers and tests.
export { esc, t, text, plural, pluralText, formatNumber, formatTime } from './configurationpage?name=tgjelly-ui.js';
export { previewHtml, renderTemplateCheck, renderTemplatePreview } from './configurationpage?name=tgjelly-preview.js';
export * from './configurationpage?name=tgjelly-api.js';
export * from './configurationpage?name=tgjelly-overview.js';
export * from './configurationpage?name=tgjelly-destinations.js';
export * from './configurationpage?name=tgjelly-advanced.js';
export * from './configurationpage?name=tgjelly-shell.js';

/**
 * The page controller. Listeners are bound once, here; viewshow is idempotent.
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
            // The connect card: mode is null | 'new' | 'existing'; result is the last save's outcome.
            mode: null,
            changingToken: false,
            saving: false,
            result: null,
            unreachable: false,
            confirmDisconnect: false,
            removeDestinations: false,
            // The destinations section: the open editor, the remove confirm, and the last test/check outcome per id.
            destEditing: null,
            destConfirm: null,
            destLeave: false,
            destResults: {},
            // A forum topic chosen by link, while its field is open: { id, slot, text }; and which IDs blocks are open.
            topicLink: null,
            destIdsOpen: {},
            // The manual block: the typed chat ID or link, and the last add's outcome (HTML).
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
    // The token shown after Show, and the masked hint it belongs to: a different bot, or none, masks it again.
    let revealedToken = '';
    let revealedFor = '';
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

    // The one Revision every save sends (settings, destinations and templates share it). It is the newest
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

    // The Messages section lives in tgjelly-messages.js. It gets the page's state and save machinery;
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

    // The Log section lives in tgjelly-log.js. It loads only while it is shown (selectSection, the poll).
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
                topicLink: state.ui.topicLink,
                idsOpen: state.ui.destIdsOpen,
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
        token: {
            id: 'tgj-token',
            dependsOnStatus: true,
            load: async () => {},
            render: () => renderToken(state.status, state.strings, {
                token: state.status && state.status.TokenHint === revealedFor ? revealedToken : '',
            }),
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
     * Overview leads until a bot is connected.
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
        setHtml('tgj-version', versionHtml(state.strings, pluginVersion()));
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

    /** Puts the typed token, proxy username and password back into the inputs after a render (never through HTML). */
    const restoreSecrets = () => {
        if (typeof view.querySelectorAll !== 'function') {
            return;
        }

        // The token too: a re-render (a poll, or another control in the card) must not empty it.
        for (const field of ['token', 'proxyUsername', 'proxyPassword']) {
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

    // The sections that hold a copy of the one settings Revision, by the state their load fills. Messages
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

    /** Takes fresh server data into a form, keeping unsaved edits. Call it from a section's load. */
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

    /** Sends the token and acts on the outcome. Returns the API result only when it failed. */
    const postToken = async confirmSwitch => {
        const ui = state.ui;
        ui.saving = true;
        ui.result = null;
        renderSection('connect');
        let keepToken = false;
        try {
            let result;
            try {
                result = await api.post('Token', { Token: typed.token, ConfirmSwitch: confirmSwitch });
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

    // --- auto-save: the one page-wide queue, the cues, and the saves of Messages and Advanced ---
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
     * The markup a cue shows: the state word as a label, Saved good, Saving… progress and Not saved bad, and
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
     * Queues the save of one change: mode 'now' (switches, selects), 'debounce' with delay (typed text and
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
     * Revision. Edits waiting in other fields stay edits, and so does a value typed while the request ran.
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

    /** Typed text and numbers save this long after the last keystroke. */
    const TYPING_SAVE_MS = 800;

    /** A switch's new state on click: the opposite of the form's value at path. */
    const flipped = path => !pathValue(state.forms.settings && state.forms.settings.local, path);

    const proxyError = () => {
        const local = state.forms.proxy && state.forms.proxy.local;
        const withAddress = Boolean(local) && (local.Mode === 'Http' || local.Mode === 'Socks5');
        return withAddress && String(local.Url ?? '').trim() === '' ? text(state.strings, 'web.proxy.needsAddress') : null;
    };

    /**
     * Sends the proxy card as one save: an untouched username/password is omitted, a cleared one is "".
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

    /** A failed removal's reply: a 409 shows the fresh data and says so; other failures go to runAction. */
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
     * Sends one auto-save of a destination: what its form changed since the server data, with the current
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

    /** Posts a topic for a slot and takes the destination it returns; the link field closes once it worked. */
    const topicPost = (id, slot, choice) => runAction('destinations', async () => {
        if (!slot || !destination(id)) {
            return undefined;
        }

        await autosave.flush();
        const result = await api.post(`Destinations/${encodeURIComponent(id)}/Topics`, { ExpectedRevision: pageRevision(), Slot: slot, ...choice });
        if (result.status === 200 && result.body) {
            state.ui.topicLink = null;
            const form = state.forms[destKey(id)];
            if (form) {
                state.forms[destKey(id)] = syncFormData(form, destinationFormData(result.body));
            }

            takeDestination(result.body);
        }

        return result;
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

    /** The manual block's reply to an add or set-up: shows the outcome and reloads; other failures go to runAction. */
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
        // A topic for a slot: TgJelly creates one named after it, or uses the one a pasted link points to.
        topicCreateYes: el => topicPost(idOf(el), el.getAttribute('data-tgj-key'), { Create: true }),
        topicLinkUse: el => {
            const ask = state.ui.topicLink;
            return ask && ask.id === idOf(el) ? topicPost(ask.id, ask.slot, { Link: ask.text }) : undefined;
        },
        topicLinkCancel: () => {
            state.ui.topicLink = null;
            renderSection('destinations');
        },
        destIds: el => {
            const id = idOf(el);
            state.ui.destIdsOpen[id] = !state.ui.destIdsOpen[id];
            renderSection('destinations');
        },
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
        showToken: () => runAction('token', async () => {
            const result = await api.get('Token');
            if (result.status !== 200 || !result.body) {
                return result;
            }

            revealedToken = String(result.body.Token ?? '');
            revealedFor = state.status ? state.status.TokenHint : '';
            return result;
        }),
        hideToken: () => {
            revealedToken = '';
            renderSection('token');
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
    };

    const inputs = {
        token: el => {
            typed.token = String(el.value ?? '').trim();
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
        // A topic choice beside a media event or a group: another topic saves at once; Create and Use a link act.
        destTopic: (el, event) => {
            const id = idOf(el);
            const slot = el.getAttribute('data-tgj-key');
            const form = state.forms[destKey(id)];
            if (event.type !== 'change' || !form || !slot) {
                return;
            }

            const value = String(el.value ?? '');
            if (value === 'create') {
                state.ui.topicLink = { id, slot, create: true };
                renderSection('destinations');
                return;
            }

            if (value === 'link') {
                state.ui.topicLink = { id, slot, text: '' };
                renderSection('destinations');
                return;
            }

            if (slot === 'media' && value === 'separate') {
                editDestination(id, { Separate: true });
                renderSection('destinations');
                saveDestination(id, `topic:${slot}`, fieldSelector(el));
                return;
            }

            const routes = routeMap(form.local.Routes);
            if (value === '') {
                routes.delete(slot);
            } else {
                routes.set(slot, Number(value));
            }

            // One topic for all media again ends separate topics; the events' own choices are kept for next time.
            editDestination(id, { Routes: [...routes].map(([k, v]) => `${k}=${v}`).sort(), ...(slot === 'media' ? { Separate: false } : {}) });
            renderSection('destinations');
            saveDestination(id, `topic:${slot}`, fieldSelector(el));
        },
        destTopicLink: el => {
            if (state.ui.topicLink) {
                state.ui.topicLink.text = String(el.value ?? '').trim();
            }
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
        revealedToken = '';
        // Jellyfin keeps the page around: the shown token mustn't stay in it.
        if (state.strings) {
            renderSection('token');
        }
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

    // Leaving an auto-saved field saves it now. Fields that share a key save when the focus leaves all
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

    // The hover link between the template and its preview follows the pointer; its mirror follows the
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
