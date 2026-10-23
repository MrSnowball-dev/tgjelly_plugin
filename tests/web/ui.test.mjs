// Dashboard component tests (plan 8, design §5.3). Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import * as page from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';

const SOURCE = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js', import.meta.url), 'utf8');
const EVIL = `<img src=x onerror="alert(1)">&'`;
const EVIL_ESCAPED = '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;&amp;&#39;';
// Design §7: no status symbols or emoji anywhere in the dashboard markup.
const SYMBOLS = /[●✓✔✗✘✅⚠❌]|\p{Extended_Pictographic}/u;

test('esc lives in tgjelly-ui.js and tgjelly.js re-exports it', () => {
    assert.equal(ui.esc(`<a href="x" title='y'>&</a>`), '&lt;a href=&quot;x&quot; title=&#39;y&#39;&gt;&amp;&lt;/a&gt;');
    assert.equal(ui.esc(null), '');
    assert.equal(page.esc, ui.esc);
});

test('card_escapesTitleAndDescription_bodyIsTrusted', () => {
    const html = ui.card({ title: EVIL, description: EVIL, body: '<p>body</p>', id: 'a"b' });
    assert.ok(!html.includes('<img'), html);
    assert.equal(html.split(EVIL_ESCAPED).length - 1, 2);
    assert.ok(html.includes('<p>body</p>'));
    assert.ok(html.includes('id="a&quot;b"'));
    assert.match(html, /^<section class="tgj-card"/);
    assert.ok(!ui.card({ title: 'T', body: '' }).includes('tgj-card-desc'), 'no empty description element');
});

test('row_escapesLabelAndHint', () => {
    const html = ui.row({ label: EVIL, hint: EVIL, control: '<input data-x>', cueKey: 'settings.<x>' });
    assert.ok(!html.includes('<img'), html);
    assert.equal(html.split(EVIL_ESCAPED).length - 1, 2);
    assert.ok(html.includes('<input data-x>'), 'the control is trusted HTML');
    assert.ok(html.includes('<span class="tgj-cue" data-tgj-cue="settings.&lt;x&gt;" aria-live="polite"></span>'), html);
    assert.ok(html.indexOf('tgj-cue') > html.indexOf('<input data-x>'), 'the cue sits beside the control');
});

test('row_withoutCueKey_hasNoCue_andNoEmptyHint', () => {
    const html = ui.row({ label: 'L', control: '<b>c</b>' });
    assert.ok(!html.includes('tgj-cue'));
    assert.ok(!html.includes('tgj-row-hint'));
});

test('switchControl_isAriaSwitch', () => {
    const on = ui.switchControl({ input: 'msgToggle', checked: true, label: EVIL, attrs: { 'data-tgj-key': 'ShowPoster' } });
    assert.match(on, /^<button type="button" role="switch" class="tgj-switch" aria-checked="true"/);
    assert.ok(on.includes('data-tgj-input="msgToggle"'));
    assert.ok(on.includes('data-tgj-key="ShowPoster"'));
    assert.ok(on.includes(`aria-label="${EVIL_ESCAPED}"`));
    assert.ok(!on.includes('disabled'));
    assert.ok(!on.includes('<img'));

    const off = ui.switchControl({ input: 'x', checked: false, disabled: true, label: 'L' });
    assert.ok(off.includes('aria-checked="false"'));
    assert.ok(off.includes(' disabled'));
});

test('textField_invalidSetsAriaInvalid', () => {
    const ok = ui.textField({ input: 'publicUrl', value: EVIL, placeholder: 'https://"x"' });
    assert.match(ok, /^<input type="text" class="tgj-field"/);
    assert.ok(ok.includes(`value="${EVIL_ESCAPED}"`));
    assert.ok(ok.includes('placeholder="https://&quot;x&quot;"'));
    assert.ok(!ok.includes('aria-invalid'));

    const bad = ui.textField({ input: 'publicUrl', value: 'x', type: 'url', invalid: true, attrs: { 'data-tgj-field': 'PublicUrl', autocomplete: 'off', spellcheck: false, readonly: true } });
    assert.ok(bad.includes('aria-invalid="true"'));
    assert.ok(bad.includes('type="url"'));
    assert.ok(bad.includes('data-tgj-field="PublicUrl"'));
    assert.ok(bad.includes(' readonly'));
    assert.ok(!bad.includes('spellcheck'), 'false leaves the attribute out');
});

test('textField_rejectsUnknownTypes_andEventHandlerAttrs', () => {
    assert.ok(ui.textField({ input: 'x', value: '', type: 'password' }).includes('type="password"'));
    assert.throws(() => ui.textField({ input: 'x', value: '', type: 'checkbox' }));
    assert.throws(() => ui.textField({ input: 'x', value: '', attrs: { onclick: 'x' } }));
    assert.throws(() => ui.textField({ input: 'x', value: '', attrs: { 'a b': 'x' } }));
});

test('selectField_marksTheCurrentValue_andEscapes', () => {
    const html = ui.selectField({ input: 'updMode', value: 'b', options: [{ value: 'a', label: EVIL }, { value: 'b', label: 'B' }] });
    assert.match(html, /^<select class="tgj-field tgj-select" data-tgj-input="updMode"/);
    assert.ok(html.includes(`<option value="a">${EVIL_ESCAPED}</option>`));
    assert.ok(html.includes('<option value="b" selected>B</option>'));
});

test('numberField_hasRange', () => {
    const html = ui.numberField({ input: 'batching', value: 5, min: 1, max: 60 });
    assert.match(html, /^<input type="number" class="tgj-field tgj-number"/);
    assert.ok(html.includes('min="1"') && html.includes('max="60"') && html.includes('value="5"'));
    assert.ok(html.includes('inputmode="numeric"'));
    assert.ok(!ui.numberField({ input: 'n', value: 1 }).includes('min='), 'no range attrs when unbounded');
});

test('actionButton_kinds', () => {
    assert.equal(
        ui.actionButton({ label: EVIL, act: 'remove', kind: 'danger', id: 'd"1' }),
        `<button type="button" class="tgj-btn tgj-btn--danger" data-tgj-act="remove" data-tgj-id="d&quot;1">${EVIL_ESCAPED}</button>`);
    assert.ok(ui.actionButton({ label: 'Go', act: 'go', kind: 'primary', disabled: true }).includes('class="tgj-btn tgj-btn--primary" data-tgj-act="go" disabled'));
    assert.ok(ui.actionButton({ label: 'Go', act: 'go' }).includes('class="tgj-btn" data-tgj-act="go"'), 'secondary is the default, no modifier');
    assert.throws(() => ui.actionButton({ label: 'Go', act: 'go', kind: 'loud' }));
});

test('inlineConfirm_hasNoDialogCall', () => {
    assert.ok(!/\b(alert|confirm|prompt)\s*\(/.test(SOURCE), 'no browser dialogs in tgjelly-ui.js');
    const html = ui.inlineConfirm({ question: EVIL, confirmLabel: 'Remove', cancelLabel: 'Cancel', confirmAct: 'removeYes', cancelAct: 'removeNo', id: 'd1' });
    assert.match(html, /^<div class="tgj-confirm" role="group"/);
    assert.ok(html.includes(`<span class="tgj-confirm-text">${EVIL_ESCAPED}</span>`));
    assert.ok(html.includes('class="tgj-btn tgj-btn--danger" data-tgj-act="removeYes" data-tgj-id="d1">Remove</button>'));
    assert.ok(html.includes('class="tgj-btn" data-tgj-act="removeNo" data-tgj-id="d1">'));
    assert.ok(!/\son\w+=/.test(ui.inlineConfirm({ question: 'Remove?', confirmLabel: 'Remove', cancelLabel: 'Cancel', confirmAct: 'a', cancelAct: 'b' })), 'no inline handlers');
});

test('inlineConfirm_cancelLabelIsRequiredAndEscaped', () => {
    const html = ui.inlineConfirm({ question: 'Q', confirmLabel: 'Yes', confirmAct: 'y', cancelAct: 'n', cancelLabel: '<Cancel>' });
    assert.ok(html.includes('&lt;Cancel&gt;</button>'));
    assert.throws(() => ui.inlineConfirm({ question: 'Q', confirmLabel: 'Yes', confirmAct: 'y', cancelAct: 'n' }), 'no English default');
});

test('notice_kinds', () => {
    for (const kind of ['info', 'warning', 'error']) {
        const html = ui.notice({ kind, html: '<b>trusted</b>' });
        assert.equal(html, `<div class="tgj-notice tgj-notice--${kind}"><b>trusted</b></div>`);
    }

    assert.ok(ui.notice({ html: 'x' }).includes('tgj-notice--info'), 'info is the default');
    assert.throws(() => ui.notice({ kind: 'success', html: 'x' }));
    assert.match(SOURCE, /@param \{\{ kind\?: 'info' \| 'warning' \| 'error', html: string[^}]*\}\} options html is TRUSTED HTML/, 'JSDoc marks html as trusted');
});

test('emptyState_escapesText_actionHtmlTrusted', () => {
    const html = ui.emptyState({ text: EVIL, actionHtml: '<button>Add</button>' });
    assert.ok(html.includes(EVIL_ESCAPED) && !html.includes('<img'));
    assert.ok(html.includes('<button>Add</button>'));
    assert.ok(!ui.emptyState({ text: 'x' }).includes('tgj-empty-actions'));
});

test('label_rendersOutlinedPillWithTone', () => {
    for (const tone of ['good', 'progress', 'neutral', 'attention', 'bad', 'accent']) {
        const html = ui.label('Connected', tone);
        assert.equal(html, `<span class="tgj-label tgj-label--${tone}">Connected</span>`);
        assert.ok(!SYMBOLS.test(html), html);
    }

    assert.equal(ui.label(EVIL, 'bad'), `<span class="tgj-label tgj-label--bad">${EVIL_ESCAPED}</span>`, 'the text is escaped');
    assert.equal(ui.label('x', 'shiny'), '<span class="tgj-label tgj-label--neutral">x</span>', 'an unknown tone is neutral');
    assert.equal(ui.label('x', 'constructor'), '<span class="tgj-label tgj-label--neutral">x</span>');
    assert.equal(ui.label('x'), '<span class="tgj-label tgj-label--neutral">x</span>');
    assert.equal(ui.label('x', 'accent', { id: 'a"b', title: 'T', hidden: false }),
        '<span class="tgj-label tgj-label--accent" id="a&quot;b" title="T">x</span>', 'attrs are escaped');
    assert.equal(ui.label('x', 'good', { class: 'x"y' }), '<span class="tgj-label tgj-label--good x&quot;y">x</span>',
        'a class joins the class list');
    assert.throws(() => ui.label('x', 'good', { onclick: 'alert(1)' }), 'no inline handlers');
    assert.ok(!SYMBOLS.test(SOURCE), 'no symbols anywhere in the components module');
});

test('extraClass_isAppendedAndEscaped_onEveryHelper', () => {
    const extraClass = 'x"y';
    const out = [
        ui.card({ title: 't', body: '', extraClass }),
        ui.row({ label: 'l', control: '', extraClass }),
        ui.switchControl({ input: 'i', checked: false, label: 'l', extraClass }),
        ui.textField({ input: 'i', value: '', extraClass }),
        ui.selectField({ input: 'i', value: '', options: [], extraClass }),
        ui.numberField({ input: 'i', value: 0, extraClass }),
        ui.actionButton({ label: 'l', act: 'a', extraClass }),
        ui.inlineConfirm({ question: 'q', confirmLabel: 'c', cancelLabel: 'n', confirmAct: 'a', cancelAct: 'b', extraClass }),
        ui.notice({ html: '', extraClass }),
        ui.emptyState({ text: 't', extraClass }),
        ui.label('t', 'good', { class: extraClass }),
    ];
    for (const html of out) {
        assert.match(html, /class="[^"]* x&quot;y"/, html);
    }
});
