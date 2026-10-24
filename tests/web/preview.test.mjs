// The template preview (plan 9 Task 10, design §6.4-6.5): Telegram's own look, and the pieces the hover link uses.
// Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as p from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-preview.js';

const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));
const CSS = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');

// The server's markers: U+E000 + index + U+E001 before a piece's output, U+E002 after it.
const piece = (index, html) => `\uE000${index}\uE001${html}\uE002`;
const MARKERS = /[\uE000-\uE002]/;

const validation = overrides => ({ Errors: [], Warnings: [], Preview: '', Length: 10, Limit: 1024, Html: 'x', Caption: true, Poster: false, Buttons: [], ...overrides });

test('previewHtml_markersBecomePieceSpans', () => {
    const marked = piece(0, '<b>') + piece(1, 'Dune') + piece(2, '</b> (') + piece(3, '2021') + piece(4, ')');
    assert.equal(p.previewHtml(marked),
        '<b><span data-tgj-piece="1">Dune</span></b><span data-tgj-piece="2"> (</span><span data-tgj-piece="3">2021</span><span data-tgj-piece="4">)</span>',
        'a piece made only of tags has no span');

    // Several text runs of one piece each get a span; entities decode inside them.
    assert.equal(p.previewHtml(piece(0, 'a <i>b &amp; c</i> d')),
        '<span data-tgj-piece="0">a </span><i><span data-tgj-piece="0">b &amp; c</span></i><span data-tgj-piece="0"> d</span>');

    // A piece inside an attribute never reaches it: the href is clean, the link text is its own piece.
    assert.equal(p.previewHtml(`<a href="${piece(0, 'https://x.example/')}">${piece(1, 'link')}</a>`),
        '<a target="_blank" rel="noopener noreferrer" href="https://x.example/"><span data-tgj-piece="1">link</span></a>');

    // The expandable quote's summary and body keep their pieces.
    assert.equal(p.previewHtml(`<blockquote expandable>${piece(0, 'one\ntwo')}</blockquote>`),
        '<details class="tgj-tg-quote"><summary><span data-tgj-piece="0">one</span></summary><span data-tgj-piece="0">two</span></details>');

    // A broken marker never shows, and text outside any piece is plain.
    assert.equal(p.previewHtml('a\uE000\uE002b\uE001c'), 'abc');
    assert.equal(p.previewHtml('plain <b>text</b>'), 'plain <b>text</b>');
});

test('previewHtml_unknownTagsStillStripped', () => {
    const html = p.previewHtml(piece(0, '<script>alert(1)</script>') + piece(1, '<img src=x onerror=y>') + piece(2, '<span style="color:red">r</span>'));
    assert.equal(html, '<span data-tgj-piece="0">&lt;script&gt;alert(1)&lt;/script&gt;</span><span data-tgj-piece="1">&lt;img src=x onerror=y&gt;</span><span data-tgj-piece="2">r</span>');
    assert.ok(!MARKERS.test(html));
    assert.equal(p.previewHtml(`<b onclick="${piece(0, 'x')}">y</b>`), '<b>y</b>');
    assert.equal(p.previewHtml(piece(0, '<b>unclosed')), '<span data-tgj-piece="0">&lt;b&gt;unclosed</span>');
});

test('preview_buttonsUnderBubbleInRows', () => {
    const html = p.renderTemplatePreview(validation({
        Html: 'Dune',
        Buttons: [[{ Text: 'Open in Jellyfin', Url: 'https://jf.example/web/' }], [{ Text: 'IMDb', Url: 'https://imdb.example' }, { Text: 'TMDB', Url: 'javascript:alert(1)' }]],
    }), EN, null, '13:42');
    const bubbleEnd = html.indexOf('</div>', html.indexOf('class="tgj-tg-text"'));
    const keys = html.indexOf('class="tgj-tg-buttons"');
    assert.ok(keys > bubbleEnd, 'the keys come after the bubble, not inside it');
    const rows = [...html.slice(keys).matchAll(/<div class="tgj-tg-row">([\s\S]*?)<\/div>/g)].map(r => [...r[1].matchAll(/class="tgj-tg-button[^"]*"[^>]*>([^<]*)</g)].map(k => k[1]));
    assert.deepEqual(rows, [['Open in Jellyfin'], ['IMDb', 'TMDB']], 'the reply_markup rows');
    assert.equal((html.match(/class="tgj-tg-button tgj-tg-url"/g) ?? []).length, 2, 'only a real link gets the link mark');
    assert.ok(!html.includes('javascript:'));
    assert.ok(!html.includes('↗'), 'the mark lives in the stylesheet');
    assert.match(CSS, /#tgjelly-root \.tgj-tg-url::after \{[^}]*content: "↗";/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-button \{[^}]*background: rgba\(/, 'a translucent fill');
    assert.ok(!p.renderTemplatePreview(validation({ Buttons: [] }), EN, null, '13:42').includes('tgj-tg-buttons'));
});

test('preview_hasTimeInBubble', () => {
    const html = p.renderTemplatePreview(validation({ Html: 'Dune <i>2021</i>', Poster: true }), EN, null, '13:42');
    const bubble = html.indexOf('class="tgj-tg-bubble');
    const photo = html.indexOf('class="tgj-tg-poster"');
    const text = html.indexOf('class="tgj-tg-text"');
    const time = html.indexOf('<span class="tgj-tg-time" aria-hidden="true"><span>13:42</span></span>');
    assert.ok(bubble >= 0 && bubble < photo && photo < text && text < time, html);
    assert.ok(time < html.indexOf('</div>', text), 'the time sits inside the text, at its end');
    assert.ok(html.indexOf('class="tgj-tg-chat"') < bubble, 'on Telegram\'s chat background');

    // The marked HTML is what the bubble shows when the server sends it.
    const marked = p.renderTemplatePreview(validation({ Html: 'Dune', MarkedHtml: piece(0, 'Dune') }), EN, null, '13:42');
    assert.ok(marked.includes('<span data-tgj-piece="0">Dune</span>'), marked);
    assert.match(p.renderTemplatePreview(validation(), EN), /<span class="tgj-tg-time" aria-hidden="true"><span>\d{1,2}:\d{2}/);

    // Telegram's look: a tail, the time in the corner, link blue, the quote bar, monospace code, a dotted spoiler.
    assert.match(CSS, /#tgjelly-root \.tgj-tg-bubble::before \{/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-time > span \{[^}]*position: absolute;[^}]*right:[^}]*bottom:/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-text a \{[^}]*color: #6ab3f3;/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-text blockquote,\s*#tgjelly-root \.tgj-tg-quote \{[^}]*border-left: 3px solid #6ab3f3;/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-text code,\s*#tgjelly-root \.tgj-tg-text pre \{[^}]*font-family: [^;]*monospace;/);
    assert.match(CSS, /#tgjelly-root \.tgj-tg-spoiler \{[^}]*radial-gradient/);
});
