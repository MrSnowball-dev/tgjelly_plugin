// TgJelly dashboard: the Log section (plan 9 Task 13, design §8.1). Imported as './configurationpage?name=tgjelly-log.js'.
//
// One row per message per destination, newest first: when, the event, the destination, the result as a label, and a
// plain reason. A Failed or attention row opens to show what Telegram or the network said, the tries and what to do.
// The rows' texts (reasons, advice) arrive from the server already in words (LogRecorder); this module adds the frame.
// No summary counts: the owner dropped them.
//
// Like tgjelly-messages.js, it imports only the leaf tgjelly-ui.js, never tgjelly.js, which imports it.
import { esc, t, text, plural, label, card, actionButton, emptyState, selectField } from './configurationpage?name=tgjelly-ui.js';

/** Rows asked for per page. */
const PAGE_SIZE = 100;
/** The most rows one GET Log returns (LogController.MaxLimit). */
const MAX_LIMIT = 500;
/** Longer than this without an event from Jellyfin, the top line says the server has been quiet (design §8.1). */
const QUIET_MS = 24 * 60 * 60 * 1000;

/** Each result's label (design §3, §8.1): its word and its tone. An unknown result shows as a neutral word. */
const RESULTS = {
    Waiting: ['web.log.result.Waiting', 'progress'],
    Skipped: ['web.log.result.Skipped', 'neutral'],
    Sent: ['web.log.result.Sent', 'good'],
    SentLate: ['web.log.result.SentLate', 'attention'],
    SentWithBuiltIn: ['web.log.result.SentWithBuiltIn', 'attention'],
    SentWithoutPhoto: ['web.log.result.SentWithoutPhoto', 'attention'],
    Failed: ['web.log.result.Failed', 'bad'],
};

/** The table's columns: each cell's class suffix and its heading. Destination, not Chat (the owner's correction). */
const COLUMNS = [
    ['when', 'web.log.col.when'],
    ['event', 'web.log.col.event'],
    ['dest', 'web.log.col.destination'],
    ['result', 'web.log.col.result'],
    ['why', 'web.log.col.why'],
];

/** The results a row opens on (Failed and the attention states), whatever else it carries. */
const PROBLEMS = new Set(['Failed', 'SentLate', 'SentWithBuiltIn', 'SentWithoutPhoto']);

/** Event keys the Log has besides the message keys (LogRecorder): the rolling summary of alerts. */
const LOG_ONLY_EVENTS = { 'server.summary': 'web.log.event.summary' };

const has = (S, key) => Boolean(S) && Object.hasOwn(S, key);

/**
 * A message's name for the Log, never a raw key: its web.event.* name ("Movies"), the summary's own name, or the name of
 * its group ("Media" for an item removed before it was ready, whose key is just "media").
 * @param {Record<string, string> | null} S
 * @param {string} key the row's EventKey
 * @returns {string} plain text
 */
export function eventName(S, key) {
    const value = String(key ?? '');
    if (has(S, `web.event.${value}`)) {
        return text(S, `web.event.${value}`);
    }

    if (Object.hasOwn(LOG_ONLY_EVENTS, value)) {
        return text(S, LOG_ONLY_EVENTS[value]);
    }

    const group = value.split('.')[0];
    return has(S, `web.event.group.${group}`) ? text(S, `web.event.group.${group}`) : value;
}

/** date.toLocaleString in the page's language, or in English when the browser doesn't know it. */
function localized(date, lang, options) {
    try {
        return date.toLocaleString(lang, options);
    } catch {
        return date.toLocaleString('en', options);
    }
}

const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

/**
 * A Log time: the local time, with the date when it isn't today (and the year when it isn't this year); and the full
 * date and time with seconds, for hovering.
 * @param {string} iso an API time
 * @param {string} lang
 * @param {number} now
 * @returns {{ shown: string, full: string }}
 */
export function logTime(iso, lang, now) {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
        return { shown: String(iso ?? ''), full: String(iso ?? '') };
    }

    const today = new Date(now);
    const clock = { hour: 'numeric', minute: '2-digit' };
    const shown = sameDay(date, today)
        ? localized(date, lang, clock)
        : localized(date, lang, { ...clock, month: 'short', day: 'numeric', ...(date.getFullYear() === today.getFullYear() ? {} : { year: 'numeric' }) });
    const full = localized(date, lang, { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
    return { shown, full };
}

/** A <time> showing a Log time, with the full date and time as its title. */
function timeHtml(iso, lang, now) {
    const { shown, full } = logTime(iso, lang, now);
    return `<time datetime="${esc(iso)}" title="${esc(full)}">${esc(shown)}</time>`;
}

/** t() for a string with a {time} placeholder that takes HTML (a <time>). */
const withTime = (S, key, html) => t(S, key).replace('{time}', html);

/**
 * Takes a refreshed newest page into the rows held: rows older than the page that the admin loaded with Show older
 * stay, with their cursor, so a refresh never takes them away. The newest page is as long as what was held (up to the
 * server's limit), so it ends where the held rows still go on.
 * @param {object | null} held the LogDto held, with any older pages appended
 * @param {object} fresh the newest page just loaded
 * @returns {object} a LogDto
 */
export function mergeLog(held, fresh) {
    if (!held || !fresh.Next || fresh.Items.length === 0) {
        return fresh;
    }

    const last = fresh.Items[fresh.Items.length - 1].Id;
    const at = held.Items.findIndex(r => r.Id === last);
    const ids = new Set(fresh.Items.map(r => r.Id));
    const tail = at < 0 ? [] : held.Items.slice(at + 1).filter(r => !ids.has(r.Id));
    return tail.length === 0 ? fresh : { ...fresh, Items: [...fresh.Items, ...tail], Next: held.Next };
}

/** The event keys the Event filter offers, by group: the catalog's, the digest under Media and the summary last. */
function eventGroups(catalog, current) {
    const groups = catalog && Array.isArray(catalog.Groups) && catalog.Groups.length > 0 ? catalog.Groups : ['media', 'security', 'server'];
    const keys = [...(catalog && Array.isArray(catalog.EventKeys) ? catalog.EventKeys : [])];
    for (const extra of ['media.digest', 'server.summary']) {
        if (!keys.includes(extra)) {
            keys.push(extra);
        }
    }

    const listed = groups.map(group => [group, keys.filter(k => k.split('.')[0] === group)]);
    if (current && !keys.includes(current)) {
        listed.push(['', [current]]);
    }

    return listed.filter(([, list]) => list.length > 0);
}

/** The controls: All / Problems only, then the Destination and Event selects. No counts. */
function renderControls(filters, S, destinations, catalog, page) {
    const show = filters.problemsOnly ? 'problems' : 'all';
    const option = (value, key) => `<label class="tgj-seg-option"><input type="radio" class="tgj-seg-input" name="tgj-log-show"`
        + ` data-tgj-input="logShow" data-tgj-key="${value}" value="${value}"${show === value ? ' checked' : ''}>`
        + `<span class="tgj-seg-text">${t(S, key)}</span></label>`;
    const segment = `<div class="tgj-seg" role="radiogroup" aria-label="${t(S, 'web.log.filter.label')}">`
        + option('all', 'web.log.filter.all') + option('problems', 'web.log.filter.problems') + '</div>';

    const dests = (destinations ?? []).map(d => ({ value: d.Id, label: d.Title }));
    // A destination removed since keeps its name from its rows.
    if (filters.destinationId && !dests.some(d => d.value === filters.destinationId)) {
        const known = page && page.Items.find(r => r.DestinationId === filters.destinationId && r.DestinationTitle);
        dests.push({ value: filters.destinationId, label: known ? known.DestinationTitle : filters.destinationId });
    }

    const destination = selectField({
        input: 'logDestination',
        value: filters.destinationId,
        options: [{ value: '', label: text(S, 'web.log.destination.all') }, ...dests],
        attrs: { 'aria-label': text(S, 'web.log.destination.label') },
    });

    const opt = (value, name) => `<option value="${esc(value)}"${value === filters.eventKey ? ' selected' : ''}>${esc(name)}</option>`;
    const groups = eventGroups(catalog, filters.eventKey).map(([group, keys]) => {
        const options = keys.map(k => opt(k, eventName(S, k))).join('');
        return group ? `<optgroup label="${esc(eventName(S, group))}">${options}</optgroup>` : options;
    });
    const event = `<select class="tgj-field tgj-select" data-tgj-input="logEvent" aria-label="${t(S, 'web.log.event.label')}">`
        + opt('', text(S, 'web.log.event.all')) + groups.join('') + '</select>';

    return `<div class="tgj-log-controls">${segment}${destination}${event}</div>`;
}

/** The top lines: when a message last reached Telegram, whether Jellyfin has been quiet, and whether the log started empty. */
function renderTopLines(page, S, lang, now) {
    const lines = [page.LastSentAt
        ? withTime(S, 'web.log.lastSent', timeHtml(page.LastSentAt, lang, now))
        : t(S, 'web.log.lastSentNone')];
    if (!page.LastEventAt) {
        lines.push(t(S, page.StartedEmpty ? 'web.log.quietSinceStart' : 'web.log.quietWeek'));
    } else if (now - new Date(page.LastEventAt).getTime() > QUIET_MS) {
        lines.push(withTime(S, 'web.log.quiet', timeHtml(page.LastEventAt, lang, now)));
    }

    const notes = lines.map(line => `<p class="tgj-log-line">${line}</p>`).join('');
    const empty = page.StartedEmpty ? `<p class="tgj-log-line tgj-muted">${t(S, 'web.log.startedEmpty')}</p>` : '';
    return `<div class="tgj-log-lines">${notes}${empty}</div>`;
}

/** Whether a row opens to show more: a Failed or attention result, or Telegram's words or advice to show. */
const opens = row => PROBLEMS.has(row.Result) || Boolean(row.Telegram) || Boolean(row.Advice);

/** The opened part of a row: what Telegram or the network said, the tries and the retry, and what to do. */
function renderDetail(row, index, tone, S, lang, now) {
    const parts = [];
    if (row.Telegram) {
        parts.push(`<p><span class="tgj-log-detail-label">${t(S, 'web.log.detail.said')}</span> <code>${esc(row.Telegram)}</code></p>`);
    }

    const tries = [];
    if (row.Tries > 0) {
        tries.push(plural(S, 'web.log.detail.tries', row.Tries, {}, lang));
    }

    if (row.RetryAt) {
        tries.push(t(S, 'web.log.detail.retry', { time: logTime(row.RetryAt, lang, now).shown }));
    } else if (row.Result === 'Failed') {
        tries.push(t(S, 'web.log.detail.noRetry'));
    }

    if (tries.length > 0) {
        parts.push(`<p>${tries.join(' ')}</p>`);
    }

    if (row.Advice) {
        parts.push(`<p><span class="tgj-log-detail-label">${t(S, 'web.log.detail.advice')}</span> ${esc(row.Advice)}</p>`);
    }

    return `<tr class="tgj-log-detail tgj-log-detail--${tone}" id="tgj-log-detail-${index}"><td colspan="5"><div class="tgj-log-box">${parts.join('')}</div></td></tr>`;
}

/** One row, and its opened part when it is open. */
function renderRow(row, index, S, lang, now, expanded) {
    const [key, tone] = RESULTS[row.Result] ?? [null, 'neutral'];
    const word = label(key ? text(S, key) : String(row.Result ?? ''), tone);
    const more = opens(row);
    const open = more && expanded.has(row.Id);
    const id = esc(row.Id);
    const result = more
        ? `<button type="button" class="tgj-log-toggle" data-tgj-act="logToggle" data-tgj-id="${id}" data-tgj-instant`
            + ` aria-expanded="${open ? 'true' : 'false'}" aria-controls="tgj-log-detail-${index}" title="${t(S, 'web.log.toggle')}">${word}</button>`
        : word;
    const destination = row.DestinationTitle
        ? esc(row.DestinationTitle)
        : `<span class="tgj-muted" title="${t(S, 'web.log.noDestination')}" aria-label="${t(S, 'web.log.noDestination')}">—</span>`;
    // Why is empty for a plain Sent (the server sends no details for it); several details are joined, most serious first.
    const why = esc((row.Details ?? []).join('; '));
    const classes = `tgj-log-row${more ? ' tgj-log-row--opens' : ''}${open ? ' tgj-log-row--open' : ''}`;
    // The whole row opens it too (design §8.1: clicking the row); the button is the keyboard's way in.
    const rowAct = more ? ` data-tgj-act="logToggle" data-tgj-id="${id}" data-tgj-instant` : '';
    return `<tr class="${classes}" data-tgj-row="${id}"${rowAct}>`
        + `<td class="tgj-log-when">${timeHtml(row.At, lang, now)}</td>`
        + `<td class="tgj-log-event"><span class="tgj-log-title">${esc(row.Title)}</span><span class="tgj-log-kind">${esc(eventName(S, row.EventKey))}</span></td>`
        + `<td class="tgj-log-dest" data-label="${t(S, 'web.log.col.destination')}">${destination}</td>`
        + `<td class="tgj-log-result">${result}</td>`
        + `<td class="tgj-log-why">${why}</td>`
        + '</tr>'
        + (open ? renderDetail(row, index, tone, S, lang, now) : '');
}

/**
 * Renders the Log section (design §8.1): the lead, the filters, the top lines and the table, newest first.
 * @param {object | null} page the LogDto (with any older pages appended), or null while it loads
 * @param {{ problemsOnly: boolean, destinationId: string, eventKey: string }} filters
 * @param {Record<string, string> | null} S
 * @param {string} [lang]
 * @param {{ now?: number, expanded?: Set<string>, destinations?: { Id: string, Title: string }[],
 *   catalog?: { EventKeys: string[], Groups: string[] } | null }} [view] the open rows by ID, and what the selects offer
 * @returns {string} HTML
 */
export function renderLog(page, filters, S, lang = 'en', view = {}) {
    const { now = Date.now(), expanded = new Set(), destinations = [], catalog = null } = view;
    const controls = renderControls(filters, S, destinations, catalog, page);
    if (!page) {
        return card({ title: text(S, 'web.log.heading'), description: text(S, 'web.log.lead'), body: controls + `<p class="tgj-row-hint">${t(S, 'web.log.loading')}</p>`, extraClass: 'tgj-log' });
    }

    let body;
    if (page.Items.length === 0) {
        const filtered = filters.problemsOnly || Boolean(filters.destinationId) || Boolean(filters.eventKey);
        body = emptyState({ text: text(S, filtered ? 'web.log.emptyFiltered' : 'web.log.empty') });
    } else {
        const head = COLUMNS.map(([name, key]) => `<th scope="col" class="tgj-log-${name}">${t(S, key)}</th>`).join('');
        const rows = page.Items.map((row, index) => renderRow(row, index, S, lang, now, expanded)).join('');
        const older = page.Next ? `<div class="tgj-actions tgj-log-older">${actionButton({ label: text(S, 'web.log.older'), act: 'logOlder' })}</div>` : '';
        body = `<div class="tgj-log-scroll"><table class="tgj-log-table"><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table></div>${older}`;
    }

    return card({
        title: text(S, 'web.log.heading'),
        description: text(S, 'web.log.lead'),
        body: controls + renderTopLines(page, S, lang, now) + body,
        extraClass: 'tgj-log',
    });
}

const selectorValue = value => String(value).replace(/["\\]/g, c => `\\${c}`);

/** The nearest scrolling ancestor of el, or the document's scrolling element. */
function scrollerOf(el) {
    const doc = el.ownerDocument || globalThis.document;
    const view = doc && doc.defaultView;
    for (let node = el.parentElement; node && view; node = node.parentElement) {
        const overflow = view.getComputedStyle(node).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) {
            return node;
        }
    }

    return doc ? doc.scrollingElement : null;
}

/**
 * Runs a write of the Log section without moving what the admin is looking at:
 * - a focused button (Show older, a row's toggle) is focused again in its new copy; nothing else takes the focus;
 * - when the admin has scrolled down into the table, the first row in view stays where it is on the screen, so rows
 *   arriving at the top don't push the rows being read away.
 * @param {Element | null} host the section's container
 * @param {() => void} write
 */
export function keepPlace(host, write) {
    if (!host || typeof host.querySelectorAll !== 'function') {
        write();
        return;
    }

    const doc = host.ownerDocument || globalThis.document;
    const active = doc ? doc.activeElement : null;
    const act = active && typeof host.contains === 'function' && host.contains(active) && typeof active.getAttribute === 'function'
        ? active.getAttribute('data-tgj-act')
        : null;
    let focusKey = null;
    if (act) {
        const id = active.getAttribute('data-tgj-id');
        focusKey = `${String(active.tagName).toLowerCase()}[data-tgj-act="${selectorValue(act)}"]${id === null ? '' : `[data-tgj-id="${selectorValue(id)}"]`}`;
    }

    const rowsNow = () => [...host.querySelectorAll('tr[data-tgj-row]')];
    let anchor = null;
    const before = rowsNow();
    if (before.length > 0 && typeof before[0].getBoundingClientRect === 'function' && before[0].getBoundingClientRect().top < 0) {
        const first = before.find(r => r.getBoundingClientRect().bottom > 0);
        anchor = first ? { id: first.getAttribute('data-tgj-row'), top: first.getBoundingClientRect().top } : null;
    }

    write();

    if (focusKey && doc.activeElement !== active) {
        const again = host.querySelector(focusKey);
        if (again && typeof again.focus === 'function') {
            again.focus({ preventScroll: true });
        }
    }

    if (anchor) {
        const again = rowsNow().find(r => r.getAttribute('data-tgj-row') === anchor.id);
        const delta = again ? again.getBoundingClientRect().top - anchor.top : 0;
        const scroller = Math.abs(delta) >= 1 ? scrollerOf(host) : null;
        if (scroller) {
            scroller.scrollTop += delta;
        }
    }
}

/**
 * The Log section's controller part. It gets the page's state and machinery (as createMessages does) and returns the
 * section, its clicks and inputs. The page reloads it with the status poll while it is shown.
 * @param {{ state: object, api: object, lang: () => string, renderSection: (name: string) => void,
 *   reloadSection: (name: string, options?: object) => Promise<void>, runAction: (name: string, fn: () => Promise<unknown>) => Promise<void> }} page
 */
export function createLog(page) {
    const { state, api } = page;
    // The filters, and the rows the admin opened (by ID; they stay open across refreshes).
    Object.assign(state.ui, { logFilters: { problemsOnly: false, destinationId: '', eventKey: '' }, logExpanded: new Set() });
    state.log = null;
    // Each filter change starts a new generation: a reply for an older one is dropped, and the first reply of a new one
    // replaces the rows instead of merging into them.
    let generation = 0;
    let heldGeneration = -1;

    const query = ({ before = null, limit = PAGE_SIZE } = {}) => {
        const filters = state.ui.logFilters;
        const params = new URLSearchParams({ problemsOnly: String(filters.problemsOnly), limit: String(limit) });
        if (filters.destinationId) {
            params.set('destinationId', filters.destinationId);
        }

        if (filters.eventKey) {
            params.set('eventKey', filters.eventKey);
        }

        if (before) {
            params.set('before', before);
        }

        return 'Log?' + params.toString();
    };

    const failure = result => `HTTP ${result.status}${result.body && result.body.Message ? `: ${result.body.Message}` : ''}`;
    const held = () => (heldGeneration === generation ? state.log : null);

    /** Loads the newest rows; rows loaded with Show older stay (mergeLog). */
    const load = async () => {
        const asked = generation;
        const rows = held() ? held().Items.length : 0;
        const result = await api.get(query({ limit: Math.min(MAX_LIMIT, Math.max(PAGE_SIZE, rows)) }));
        if (result.status !== 200 || !result.body) {
            throw new Error(failure(result));
        }

        if (asked === generation) {
            state.log = mergeLog(held(), result.body);
            heldGeneration = asked;
        }
    };

    /** Loads the page under the oldest row held, with the server's own cursor. */
    const older = async () => {
        const current = held();
        if (!current || !current.Next) {
            return undefined;
        }

        const asked = generation;
        const cursor = current.Next;
        const result = await api.get(query({ before: cursor }));
        if (result.status !== 200 || !result.body) {
            return result;
        }

        // A filter change, or another Show older, got there first: this page no longer follows the rows held.
        if (asked !== generation || !held() || held().Next !== cursor) {
            return undefined;
        }

        const ids = new Set(state.log.Items.map(r => r.Id));
        state.log = { ...state.log, Items: [...state.log.Items, ...result.body.Items.filter(r => !ids.has(r.Id))], Next: result.body.Next };
        return undefined;
    };

    const setFilter = change => {
        const next = { ...state.ui.logFilters, ...change };
        const filters = state.ui.logFilters;
        if (next.problemsOnly === filters.problemsOnly && next.destinationId === filters.destinationId && next.eventKey === filters.eventKey) {
            return undefined;
        }

        state.ui.logFilters = next;
        generation++;
        return page.reloadSection('log', { fresh: true });
    };

    const section = {
        id: 'tgj-log',
        load,
        render: () => renderLog(held(), state.ui.logFilters, state.strings, page.lang(), {
            expanded: state.ui.logExpanded,
            destinations: state.destinations ? state.destinations.Items : [],
            catalog: state.destinations ? state.destinations.Catalog : null,
        }),
        write: keepPlace,
    };

    const actions = {
        logToggle: el => {
            const id = el.getAttribute('data-tgj-id');
            const open = state.ui.logExpanded;
            if (open.has(id)) {
                open.delete(id);
            } else {
                open.add(id);
            }

            page.renderSection('log');
        },
        logOlder: () => page.runAction('log', older),
    };

    // A select or a radio fires input and then change; the change is the choice.
    const inputs = {
        logShow: (el, event) => (event.type === 'change' ? setFilter({ problemsOnly: el.value === 'problems' }) : undefined),
        logDestination: (el, event) => (event.type === 'change' ? setFilter({ destinationId: String(el.value ?? '') }) : undefined),
        logEvent: (el, event) => (event.type === 'change' ? setFilter({ eventKey: String(el.value ?? '') }) : undefined),
    };

    return { section, actions, inputs };
}
