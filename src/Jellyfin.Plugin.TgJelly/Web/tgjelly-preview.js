// TgJelly dashboard: the template preview (spec §8.5; plan 9). Imported as './configurationpage?name=tgjelly-preview.js'.
//
// Turns a Validate reply into the message as Telegram would show it. Every piece of markup it writes is either built
// from esc()/t() output or comes from previewHtml(), the sanitiser, which re-emits only Telegram's formatting tags.
// The tgj-tg- classes imitate Telegram on purpose, so tgjelly.css styles them with Telegram's own colours (plan 8, R3).
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
        parts.push(`<p class="tgj-tpl-warning">${esc(w)}</p>`);
    }

    const failed = Boolean(result.RequestError) || (result.Errors ?? []).length > 0;
    const shown = failed ? lastGood : result;
    if (shown) {
        parts.push(`<div class="tgj-tg-bubble${failed ? ' tgj-tg-stale' : ''}">`
            + (shown.Poster ? `<div class="tgj-tg-poster">${t(S, 'web.templates.poster')}</div>` : '')
            + `<div class="tgj-tg-text">${previewHtml(shown.Html)}</div>${previewButtons(shown.Buttons)}</div>`);
    }

    if (!result.RequestError && (!failed || result.Length > 0)) {
        parts.push(`<p class="tgj-row-hint tgj-tpl-length">${t(S, 'web.templates.length', { length: formatNumber(result.Length ?? 0, lang), limit: formatNumber(result.Limit ?? 0, lang) })}</p>`);
    }

    return parts.join('');
}
