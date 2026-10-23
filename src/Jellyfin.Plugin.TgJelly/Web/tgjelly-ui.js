// TgJelly dashboard components (plan 8, design §5.3). Imported as './configurationpage?name=tgjelly-ui.js'.
//
// Every helper is a pure function that returns an HTML string styled by tgjelly.css:
// - every text argument is escaped here, so callers pass plain text;
// - arguments named control, body, html or actionHtml are trusted HTML that the caller has already built from
//   esc()/t() output or from these helpers;
// - every helper takes an optional extraClass, appended to its outer element's class list.
// Controls carry the page's usual hooks: data-tgj-input for inputs, data-tgj-act and data-tgj-id for actions.
// No browser dialogs and no status symbols: a destructive action asks with inlineConfirm, and a state is a coloured word.

const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

/**
 * Escapes a value for HTML text and attribute values. null and undefined give ''.
 * @param {unknown} value
 * @returns {string}
 */
export function esc(value) {
    return value === null || value === undefined ? '' : String(value).replace(/[&<>"']/g, c => ESCAPES[c]);
}

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

const STATE_KINDS = ['ok', 'warning', 'error', 'muted'];

/**
 * A state shown as a coloured word, with no dot or symbol: "Connected", "Not connected", "2 failing".
 * @param {{ kind?: 'ok' | 'warning' | 'error' | 'muted', text: string, extraClass?: string }} options
 * @returns {string}
 */
export function stateLabel({ kind = 'ok', text, extraClass = '' }) {
    return `<span class="${cls(`tgj-state tgj-state--${oneOf(kind, STATE_KINDS, 'stateLabel kind')}`, extraClass)}">${esc(text)}</span>`;
}
