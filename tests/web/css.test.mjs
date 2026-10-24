// Stylesheet tests (plan 8, design §5.3 and §8). Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
// tgjelly.css lives inside Jellyfin's own page, so every rule is scoped under #tgjelly-root, and every colour comes from
// a --tgj- token that reads Jellyfin's palette, so the page follows the server's theme.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const CSS = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');

const NESTING_AT_RULES = new Set(['media', 'container', 'supports']);

/** Skips a quoted string starting at i; returns the index after its closing quote. */
function skipString(src, i) {
    const quote = src[i];
    for (i++; i < src.length && src[i] !== quote; i++) {
        if (src[i] === '\\') {
            i++;
        }
    }

    return i + 1;
}

/** The index of the '}' that closes the block whose '{' is just before start. */
function blockEnd(src, start) {
    let depth = 1;
    for (let i = start; i < src.length;) {
        const c = src[i];
        if (c === '"' || c === "'") {
            i = skipString(src, i);
            continue;
        }

        if (c === '{') {
            depth++;
        } else if (c === '}' && --depth === 0) {
            return i;
        }

        i++;
    }

    throw new Error('unclosed block');
}

/** Splits on sep outside parentheses and strings. */
function splitTop(text, sep) {
    const parts = [];
    let depth = 0;
    let last = 0;
    for (let i = 0; i < text.length;) {
        const c = text[i];
        if (c === '"' || c === "'") {
            i = skipString(text, i);
            continue;
        }

        if (c === '(') {
            depth++;
        } else if (c === ')') {
            depth--;
        } else if (c === sep && depth === 0) {
            parts.push(text.slice(last, i));
            last = i + 1;
        }

        i++;
    }

    parts.push(text.slice(last));
    return parts.map(p => p.trim()).filter(p => p !== '');
}

/**
 * A small CSS parser: enough for this stylesheet. Rules inside @media/@container/@supports are collected with their
 * at-rule context; @keyframes are collected by name; anything else (other at-rules, nesting, stray text) is a problem.
 */
function parseCss(css) {
    const src = css.replace(/\/\*[\s\S]*?\*\//g, '');
    const out = { rules: [], keyframes: [], problems: [] };

    function walk(start, end, context) {
        let i = start;
        while (i < end) {
            while (i < end && /\s/.test(src[i])) {
                i++;
            }

            if (i >= end) {
                break;
            }

            const from = i;
            while (i < end && src[i] !== '{' && src[i] !== ';' && src[i] !== '}') {
                i = src[i] === '"' || src[i] === "'" ? skipString(src, i) : i + 1;
            }

            const prelude = src.slice(from, i).trim();
            if (i >= end || src[i] !== '{') {
                out.problems.push(`not a rule: ${prelude}`);
                i++;
                continue;
            }

            const close = blockEnd(src, i + 1);
            const body = src.slice(i + 1, close);
            const at = /^@([\w-]+)/.exec(prelude);
            if (at !== null && NESTING_AT_RULES.has(at[1])) {
                walk(i + 1, close, [...context, prelude]);
            } else if (at !== null && at[1] === 'keyframes') {
                out.keyframes.push({ name: prelude.slice(at[0].length).trim(), body, context });
            } else if (at !== null) {
                out.problems.push(`unexpected at-rule: ${prelude}`);
            } else if (body.includes('{')) {
                out.problems.push(`nested rules are not allowed: ${prelude}`);
            } else {
                const decls = splitTop(body, ';').map(d => {
                    const colon = d.indexOf(':');
                    return { prop: d.slice(0, colon).trim().toLowerCase(), value: d.slice(colon + 1).trim() };
                });
                out.rules.push({ selectors: splitTop(prelude, ','), decls, context });
            }

            i = close + 1;
        }
    }

    walk(0, src.length, []);
    return out;
}

const ROOT = /^#tgjelly-root(?![\w-])/;

/** Every reason the stylesheet isn't scoped under #tgjelly-root. */
function scopeProblems(css) {
    const parsed = parseCss(css);
    const problems = [...parsed.problems];
    for (const rule of parsed.rules) {
        for (const selector of rule.selectors) {
            if (!ROOT.test(selector)) {
                problems.push(`${rule.context.join(' ')} ${selector}`.trim());
            }
        }
    }

    for (const frames of parsed.keyframes) {
        if (!frames.name.startsWith('tgj-')) {
            problems.push(`@keyframes ${frames.name} is not tgj- prefixed`);
        }
    }

    return problems;
}

// Properties whose whole value must be one colour.
const COLOR_PROPS = /^(color|background|background-color|border-color|border-(top|right|bottom|left|block|inline|block-start|block-end|inline-start|inline-end)-color|outline-color|caret-color|accent-color|text-decoration-color|fill|stroke)$/;
const COLOR_KEYWORDS = /^(transparent|currentcolor|inherit)$/i;
const TOKEN = /^var\(--tgj-[\w-]+\)$/;
const COLOR_LITERAL = /#[0-9a-f]{3,8}\b|\b(rgba?|hsla?|hwb|lab|lch|oklab|oklch|color|color-mix)\(|\b(white|black|red|green|blue|gr[ae]y|silver|orange|yellow|purple|pink)\b/i;
// --tgj- tokens that hold no colour.
const NON_COLOR_TOKEN = /^--tgj-(space|step|radius|font|leading|dur|ease)\b/;
const PALETTE = /^var\(--jf-palette-[\w-]+,\s*\S[^)]*\)$/;
const MIX = /^color-mix\(in [\w-]+,\s*var\(--tgj-[\w-]+\)(\s+\d+%)?,\s*(var\(--tgj-[\w-]+\)|transparent)(\s+\d+%)?\)$/;
// R3: the template preview imitates a Telegram bubble on purpose (design §3), so its tgj-tg- rules keep Telegram's colours.
const TELEGRAM_BUBBLE = /\.tgj-tg-[\w-]/;

/** Every colour that doesn't come from a --tgj- token (or a token that doesn't come from Jellyfin's palette). */
function colorProblems(css) {
    const problems = [];
    for (const rule of parseCss(css).rules) {
        if (rule.selectors.every(s => TELEGRAM_BUBBLE.test(s))) {
            continue;
        }

        const where = rule.selectors.join(', ');
        for (const { prop, value } of rule.decls) {
            if (prop.startsWith('--')) {
                if (!prop.startsWith('--tgj-')) {
                    problems.push(`${where}: custom property ${prop} is not a --tgj- token`);
                } else if (NON_COLOR_TOKEN.test(prop)) {
                    if (COLOR_LITERAL.test(value)) {
                        problems.push(`${where}: ${prop} is not a colour token but holds a colour`);
                    }
                } else if (!PALETTE.test(value) && !MIX.test(value)) {
                    problems.push(`${where}: ${prop}: ${value} is neither var(--jf-palette-..., fallback) nor a color-mix of tokens`);
                }

                continue;
            }

            if (COLOR_PROPS.test(prop) && !TOKEN.test(value) && !COLOR_KEYWORDS.test(value)) {
                problems.push(`${where}: ${prop}: ${value}`);
            }

            if (COLOR_LITERAL.test(value.replace(/var\(--[\w-]+\)/g, 'var()'))) {
                problems.push(`${where}: ${prop}: ${value} has a colour literal`);
            }

            for (const ref of value.matchAll(/var\((--[\w-]+)/g)) {
                if (!ref[1].startsWith('--tgj-')) {
                    problems.push(`${where}: ${prop} reads ${ref[1]} directly instead of a --tgj- token`);
                }
            }
        }
    }

    return problems;
}

function rootTokens() {
    const tokens = new Map();
    for (const rule of parseCss(CSS).rules) {
        if (rule.selectors.length === 1 && rule.selectors[0] === '#tgjelly-root' && rule.context.length === 0) {
            for (const { prop, value } of rule.decls.filter(d => d.prop.startsWith('--'))) {
                tokens.set(prop, value);
            }
        }
    }

    return tokens;
}

test('the scope checker catches unscoped rules, including those inside at-rules', () => {
    assert.deepEqual(scopeProblems('#tgjelly-root .a { color: x } #tgjelly-root:dir(rtl) .b {}'), []);
    assert.deepEqual(scopeProblems('.a {}'), ['.a']);
    assert.deepEqual(scopeProblems('#tgjelly-root .a, .b {}'), ['.b']);
    assert.deepEqual(scopeProblems('#tgjelly-rootx .a {}'), ['#tgjelly-rootx .a']);
    assert.deepEqual(scopeProblems('@media (x) { #tgjelly-root .a {} body {} }'), ['@media (x) body']);
    assert.deepEqual(scopeProblems('@container t (max-width: 1px) { @supports (a: b) { .c {} } }'), ['@container t (max-width: 1px) @supports (a: b) .c']);
    assert.deepEqual(scopeProblems('#tgjelly-root :is(.a, .b) {}'), []);
    assert.deepEqual(scopeProblems('@keyframes spin { from {} to {} }'), ['@keyframes spin is not tgj- prefixed']);
    assert.deepEqual(scopeProblems('@import url(x.css);'), ['not a rule: @import url(x.css)']);
    assert.deepEqual(scopeProblems('@font-face { font-family: x }'), ['unexpected at-rule: @font-face']);
    assert.deepEqual(scopeProblems('#tgjelly-root { .a { color: red } }'), ['nested rules are not allowed: #tgjelly-root']);
});

test('everySelectorScopedToRoot', () => {
    assert.deepEqual(scopeProblems(CSS), []);
    const parsed = parseCss(CSS);
    assert.ok(parsed.rules.length > 40, 'the parser found the rules');
    assert.ok(parsed.rules.some(r => r.context.some(c => c.startsWith('@container'))), 'and the ones inside @container');
    assert.ok(parsed.rules.some(r => r.context.some(c => c.startsWith('@media'))), 'and the ones inside @media');
});

test('the colour checker catches raw colours and palette reads outside tokens', () => {
    assert.deepEqual(colorProblems('#tgjelly-root { --tgj-fg: var(--jf-palette-text-primary, #fff); --tgj-x: color-mix(in srgb, var(--tgj-fg) 20%, transparent); --tgj-space-1: 0.25rem }'), []);
    assert.equal(colorProblems('#tgjelly-root .a { color: #fff }').length, 2);
    assert.equal(colorProblems('#tgjelly-root .a { background-color: var(--jf-palette-background-paper) }').length, 2);
    assert.equal(colorProblems('#tgjelly-root .a { border: 1px solid rgba(0, 0, 0, 0.1) }').length, 1);
    assert.equal(colorProblems('#tgjelly-root .a { border: 1px solid var(--jf-palette-divider) }').length, 1);
    assert.equal(colorProblems('#tgjelly-root .a { color: white }').length, 2);
    assert.equal(colorProblems('#tgjelly-root { --tgj-fg: #fff }').length, 1);
    assert.equal(colorProblems('#tgjelly-root { --tgj-fg: var(--jf-palette-text-primary) }').length, 1, 'a palette read needs a fallback');
    assert.equal(colorProblems('#tgjelly-root { --tgj-x: color-mix(in srgb, #f00 20%, transparent) }').length, 1);
    assert.equal(colorProblems('#tgjelly-root { --other: 1px }').length, 1);
    assert.deepEqual(colorProblems('#tgjelly-root .a { color: currentColor; background: transparent; border-color: inherit }'), []);
    assert.deepEqual(colorProblems('#tgjelly-root .a { border: 1px solid var(--tgj-white) }'), [], 'a token name is not a colour literal');
    assert.equal(colorProblems('#tgjelly-root .a { border: 1px solid var(--tgj-line, white) }').length, 1, 'but a fallback is');
    // R3: the Telegram bubble keeps Telegram's colours.
    assert.deepEqual(colorProblems('#tgjelly-root .tgj-tg-bubble, #tgjelly-root .tgj-tg-text a { color: #3390ec }'), []);
    assert.equal(colorProblems('#tgjelly-root .tgj-tg-bubble, #tgjelly-root .tgj-card { color: #3390ec }').length, 2, 'only when every selector is the bubble');
});

test('tokens_allColorsFromPalette', () => {
    assert.deepEqual(colorProblems(CSS), []);
    const tokens = rootTokens();
    for (const name of ['--tgj-fg', '--tgj-fg-2', '--tgj-line', '--tgj-paper', '--tgj-accent', '--tgj-success', '--tgj-warning', '--tgj-error']) {
        assert.match(tokens.get(name) ?? '', PALETTE, `${name} reads Jellyfin's palette with a fallback`);
    }

    for (const name of ['--tgj-space-1', '--tgj-space-2', '--tgj-space-3', '--tgj-space-4', '--tgj-space-5', '--tgj-step--1', '--tgj-step-0', '--tgj-step-1', '--tgj-step-2']) {
        assert.ok(tokens.has(name), `${name} is defined`);
    }

    assert.match(tokens.get('--tgj-radius') ?? '', /^var\(--jf-card-borderRadius,\s*\S+\)$/, "the radius is Jellyfin's card radius");
});

test('statusText_mixedTowardFg', () => {
    const tokens = rootTokens();
    for (const kind of ['success', 'warning', 'error', 'accent']) {
        const value = tokens.get(`--tgj-${kind}-text`) ?? '';
        assert.match(value, new RegExp(`^color-mix\\(in [\\w-]+, var\\(--tgj-${kind}\\) \\d+%, var\\(--tgj-fg\\)\\)$`), `--tgj-${kind}-text`);
    }

    const parsed = parseCss(CSS);
    const colorOf = selector => parsed.rules.find(r => r.selectors.includes(selector))?.decls.find(d => d.prop === 'color')?.value;
    assert.equal(colorOf('#tgjelly-root .tgj-label--good'), 'var(--tgj-success-text)');
    assert.equal(colorOf('#tgjelly-root .tgj-label--attention'), 'var(--tgj-warning-text)');
    assert.equal(colorOf('#tgjelly-root .tgj-label--bad'), 'var(--tgj-error-text)');
    assert.equal(colorOf('#tgjelly-root .tgj-label--accent'), 'var(--tgj-accent-text)');
    assert.equal(colorOf('#tgjelly-root .tgj-cue-reason'), 'var(--tgj-error-text)');
});

test('css_labelTonesUsePaletteTokens', () => {
    const tokens = rootTokens();
    const parsed = parseCss(CSS);
    const rule = selector => parsed.rules.find(r => r.selectors.includes(selector) && r.context.length === 0);
    const decl = (selector, prop) => rule(selector)?.decls.find(d => d.prop === prop)?.value;

    // Like the v0.3 template badge: a 1px outline, a pill, no fill.
    assert.equal(decl('#tgjelly-root .tgj-label', 'border'), '1px solid var(--tgj-line-strong)');
    assert.equal(decl('#tgjelly-root .tgj-label', 'border-radius'), '999px');
    assert.equal(decl('#tgjelly-root .tgj-label', 'white-space'), 'nowrap');

    const tones = {
        good: ['var(--tgj-success-text)', '--tgj-success-ring', '--tgj-success'],
        progress: ['var(--tgj-accent-text)', '--tgj-accent-ring', '--tgj-accent'],
        neutral: ['var(--tgj-fg-2)', '--tgj-line-strong', '--tgj-fg'],
        attention: ['var(--tgj-warning-text)', '--tgj-warning-ring', '--tgj-warning'],
        bad: ['var(--tgj-error-text)', '--tgj-error-ring', '--tgj-error'],
        accent: ['var(--tgj-accent-text)', '--tgj-accent-ring', '--tgj-accent'],
    };
    for (const [tone, [color, ring, base]] of Object.entries(tones)) {
        const selector = `#tgjelly-root .tgj-label--${tone}`;
        assert.ok(rule(selector), `${tone} has a rule`);
        assert.equal(decl(selector, 'color'), color, `${tone} text`);
        assert.equal(decl(selector, 'border-color'), `var(${ring})`, `${tone} outline`);
        // The ring is a soft tint of the tone's colour, which reads Jellyfin's palette.
        assert.match(tokens.get(ring) ?? '', new RegExp(`^color-mix\\(in srgb, var\\(${base}\\) \\d+%, transparent\\)$`), ring);
        assert.match(tokens.get(base) ?? '', /^var\(--jf-palette-/, `${base} reads the palette`);
    }

    // No fill on any label, base or tone.
    for (const r of parsed.rules.filter(r => r.selectors.some(s => /\.tgj-label(--\w+)?$/.test(s)))) {
        for (const d of r.decls.filter(d => /^background/.test(d.prop))) {
            assert.match(d.value, /^(none|transparent)$/, `${r.selectors.join(', ')} has no fill`);
        }
    }

    // The cue's reason is plain error text; no parallel badge style is left.
    assert.equal(decl('#tgjelly-root .tgj-cue-reason', 'color'), 'var(--tgj-error-text)');
    assert.ok(!parsed.rules.some(r => r.selectors.some(s => /\.tgj-(badge|state)\b/.test(s))), 'labels replace .tgj-badge and .tgj-state');
});

test('every component class is styled', () => {
    const classes = [
        'tgj-shell', 'tgj-header', 'tgj-sidebar', 'tgj-content', 'tgj-card', 'tgj-row', 'tgj-switch', 'tgj-field', 'tgj-btn',
        'tgj-btn--primary', 'tgj-btn--danger', 'tgj-confirm', 'tgj-notice', 'tgj-notice--info', 'tgj-notice--warning',
        'tgj-notice--error', 'tgj-label', 'tgj-label--good', 'tgj-label--progress', 'tgj-label--neutral', 'tgj-label--attention',
        'tgj-label--bad', 'tgj-label--accent', 'tgj-cue', 'tgj-cue--saved', 'tgj-cue--saving', 'tgj-cue--error', 'tgj-cue-reason',
        'tgj-empty',
    ];
    const selectors = parseCss(CSS).rules.flatMap(r => r.selectors).join('\n');
    for (const name of classes) {
        assert.match(selectors, new RegExp(`\\.${name}(?![\\w-])`), name);
    }
});

test('narrowLayout_usesContainerQueries_notWindowMediaQueries', () => {
    const parsed = parseCss(CSS);
    const contexts = parsed.rules.flatMap(r => r.context);
    assert.ok(!contexts.some(c => /^@media\b.*width/.test(c)), 'no @media width queries');
    assert.ok(contexts.some(c => /^@container tgjelly \(max-width: 40rem\)$/.test(c)), 'the 40rem container query');
    const root = parsed.rules.find(r => r.selectors[0] === '#tgjelly-root' && r.context.length === 0);
    assert.ok(root.decls.some(d => d.prop === 'container' && d.value === 'tgjelly / inline-size'), 'the root is the container');
});

test('cueFade_respectsReducedMotion', () => {
    const parsed = parseCss(CSS);
    const saved = parsed.rules.find(r => r.selectors.includes('#tgjelly-root .tgj-cue--saved') && r.context.length === 0);
    assert.match(saved.decls.find(d => d.prop === 'animation')?.value ?? '', /^tgj-cue-saved 2000ms\b/, 'the Saved cue fades over its 2 s');
    assert.ok(parsed.keyframes.some(k => k.name === 'tgj-cue-saved'));

    const reduced = parsed.rules.filter(r => r.context.includes('@media (prefers-reduced-motion: reduce)'));
    const cue = reduced.find(r => r.selectors.some(s => s.startsWith('#tgjelly-root .tgj-cue')));
    assert.ok(cue, 'a reduced-motion rule for the cue');
    assert.ok(cue.decls.some(d => d.prop === 'animation' && d.value === 'none'));
    assert.ok(cue.decls.some(d => d.prop === 'transition' && d.value === 'none'));
});
