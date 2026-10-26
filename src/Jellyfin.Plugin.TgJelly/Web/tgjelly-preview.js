// TgJelly dashboard: the template preview. Imported as './configurationpage?name=tgjelly-preview.js'.
//
// Turns a Validate reply into the message as Telegram would show it. Every piece of markup it writes is either built
// from esc()/t() output or comes from previewHtml(), the sanitiser, which re-emits only Telegram's formatting tags.
// The tgj-tg- classes imitate Telegram on purpose, so tgjelly.css styles them with Telegram's own colours.
import { esc, t, formatNumber } from './configurationpage?name=tgjelly-ui.js';

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

// The server's hover-link markers (TemplateMarkers): U+E000, a piece's index, U+E001 before the piece's
// output, and U+E002 after it. Escaped as \u here so no editor or tool can lose them.
const MARK_START = '\uE000';
const MARK_INDEX = '\uE001';
const MARK_END = '\uE002';
const MARK_ANY = /[\uE000-\uE002]/;
const MARK_HEAD = /^(\d{1,6})\uE001/;

/**
 * Takes the markers out of marked HTML: the clean HTML, and for each of its characters the piece it belongs to (-1 for
 * none); pieceAt is null when there are no markers. A broken marker is dropped.
 */
function unmark(source) {
    if (!MARK_ANY.test(source)) {
        return { html: source, pieceAt: null };
    }

    let html = '';
    const pieceAt = [];
    let current = -1;
    for (let i = 0; i < source.length; i++) {
        const c = source[i];
        if (c === MARK_START) {
            const head = MARK_HEAD.exec(source.slice(i + 1, i + 9));
            if (head) {
                current = Number(head[1]);
                i += head[0].length;
            }
        } else if (c === MARK_END) {
            current = -1;
        } else if (c !== MARK_INDEX) {
            html += c;
            pieceAt.push(current);
        }
    }

    return { html, pieceAt };
}

/**
 * Splits Telegram HTML into tokens: text (decoded), and open and close tags with their attributes and raw source. With
 * pieceAt, every token carries the piece it starts in, and text is split where the piece changes.
 */
function previewTokens(source, pieceAt = null) {
    const tokens = [];
    let start = 0;
    let i = 0;
    const text = end => {
        let from = start;
        for (let k = start + 1; k <= end; k++) {
            if (k === end || (pieceAt && pieceAt[k] !== pieceAt[from])) {
                tokens.push({ type: 'text', value: decodeEntities(source.slice(from, k)), piece: pieceAt ? pieceAt[from] : -1 });
                from = k;
            }
        }
    };

    while (i < source.length) {
        const lt = source.indexOf('<', i);
        if (lt < 0) {
            break;
        }

        PREVIEW_TAG.lastIndex = lt;
        const match = PREVIEW_TAG.exec(source);
        if (!match) {
            i = lt + 1;
            continue;
        }

        text(lt);
        const attrs = {};
        for (const attr of match[3].matchAll(PREVIEW_ATTR)) {
            const name = attr[1].toLowerCase();
            if (!Object.hasOwn(attrs, name)) {
                attrs[name] = decodeEntities(attr[2] ?? attr[3] ?? attr[4] ?? '');
            }
        }

        tokens.push({ type: match[1] ? 'close' : 'open', name: match[2].toLowerCase(), attrs, raw: match[0], piece: pieceAt ? pieceAt[lt] : -1 });
        i = start = lt + match[0].length;
    }

    text(source.length);
    return tokens;
}

/**
 * A node list as HTML: markup (already safe) as it is, text escaped. Text of a hover-link piece goes in a span carrying
 * the piece's number, one span for each run of the same piece's text; empty text gets none.
 */
function nodesHtml(nodes) {
    let html = '';
    for (let i = 0; i < nodes.length; i++) {
        const node = nodes[i];
        if (node.html !== undefined) {
            html += node.html;
            continue;
        }

        let run = node.text;
        while (node.piece >= 0 && i + 1 < nodes.length && nodes[i + 1].html === undefined && nodes[i + 1].piece === node.piece) {
            run += nodes[++i].text;
        }

        html += node.piece >= 0 && run !== '' ? `<span data-tgj-piece="${node.piece}">${esc(run)}</span>` : esc(run);
    }

    return html;
}

/** An expandable quote: the first line is the summary, the rest the body. */
function expandableQuote(children) {
    const index = children.findIndex(n => n.text !== undefined && n.text.includes('\n'));
    let summary = children;
    let body = [];
    if (index >= 0) {
        const cut = children[index].text.indexOf('\n');
        const { text, piece } = children[index];
        summary = [...children.slice(0, index), { text: text.slice(0, cut), piece }];
        body = [{ text: text.slice(cut + 1), piece }, ...children.slice(index + 1)];
    }

    return `<details class="tgj-tg-quote"><summary>${nodesHtml(summary)}</summary>${nodesHtml(body)}</details>`;
}

/** The safe preview markup for one closed Telegram element. */
function previewElement(name, attrs, children) {
    const inner = nodesHtml(children);
    if (PREVIEW_SAME.has(name)) {
        return `<${name}>${inner}</${name}>`;
    }

    switch (name) {
        case 'tg-spoiler':
            return `<span class="tgj-tg-spoiler">${inner}</span>`;
        case 'span':
            return String(attrs.class ?? '').trim() === 'tg-spoiler' ? `<span class="tgj-tg-spoiler">${inner}</span>` : inner;
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
 * span.tgj-tg-spoiler; blockquote (an expandable one as details/summary); a as a new-tab link for http:, https: and tg:
 * hrefs only, otherwise its text; tg-emoji and tg-time as their text. An unknown, unclosed or stray tag is shown as text.
 * Entities are decoded once, and all text is escaped on output.
 * The server's hover-link markers are taken out first, wherever they are, even inside a tag; each run of
 * a piece's text becomes a span data-tgj-piece="<index>", and a piece that is only tags gets no span.
 * @param {string} telegramHtml
 * @returns {string} HTML
 */
export function previewHtml(telegramHtml) {
    // Each open element: its tag, attributes, raw source and children; the root holds the output.
    const root = { children: [] };
    const stack = [root];
    const top = () => stack.at(-1);
    // An element that never closed: its tag's source as text, then its children.
    const unwrap = frame => [{ text: frame.raw, piece: frame.piece }, ...frame.children];
    const { html, pieceAt } = unmark(String(telegramHtml ?? ''));

    for (const token of previewTokens(html, pieceAt)) {
        if (token.type === 'text') {
            top().children.push({ text: token.value, piece: token.piece });
        } else if (token.type === 'open' && PREVIEW_KNOWN.has(token.name)) {
            stack.push({ name: token.name, attrs: token.attrs, raw: token.raw, piece: token.piece, children: [] });
        } else if (token.type === 'close' && stack.some((f, i) => i > 0 && f.name === token.name)) {
            while (top().name !== token.name) {
                const open = stack.pop();
                top().children.push(...unwrap(open));
            }

            const frame = stack.pop();
            top().children.push({ html: previewElement(frame.name, frame.attrs, frame.children) });
        } else {
            top().children.push({ text: token.raw, piece: token.piece });
        }
    }

    while (stack.length > 1) {
        const open = stack.pop();
        top().children.push(...unwrap(open));
    }

    return nodesHtml(root.children);
}

/**
 * The message's inline keyboard, under the bubble as in Telegram: the reply_markup rows as they are, each key a link
 * only for an http:, https: or tg: URL. A link key carries tgj-tg-url, which draws Telegram's small corner arrow.
 */
function previewButtons(rows, stale) {
    const html = (rows ?? []).filter(row => Array.isArray(row) && row.length > 0).map(row => `<div class="tgj-tg-row">${row.map(b => {
        const href = safeHref(b && b.Url);
        const label = esc(b && b.Text);
        return href === null
            ? `<span class="tgj-tg-button">${label}</span>`
            : `<a class="tgj-tg-button tgj-tg-url" target="_blank" rel="noopener noreferrer" href="${esc(href)}">${label}</a>`;
    }).join('')}</div>`);
    return html.length > 0 ? `<div class="tgj-tg-buttons${stale ? ' tgj-tg-stale' : ''}">${html.join('')}</div>` : '';
}

/** The time Telegram prints in the bubble's corner: now, as hours and minutes. */
function clockTime() {
    const now = new Date();
    return `${now.getHours()}:${String(now.getMinutes()).padStart(2, '0')}`;
}

/** Whether a validation result failed: it had errors, or the request itself failed. */
const failed = result => Boolean(result.RequestError) || (result.Errors ?? []).length > 0;

/**
 * The template's check, shown under the textarea: the validation's errors and warnings, then the
 * message's length against Telegram's limit (left out while the text has errors and renders nothing).
 * @param {object | null} result the last TemplateValidationDto; RequestError (HTML) when the request itself failed
 * @param {Record<string, string>} S
 * @param {string} [lang]
 * @returns {string} HTML
 */
export function renderTemplateCheck(result, S, lang = 'en') {
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
        parts.push(`<p class="tgj-tpl-warning">${esc(w)}</p>`);
    }

    if (!result.RequestError && (!failed(result) || result.Length > 0)) {
        parts.push(`<p class="tgj-row-hint tgj-tpl-length">${t(S, 'web.templates.length', { length: formatNumber(result.Length ?? 0, lang), limit: formatNumber(result.Limit ?? 0, lang) })}</p>`);
    }

    return parts.join('');
}

/**
 * The template preview pane: the message as Telegram's dark theme shows it, on the chat background. The
 * bubble has its tail and holds the poster on top, then the text with the time in its bottom right corner; the inline
 * keys sit under the bubble, in their rows. The text comes from MarkedHtml when the server sent it, so each piece of
 * the template is a span the hover link can find. With errors, the last good preview is shown dimmed instead;
 * renderTemplateCheck shows the errors themselves.
 * @param {object | null} result the last TemplateValidationDto; RequestError (HTML) when the request itself failed
 * @param {Record<string, string>} S
 * @param {object | null} [lastGood] the last validation of this template without errors
 * @param {string} [time] the time in the bubble (default: now)
 * @returns {string} HTML
 */
export function renderTemplatePreview(result, S, lastGood = null, time = clockTime()) {
    if (!result) {
        return '';
    }

    const stale = failed(result);
    const shown = stale ? lastGood : result;
    return shown
        ? `<div class="tgj-tg-chat"><div class="tgj-tg-msg"><div class="tgj-tg-bubble${stale ? ' tgj-tg-stale' : ''}">`
            + (shown.Poster ? `<div class="tgj-tg-poster">${t(S, 'web.templates.poster')}</div>` : '')
            + `<div class="tgj-tg-text">${previewHtml(shown.MarkedHtml || shown.Html)}`
            + `<span class="tgj-tg-time" aria-hidden="true"><span>${esc(time)}</span></span></div></div>`
            + `${previewButtons(shown.Buttons, stale)}</div></div>`
        : '';
}
