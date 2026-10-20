// Dashboard page tests (spec §11.1). Run: node --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import { makeView, makeApiClient, fakeTimers, flush, clickOn, dispatchOn } from './fake-view.mjs';

const SOURCE = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js', import.meta.url), 'utf8');
const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));

const IDS = [
    'tgj-status', 'tgj-connect', 'tgj-bot', 'tgj-destinations', 'tgj-messages', 'tgj-advanced', 'tgj-advanced-title',
    'tgj-proxy', 'tgj-batching', 'tgj-updates', 'tgj-templates', 'tgj-manual', 'tgj-diagnostics',
];

function status(overrides = {}) {
    return {
        Version: '1.0.0.0',
        BuildTarget: '10.11',
        Compatible: true,
        CompatMessage: null,
        InstanceOwner: true,
        InstanceMessage: null,
        Revision: 3,
        BackupRestoredAt: null,
        LoadWarnings: [],
        LastErrors: [],
        ConnectionState: 'Connected',
        ConnectionMessage: 'Connected as @jf_bot',
        BotUsername: 'jf_bot',
        HasToken: true,
        Counters: { DroppedIntake: 0, DroppedOutbox: 0, Evictions: 0 },
        OutboxCount: 0,
        UnhealthyDestinations: 0,
        PendingMedia: 0,
        UpdateCheckFailing: null,
        RemovedLibraryFilters: 0,
        Notices: [],
        ...overrides,
    };
}

const saved = {};
for (const name of ['window', 'document', 'setInterval', 'clearInterval']) {
    saved[name] = Object.getOwnPropertyDescriptor(globalThis, name);
}

afterEach(() => {
    for (const [name, descriptor] of Object.entries(saved)) {
        if (descriptor) {
            Object.defineProperty(globalThis, name, descriptor);
        } else {
            delete globalThis[name];
        }
    }
});

/** Builds a page on a fake view, with fake globals; returns everything a test needs. */
function mount(routes = {}) {
    const timers = fakeTimers();
    const api = makeApiClient({
        'GET TgJelly/Strings': EN,
        'GET TgJelly/Status': status(),
        ...routes,
    });
    const doc = { hidden: false, documentElement: { lang: 'en' } };
    globalThis.window = { ApiClient: api };
    globalThis.document = doc;
    globalThis.setInterval = timers.setInterval;
    globalThis.clearInterval = timers.clearInterval;

    const view = makeView(IDS);
    view.elements.get('tgj-advanced').hidden = true; // as in the HTML, until the strings load
    const hooks = page(view, {});
    return { view, api, timers, doc, hooks, el: id => view.elements.get(id) };
}

async function show(view) {
    view.dispatchEvent(new Event('viewshow'));
    await flush();
}

// --- helpers ---

test('esc escapes all five', () => {
    assert.equal(m.esc(`<a href="x" title='y'>&</a>`), '&lt;a href=&quot;x&quot; title=&#39;y&#39;&gt;&amp;&lt;/a&gt;');
    assert.equal(m.esc(null), '');
    assert.equal(m.esc(undefined), '');
    assert.equal(m.esc(5), '5');
});

test('t fills and escapes args', () => {
    const S = { 'a.b': 'Hello {name}, {name}! {missing}' };
    assert.equal(m.t(S, 'a.b', { name: '<b>' }), 'Hello &lt;b&gt;, &lt;b&gt;! {missing}');
});

test('t escapes the text itself', () => {
    assert.equal(m.t({ k: 'Tom & <Jerry>' }, 'k'), 'Tom &amp; &lt;Jerry&gt;');
});

test('t returns key when missing', () => {
    assert.equal(m.t({}, 'no.such.key'), 'no.such.key');
    assert.equal(m.t(null, 'no.such.key'), 'no.such.key');
});

test('plural picks one/other', () => {
    const S = { 'x.one': '{count} chat in {where}', 'x.other': '{count} chats in {where}' };
    assert.equal(m.plural(S, 'x', 1, { where: 'A&B' }), '1 chat in A&amp;B');
    assert.equal(m.plural(S, 'x', 0, { where: 'A' }), '0 chats in A');
    assert.equal(m.plural(S, 'x', 1000, { where: 'A' }, 'en'), '1,000 chats in A');
});

test('plural falls back to other for a category the table lacks', () => {
    const S = { 'x.one': 'one', 'x.other': 'other {count}' };
    assert.equal(m.plural(S, 'x', 3, {}, 'ru'), 'other 3');
});

test('flatten and unflatten round-trip nested objects, keeping arrays whole', () => {
    const nested = { a: 1, b: { c: 'x', d: { e: true } }, f: [1, 2] };
    const flat = m.flatten(nested);
    assert.deepEqual(flat, { a: 1, 'b.c': 'x', 'b.d.e': true, f: [1, 2] });
    assert.deepEqual(m.unflatten(flat), nested);
});

test('mergeConflict keeps local edits, takes server elsewhere, reports both-changed paths', () => {
    const { merged, changed } = m.mergeConflict({ a: 1, b: 1, c: 1 }, { a: 2, b: 1, c: 3 }, { a: 1, b: 5, c: 4 });
    assert.deepEqual(merged, { a: 2, b: 5, c: 3 });
    assert.deepEqual(changed, ['c']);
});

test('mergeConflict compares arrays by value', () => {
    const { merged, changed } = m.mergeConflict({ l: ['x'] }, { l: ['x'] }, { l: ['y'] });
    assert.deepEqual(merged, { l: ['y'] });
    assert.deepEqual(changed, []);
});

// --- renderStatus ---

test('renderStatus escapes notice text', () => {
    const html = m.renderStatus(status({ Notices: [{ Id: 'n1', Kind: 'X', Text: '<img src=x onerror=alert(1)>' }] }), EN);
    assert.ok(!html.includes('<img src=x onerror=alert(1)>'));
    assert.ok(html.includes('&lt;img src=x onerror=alert(1)&gt;'));
    assert.ok(html.includes('data-tgj-act="dismiss"'));
    assert.ok(html.includes('data-tgj-id="n1"'));
});

test('renderStatus shows take over only on WebhookConflict', () => {
    const conflict = m.renderStatus(status({ ConnectionState: 'WebhookConflict', ConnectionMessage: 'webhook' }), EN);
    const connected = m.renderStatus(status(), EN);
    assert.ok(conflict.includes('data-tgj-act="takeover"'));
    assert.ok(!connected.includes('data-tgj-act="takeover"'));
});

test('renderStatus shows the compat banner when incompatible', () => {
    const html = m.renderStatus(status({ Compatible: false, CompatMessage: 'Install the <12> build', ConnectionState: 'Incompatible' }), EN);
    assert.ok(html.includes('tgj-error'));
    assert.ok(html.includes('Install the &lt;12&gt; build'));
    assert.ok(!m.renderStatus(status(), EN).includes('tgj-compat'));
});

test('renderStatus shows the connection message, counts and last errors, escaped', () => {
    const html = m.renderStatus(status({
        ConnectionMessage: 'Connected as @a<b',
        PendingMedia: 4,
        OutboxCount: 7,
        Counters: { DroppedIntake: 1, DroppedOutbox: 2, Evictions: 3 },
        LastErrors: [{ Source: 'outbox', Message: 'chat <gone>', At: '2026-10-20T10:00:00Z' }],
    }), EN);
    assert.ok(html.includes('Connected as @a&lt;b'));
    assert.ok(html.includes(m.t(EN, 'web.status.counts', { pending: '4', outbox: '7', dropped: '6' })));
    assert.ok(html.includes('chat &lt;gone&gt;'));
    assert.ok(!html.includes('chat <gone>'));
});

// --- controller ---

test('viewshow loads strings and status, and renders the status', async () => {
    const { view, api, el } = mount();
    await show(view);

    assert.deepEqual(api.calls.map(c => `${c.type} ${c.url}`), ['GET /jf/TgJelly/Strings?lang=en', 'GET /jf/TgJelly/Status']);
    assert.ok(el('tgj-status').innerHTML.includes('Connected as @jf_bot'));
    assert.equal(el('tgj-advanced-title').innerHTML, m.t(EN, 'web.advanced'));
    assert.equal(el('tgj-advanced').hidden, false);
});

test('viewshow twice binds one poller', async () => {
    const { view, timers, api } = mount();
    await show(view);
    await show(view);

    assert.equal(timers.active(), 1);
    assert.deepEqual(timers.intervals(), [3000]);
    assert.equal(api.calls.filter(c => c.url.includes('Strings')).length, 1);
});

test('polling stops on viewhide and when hidden', async () => {
    const { view, timers, api, doc } = mount();
    await show(view);
    view.dispatchEvent(new Event('viewhide'));
    assert.equal(timers.active(), 0);

    for (const name of ['viewbeforehide', 'viewdestroy']) {
        await show(view);
        view.dispatchEvent(new Event(name));
        assert.equal(timers.active(), 0, name);
    }

    await show(view);
    const before = api.calls.length;
    doc.hidden = true;
    timers.tick();
    await flush();
    assert.equal(api.calls.length, before, 'paused while document.hidden');
    assert.equal(timers.active(), 1);

    doc.hidden = false;
    timers.tick();
    await flush();
    assert.equal(api.calls.length, before + 1, 'polls Status when visible again');

    view.classList.add('hide');
    timers.tick();
    await flush();
    assert.equal(timers.active(), 0, 'stops when the view is hidden');

    view.classList.remove('hide');
    await show(view);
    view.isConnected = false;
    timers.tick();
    assert.equal(timers.active(), 0, 'stops when the view is detached');
});

test('a poll re-renders only the status', async () => {
    const { view, timers, el, hooks, api } = mount({ 'GET TgJelly/Status': status({ ConnectionMessage: 'first' }) });
    let loads = 0;
    let renders = 0;
    hooks.sections.demo = { id: 'tgj-connect', load: async () => { loads++; }, render: () => `demo ${++renders}` };
    await show(view);
    assert.equal(el('tgj-connect').innerHTML, 'demo 1');

    api.fetch = makeApiClient({ 'GET TgJelly/Status': status({ ConnectionMessage: 'second' }) }).fetch;
    timers.tick();
    await flush();

    assert.ok(el('tgj-status').innerHTML.includes('second'));
    assert.equal(loads, 1);
    assert.equal(renders, 1);
    assert.equal(el('tgj-connect').innerHTML, 'demo 1');
});

test('an identical poll does not rewrite the status, even with an apostrophe the browser serializes differently', async () => {
    const { view, timers, el } = mount({
        'GET TgJelly/Status': status({ Notices: [{ Id: 'n1', Kind: 'X', Text: "The bot's chat is gone" }] }),
    });
    await show(view);
    assert.equal(el('tgj-status').writes, 1);

    timers.tick();
    await flush();
    timers.tick();
    await flush();
    assert.equal(el('tgj-status').writes, 1);
});

test('a poll does not overlap the initial status load', async () => {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const { view, timers, api } = mount();
    const fetch = api.fetch;
    api.fetch = async request => (request.url.endsWith('/Status') ? gate.then(() => fetch(request)) : fetch(request));

    view.dispatchEvent(new Event('viewshow'));
    await flush();
    timers.tick();
    timers.tick();
    release();
    await flush();

    assert.equal(api.calls.filter(c => c.url.endsWith('/Status')).length, 1);
});

test('change and input events reach the inputs map', async () => {
    const { view, hooks } = mount();
    const seen = [];
    hooks.inputs.demo = (el, event) => seen.push([event.type, el.value]);
    await show(view);

    dispatchOn(view, 'change', { 'data-tgj-input': 'demo' }, { value: 'a' });
    dispatchOn(view, 'input', { 'data-tgj-input': 'demo' }, { value: 'ab' });
    dispatchOn(view, 'change', { 'data-tgj-input': 'other' }, { value: 'x' });
    dispatchOn(view, 'change', {}, { value: 'y' });
    await flush();

    assert.deepEqual(seen, [['change', 'a'], ['input', 'ab']]);
});

test('an action error lands in its own section', async () => {
    const { view, hooks, el } = mount({ 'POST TgJelly/Boom': { status: 400, body: { Message: 'bad <x>' } } });
    hooks.sections.demo = { id: 'tgj-messages', load: async () => {}, render: () => '<p>demo</p>' };
    hooks.actions.boom = () => hooks.runAction('demo', () => hooks.api.post('Boom'));
    hooks.actions.crash = () => hooks.runAction('demo', async () => { throw new Error('kaput'); });
    await show(view);

    clickOn(view, { 'data-tgj-act': 'boom' });
    await flush();
    assert.ok(el('tgj-messages').innerHTML.includes('<p class="tgj-error">bad &lt;x&gt;</p>'));
    assert.ok(el('tgj-messages').innerHTML.includes('<p>demo</p>'));
    assert.ok(!el('tgj-status').innerHTML.includes('bad'));

    clickOn(view, { 'data-tgj-act': 'crash' });
    await flush();
    assert.ok(el('tgj-messages').innerHTML.includes(m.t(EN, 'web.requestFailed', { reason: 'kaput' })));
    assert.ok(!el('tgj-messages').innerHTML.includes('bad'));
});

test('a dirty form survives a re-show and a poll, merged onto fresh data', async () => {
    let server = { PublicUrl: 'https://a', Batch: { Seconds: 30 } };
    const { view, hooks, el, timers } = mount({ 'GET TgJelly/Settings': () => server });
    hooks.sections.demo = {
        id: 'tgj-messages',
        load: async () => hooks.syncForm('demo', (await hooks.api.get('Settings')).body),
        render: () => `${hooks.state.forms.demo.local.PublicUrl} ${hooks.state.forms.demo.local.Batch.Seconds}`,
    };
    await show(view);
    assert.equal(el('tgj-messages').innerHTML, 'https://a 30');

    hooks.markDirty('demo', 'PublicUrl', 'https://mine');
    server = { PublicUrl: 'https://theirs', Batch: { Seconds: 60 } };
    await show(view);
    timers.tick();
    await flush();

    const form = hooks.state.forms.demo;
    assert.equal(el('tgj-messages').innerHTML, 'https://mine 60');
    assert.deepEqual(form.local, { PublicUrl: 'https://mine', Batch: { Seconds: 60 } });
    assert.deepEqual(form.base, server);
    assert.equal(form.dirty, true);
    assert.deepEqual(form.changed, ['PublicUrl']);

    hooks.resetForm('demo', server);
    await hooks.reloadSection('demo');
    assert.equal(el('tgj-messages').innerHTML, 'https://theirs 60');
    assert.equal(hooks.state.forms.demo.dirty, false);
});

test('syncFormData replaces a clean form and merges a dirty one', () => {
    const clean = m.syncFormData(null, { a: 1, b: { c: 2 } });
    assert.deepEqual(clean, { base: { a: 1, b: { c: 2 } }, local: { a: 1, b: { c: 2 } }, dirty: false, changed: [] });
    assert.notEqual(clean.local.b, clean.base.b, 'local is a copy');

    const edited = m.editForm(clean, 'b.c', 5);
    assert.equal(edited.dirty, true);
    assert.deepEqual(edited.local, { a: 1, b: { c: 5 } });
    assert.deepEqual(clean.local, { a: 1, b: { c: 2 } }, 'editForm does not mutate');

    const synced = m.syncFormData(edited, { a: 9, b: { c: 7 } });
    assert.deepEqual(synced.local, { a: 9, b: { c: 5 } });
    assert.deepEqual(synced.changed, ['b.c']);
    assert.equal(synced.dirty, true);

    assert.equal(m.syncFormData(m.editForm(clean, 'a', 4), { a: 4, b: { c: 2 } }).dirty, false, 'nothing left to save');
});

test('all requests go through getUrl', async () => {
    const { view, api, timers } = mount({
        'GET TgJelly/Status': status({ Notices: [{ Id: 'n1', Kind: 'X', Text: 'x' }], ConnectionState: 'WebhookConflict' }),
        'POST TgJelly/Status/Dismiss': { status: 204 },
        'POST TgJelly/Webhook/TakeOver': { status: 204 },
    });
    await show(view);
    timers.tick();
    clickOn(view, { 'data-tgj-act': 'dismiss', 'data-tgj-id': 'n1' });
    clickOn(view, { 'data-tgj-act': 'takeover' });
    await flush();

    assert.ok(api.calls.length >= 5);
    for (const call of api.calls) {
        assert.ok(call.url.startsWith('/jf/TgJelly/'), call.url);
        assert.equal(call.contentType, 'application/json');
        assert.equal(call.dataType, undefined);
    }
    assert.ok(!SOURCE.includes("getUrl('/"));
    assert.ok(!SOURCE.includes('api.telegram.org'));
});

test('dismiss posts the notice id', async () => {
    const { view, api } = mount({
        'GET TgJelly/Status': status({ Notices: [{ Id: 'n1', Kind: 'X', Text: 'x' }] }),
        'POST TgJelly/Status/Dismiss': { status: 204 },
    });
    await show(view);
    const { event } = clickOn(view, { 'data-tgj-act': 'dismiss', 'data-tgj-id': 'n1' });
    await flush();

    const post = api.calls.find(c => c.type === 'POST');
    assert.equal(post.url, '/jf/TgJelly/Status/Dismiss');
    assert.deepEqual(JSON.parse(post.data), { NoticeId: 'n1' });
    assert.ok(event.defaultPrevented);
    assert.equal(api.calls.at(-1).url, '/jf/TgJelly/Status', 'refreshes the status after');
});

test('take over shows the server message when Telegram refuses', async () => {
    const { view, el } = mount({
        'GET TgJelly/Status': status({ ConnectionState: 'WebhookConflict', ConnectionMessage: 'webhook' }),
        'POST TgJelly/Webhook/TakeOver': { status: 400, body: { Message: 'Telegram said <no>' } },
    });
    await show(view);
    clickOn(view, { 'data-tgj-act': 'takeover' });
    await flush();

    assert.ok(el('tgj-status').innerHTML.includes('Telegram said &lt;no&gt;'));
});

test('an unknown action is ignored', async () => {
    const { view, api } = mount();
    await show(view);
    const before = api.calls.length;
    const { event } = clickOn(view, { 'data-tgj-act': 'nope' });
    await flush();
    assert.equal(api.calls.length, before);
    assert.ok(!event.defaultPrevented);
});

test('strings failure shows the fallback line', async t => {
    const errors = [];
    t.mock.method(console, 'error', (...args) => errors.push(args));
    const { view, el, timers } = mount({ 'GET TgJelly/Strings': { status: 500, body: { Message: 'boom' } } });
    await show(view);

    assert.equal(el('tgj-status').textContent, 'Could not load TgJelly.');
    assert.equal(el('tgj-advanced').hidden, true, 'the empty Advanced block stays hidden');
    assert.equal(errors.length, 1);
    assert.equal(timers.active(), 0);
});

test('a network failure loading strings shows the fallback line', async t => {
    t.mock.method(console, 'error', () => {});
    const { view, el } = mount({ NETWORK: true });
    await show(view);
    assert.equal(el('tgj-status').textContent, 'Could not load TgJelly.');
});

test('a failed status poll says so, escaped through the strings', async t => {
    t.mock.method(console, 'error', () => {});
    const { view, el } = mount({ 'GET TgJelly/Status': { status: 500, body: { Message: 'x' } } });
    await show(view);
    assert.ok(el('tgj-status').innerHTML.includes(m.t(EN, 'web.status.failed')));
});

// --- createApi ---

test('createApi resolves status and body, a 204 as null, and a 4xx as a value', async () => {
    const client = makeApiClient({
        'GET TgJelly/Status': { status: 200, body: { A: 1 } },
        'POST TgJelly/Status/Dismiss': { status: 204 },
        'PUT TgJelly/Settings': { status: 409, body: { Revision: 4 } },
        'DELETE TgJelly/Destinations/x': { status: 400, body: { Message: 'no' } },
    });
    const api = m.createApi(client);

    assert.deepEqual(await api.get('Status'), { status: 200, body: { A: 1 } });
    assert.deepEqual(await api.post('Status/Dismiss', { NoticeId: 'a' }), { status: 204, body: null });
    assert.deepEqual(await api.put('Settings', { expectedRevision: 3 }), { status: 409, body: { Revision: 4 } });
    assert.deepEqual(await api.del('Destinations/x?expectedRevision=3'), { status: 400, body: { Message: 'no' } });

    assert.deepEqual(client.calls.map(c => c.type), ['GET', 'POST', 'PUT', 'DELETE']);
    assert.equal(client.calls[0].data, undefined);
    assert.equal(client.calls[2].data, JSON.stringify({ expectedRevision: 3 }));
    assert.equal(client.calls[3].url, '/jf/TgJelly/Destinations/x?expectedRevision=3');
});

test('createApi throws on a network failure', async () => {
    const api = m.createApi(makeApiClient({ NETWORK: true }));
    await assert.rejects(api.get('Status'), TypeError);
});

// --- source checks ---

test('every t(…) key used in tgjelly.js exists in en.json', () => {
    const keys = [...SOURCE.matchAll(/\bt\(S, '([^']+)'/g)].map(x => x[1]);
    const plurals = [...SOURCE.matchAll(/\bplural\(S, '([^']+)'/g)].map(x => x[1]);
    assert.ok(keys.length > 0);
    for (const key of keys) {
        assert.ok(key in EN, key);
    }
    for (const key of plurals) {
        assert.ok(`${key}.one` in EN && `${key}.other` in EN, key);
    }
    assert.ok(!/\bt\((?!S, )[a-z]/.test(SOURCE.replace(/function t\(/, '')), 'every t( call passes S first');
});

test('every web.* key in en.json is used by tgjelly.js', () => {
    for (const key of Object.keys(EN).filter(k => k.startsWith('web.'))) {
        const base = key.replace(/\.(one|other)$/, '');
        assert.ok(SOURCE.includes(`'${base}'`), key);
    }
});
