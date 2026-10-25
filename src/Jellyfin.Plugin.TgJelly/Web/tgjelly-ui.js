// TgJelly dashboard components. Imported as './configurationpage?name=tgjelly-ui.js'.
//
// It also holds the string and number helpers (t, text, plural, formatNumber, ...) that every page module shares; the
// sibling modules import them from here, never from tgjelly.js, so no two modules import each other.
//
// Every component is a pure function that returns an HTML string styled by tgjelly.css:
// - every text argument is escaped here, so callers pass plain text;
// - arguments named control, body, html or actionHtml are trusted HTML that the caller has already built from
//   esc()/t() output or from these helpers;
// - every helper takes an optional extraClass, appended to its outer element's class list.
// Controls carry the page's usual hooks: data-tgj-input for inputs, data-tgj-act and data-tgj-id for actions.
// No browser dialogs and no status symbols: a destructive action asks with inlineConfirm, and a state is a label (a
// word in an outline).

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
 * The short cause of a failed API result, for the error a load throws: the HTTP status and the server's own message
 * when it sent one ("HTTP 500: The log file is locked"), else just the status.
 * @param {{ status: number, body: any }} result
 * @returns {string}
 */
export function httpFailure(result) {
    return `HTTP ${result.status}${result.body && result.body.Message ? `: ${result.body.Message}` : ''}`;
}

// --- strings and numbers ---
// The strings table is always called S, so the key checks in tests/web can find the key at every t() and plural() call.

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

// --- components ---

/** The class attribute value: the base classes plus the caller's extraClass, escaped. */
function cls(base, extraClass) {
    return esc(extraClass ? `${base} ${extraClass}` : base);
}

/** One of the allowed values, or a thrown error: a typo in a kind is a bug, not a style to guess. */
function oneOf(value, allowed, what) {
    if (!allowed.includes(value)) {
        throw new Error(`${what} must be one of ${allowed.join(', ')} (got ${value})`);
    }

    return value;
}

/**
 * Extra attributes as HTML: true gives a bare attribute, false/null/undefined leaves it out, anything else is escaped.
 * Names are checked, and inline event handlers (on...) are refused, so attrs can't smuggle script in.
 * @param {Record<string, unknown>} attrs
 */
function attributes(attrs) {
    let html = '';
    for (const [name, value] of Object.entries(attrs ?? {})) {
        if (!/^[a-z][a-z0-9-]*$/i.test(name) || /^on/i.test(name)) {
            throw new Error(`not an allowed attribute name: ${name}`);
        }

        if (value === true) {
            html += ` ${name}`;
        } else if (value !== false && value !== null && value !== undefined) {
            html += ` ${name}="${esc(value)}"`;
        }
    }

    return html;
}

/**
 * A card: a title, an optional description and a body.
 * @param {{ title: string, description?: string, body: string, id?: string | null, extraClass?: string }} options
 *   body is trusted HTML.
 * @returns {string}
 */
export function card({ title, description = '', body, id = null, extraClass = '' }) {
    return `<section class="${cls('tgj-card', extraClass)}"${id === null ? '' : ` id="${esc(id)}"`}>`
        + `<header class="tgj-card-head"><h2 class="tgj-card-title">${esc(title)}</h2>`
        + `${description ? `<p class="tgj-card-desc">${esc(description)}</p>` : ''}</header>`
        + `<div class="tgj-card-body">${body ?? ''}</div></section>`;
}

/**
 * A setting row: the label and its explanation on the left, the control on the right, and the save cue beside the
 * control. Rows in a card are separated by thin dividers. Add extraClass 'tgj-row--stack' to put a wide control
 * (a long text field) under its label.
 * @param {{ label: string, hint?: string, control: string, cueKey?: string | null, extraClass?: string }} options
 *   control is trusted HTML; cueKey names the cue the auto-save queue writes to (data-tgj-cue).
 * @returns {string}
 */
export function row({ label, hint = '', control, cueKey = null, extraClass = '' }) {
    return `<div class="${cls('tgj-row', extraClass)}">`
        + `<div class="tgj-row-text"><span class="tgj-row-label">${esc(label)}</span>`
        + `${hint ? `<span class="tgj-row-hint">${esc(hint)}</span>` : ''}</div>`
        + `<div class="tgj-row-control">${control ?? ''}`
        + `${cueKey === null ? '' : `<span class="tgj-cue" data-tgj-cue="${esc(cueKey)}" aria-live="polite"></span>`}</div></div>`;
}

/**
 * An on/off switch, which replaces checkboxes. It is a button: the page toggles aria-checked on click.
 * @param {{ input: string, checked: boolean, disabled?: boolean, label: string, attrs?: Record<string, unknown>,
 *   extraClass?: string }} options label is the accessible name (the row shows it as text).
 * @returns {string}
 */
export function switchControl({ input, checked, disabled = false, label, attrs = {}, extraClass = '' }) {
    return `<button type="button" role="switch" class="${cls('tgj-switch', extraClass)}" aria-checked="${checked ? 'true' : 'false'}"`
        + ` aria-label="${esc(label)}" data-tgj-input="${esc(input)}"${attributes(attrs)}${disabled ? ' disabled' : ''}></button>`;
}

const TEXT_TYPES = ['text', 'url', 'password', 'search', 'email', 'tel'];

/**
 * A one-line text field.
 * @param {{ input: string, value: unknown, type?: string, placeholder?: string, invalid?: boolean,
 *   attrs?: Record<string, unknown>, extraClass?: string }} options invalid marks it aria-invalid (the error colour).
 * @returns {string}
 */
export function textField({ input, value, type = 'text', placeholder = '', invalid = false, attrs = {}, extraClass = '' }) {
    return `<input type="${oneOf(type, TEXT_TYPES, 'textField type')}" class="${cls('tgj-field', extraClass)}" data-tgj-input="${esc(input)}"`
        + ` value="${esc(value)}"${placeholder ? ` placeholder="${esc(placeholder)}"` : ''}${invalid ? ' aria-invalid="true"' : ''}`
        + `${attributes(attrs)} />`;
}

/**
 * A select; the option whose value equals value is selected.
 * @param {{ input: string, value: unknown, options: { value: unknown, label: string }[], attrs?: Record<string, unknown>,
 *   extraClass?: string }} options
 * @returns {string}
 */
export function selectField({ input, value, options, attrs = {}, extraClass = '' }) {
    const items = (options ?? []).map(o => `<option value="${esc(o.value)}"${String(o.value) === String(value) ? ' selected' : ''}>${esc(o.label)}</option>`);
    return `<select class="${cls('tgj-field tgj-select', extraClass)}" data-tgj-input="${esc(input)}"${attributes(attrs)}>${items.join('')}</select>`;
}

/**
 * A whole-number field with an optional range.
 * @param {{ input: string, value: unknown, min?: number, max?: number, attrs?: Record<string, unknown>,
 *   extraClass?: string }} options
 * @returns {string}
 */
export function numberField({ input, value, min, max, attrs = {}, extraClass = '' }) {
    return `<input type="number" class="${cls('tgj-field tgj-number', extraClass)}" data-tgj-input="${esc(input)}" value="${esc(value)}"`
        + `${attributes({ min, max, step: 1, inputmode: 'numeric', ...attrs })} />`;
}

const BUTTON_KINDS = ['secondary', 'primary', 'danger'];

/**
 * A button for a data-tgj-act action. secondary is quiet, primary is the one solid accent button of a card, and
 * danger is for destructive actions.
 * @param {{ label: string, act: string, kind?: 'secondary' | 'primary' | 'danger', disabled?: boolean,
 *   id?: string | null, attrs?: Record<string, unknown>, extraClass?: string }} options
 * @returns {string}
 */
export function actionButton({ label, act, kind = 'secondary', disabled = false, id = null, attrs = {}, extraClass = '' }) {
    const modifier = oneOf(kind, BUTTON_KINDS, 'actionButton kind') === 'secondary' ? '' : ` tgj-btn--${kind}`;
    return `<button type="button" class="${cls(`tgj-btn${modifier}`, extraClass)}" data-tgj-act="${esc(act)}"`
        + `${id === null ? '' : ` data-tgj-id="${esc(id)}"`}${attributes(attrs)}${disabled ? ' disabled' : ''}>${esc(label)}</button>`;
}

/**
 * The inline confirmation that replaces browser dialogs: the question, then Confirm and Cancel buttons in place.
 * @param {{ question: string, confirmLabel: string, confirmAct: string, cancelAct: string, cancelLabel: string,
 *   id?: string | null, extraClass?: string }} options cancelLabel comes from the strings table like every label, so
 *   it has no English default. Both buttons carry data-tgj-id when id is given.
 * @returns {string}
 */
export function inlineConfirm({ question, confirmLabel, confirmAct, cancelAct, cancelLabel, id = null, extraClass = '' }) {
    if (typeof cancelLabel !== 'string' || cancelLabel === '') {
        throw new Error('inlineConfirm needs a cancelLabel');
    }

    return `<div class="${cls('tgj-confirm', extraClass)}" role="group"><span class="tgj-confirm-text">${esc(question)}</span>`
        + '<span class="tgj-confirm-actions">'
        + actionButton({ label: confirmLabel, act: confirmAct, kind: 'danger', id })
        + actionButton({ label: cancelLabel, act: cancelAct, id })
        + '</span></div>';
}

const NOTICE_KINDS = ['info', 'warning', 'error'];

/**
 * A notice: coloured text on a tinted background, with no icon. The only helper that takes HTML for its content.
 * @param {{ kind?: 'info' | 'warning' | 'error', html: string, extraClass?: string }} options html is TRUSTED HTML,
 *   inserted as is: build it from esc()/t() output only.
 * @returns {string}
 */
export function notice({ kind = 'info', html, extraClass = '' }) {
    return `<div class="${cls(`tgj-notice tgj-notice--${oneOf(kind, NOTICE_KINDS, 'notice kind')}`, extraClass)}">${html ?? ''}</div>`;
}

/**
 * An empty state, such as "No destinations yet", with the next step.
 * @param {{ text: string, actionHtml?: string, extraClass?: string }} options actionHtml is trusted HTML (buttons).
 * @returns {string}
 */
export function emptyState({ text, actionHtml = '', extraClass = '' }) {
    return `<div class="${cls('tgj-empty', extraClass)}"><p class="tgj-empty-text">${esc(text)}</p>`
        + `${actionHtml ? `<div class="tgj-empty-actions">${actionHtml}</div>` : ''}</div>`;
}

const LABEL_TONES = ['good', 'progress', 'neutral', 'attention', 'bad', 'accent'];

/**
 * A label: a state word in an outlined pill, coloured by its tone, with no fill and no symbol. It is the
 * one way the dashboard shows a state: "Saved", "Connected", "Edited", "Lost".
 * - good is success (Saved, Connected); progress is the accent (Saving…, Connecting); neutral is muted (Built-in,
 *   Off); attention is the warning colour (Photos blocked); bad is the error colour (Not saved, Lost); accent marks a
 *   change (Edited);
 * - an unknown tone shows as neutral, so a state this page doesn't know is still a word;
 * - attrs are extra attributes, as the other helpers take them; a class in attrs joins the class list.
 * @param {string} text plain text, escaped here
 * @param {'good' | 'progress' | 'neutral' | 'attention' | 'bad' | 'accent'} [tone]
 * @param {Record<string, unknown>} [attrs]
 * @returns {string}
 */
export function label(text, tone = 'neutral', attrs = {}) {
    const { class: extraClass = '', ...rest } = attrs ?? {};
    const known = LABEL_TONES.includes(tone) ? tone : 'neutral';
    return `<span class="${cls(`tgj-label tgj-label--${known}`, extraClass)}"${attributes(rest)}>${esc(text)}</span>`;
}
