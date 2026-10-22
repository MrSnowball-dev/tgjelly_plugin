// Dashboard page tests (spec §11.1). Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import { makeView, makeApiClient, makeResponse, fakeTimers, flush, clickOn, dispatchOn, liveFields } from './fake-view.mjs';

const SOURCE = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js', import.meta.url), 'utf8');
const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));

// The page has only #tgjelly-root; the shell the controller writes into it adds the rest (makeView registers them).
// The section hosts are listed too, so a test can hold an element before the shell is written.
const IDS = [
    'tgjelly-root', 'tgj-status', 'tgj-connect', 'tgj-bot', 'tgj-summary', 'tgj-destinations', 'tgj-messages', 'tgj-demo',
    'tgj-proxy', 'tgj-batching', 'tgj-updates', 'tgj-templates', 'tgj-tpl-preview', 'tgj-manual', 'tgj-diagnostics',
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
        ConnectionMessage: 'Connected to Telegram',
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

function settings(overrides = {}) {
    return {
        Revision: 5,
        PublicUrl: '',
        PublicUrlValid: true,
        PublicUrlSuggestion: '',
        Message: { ShowPoster: true, ShowOverview: true, ShowRatingGenres: true, ShowOpenInJellyfin: true, ShowProviderLinks: false, AnnounceUpgrades: false },
        Batching: { QuietWindowSeconds: 120, MaxHoldMinutes: 30, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 },
        UpdateCheck: { Enabled: true, Mode: 'SameMajor' },
        ProxyMode: 'System',
        ProxyUrl: '',
        HasProxyUsername: false,
        HasProxyPassword: false,
        ...overrides,
    };
}

const LINKS = {
    OpenBot: 'https://t.me/jf_bot?start=setup',
    StartAdd: 'https://t.me/jf_bot?start=add',
    StartGroup: 'https://t.me/jf_bot?startgroup=add',
    StartChannel: 'https://t.me/jf_bot?startchannel',
};

const KEYS = ['media.movie', 'media.episodes', 'security.login_failed', 'server.started'];
const CATALOG = {
    EventKeys: KEYS,
    Groups: ['media', 'security', 'server'],
    Presets: { Everything: KEYS, NewMedia: ['media.movie', 'media.episodes'], AdminAlerts: ['security.login_failed', 'server.started'] },
};
const LIB_A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const LIB_B = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const LIBRARIES = [{ Id: LIB_A, Name: 'Movies', CollectionType: 'movies' }, { Id: LIB_B, Name: 'Shows <i>', CollectionType: 'tvshows' }];

function dest(overrides = {}) {
    return {
        Id: 'd1',
        ChatId: -100123,
        ThreadId: 0,
        Title: 'Family',
        Kind: 'Supergroup',
        Enabled: true,
        Preset: 'Everything',
        Events: KEYS,
        LibraryIds: [],
        SilentGroups: [],
        RemovedLibraryIds: [],
        LibraryFilterBroken: false,
        Health: { Status: 'Ok', PhotosBlocked: false, LastError: null, At: null },
        ...overrides,
    };
}

function destinations(overrides = {}) {
    return { Revision: 7, Items: [dest()], DetectedChats: [], ManualUnlocked: false, Catalog: CATALOG, ...overrides };
}

const saved = {};
for (const name of ['window', 'document', 'setInterval', 'clearInterval', 'setTimeout', 'clearTimeout']) {
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

/**
 * Builds a page on a fake view, with fake globals; returns everything a test needs. hash is the page URL's hash; the
 * fake history keeps the router's state object, as Jellyfin's router stores one there.
 */
function mount(routes = {}, { hash = '#/configurationpage?name=TgJelly' } = {}) {
    const timers = fakeTimers();
    const api = makeApiClient({
        'GET TgJelly/Strings': EN,
        'GET TgJelly/Status': status(),
        'GET TgJelly/Links': LINKS,
        'GET TgJelly/Settings': settings(),
        'GET TgJelly/Destinations': destinations(),
        'GET TgJelly/Libraries': LIBRARIES,
        ...routes,
    });
    const doc = { hidden: false, documentElement: { lang: 'en' } };
    const location = { hash };
    const history = {
        state: { idx: 3 },
        replaced: [],
        replaceState(state, title, url) {
            this.replaced.push(url);
            this.state = state;
            location.hash = String(url);
        },
    };
    globalThis.window = { ApiClient: api, location, history };
    globalThis.document = doc;
    globalThis.setInterval = timers.setInterval;
    globalThis.clearInterval = timers.clearInterval;
    globalThis.setTimeout = timers.setTimeout;
    globalThis.clearTimeout = timers.clearTimeout;

    const view = makeView(IDS);
    const hooks = page(view, {});
    return { view, api, timers, doc, hooks, win: globalThis.window, el: id => view.elements.get(id) };
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
    assert.ok(html.includes('tgj-notice--error') && html.includes('tgj-compat'));
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

    assert.deepEqual(api.calls.map(c => `${c.type} ${c.url}`), [
        'GET /jf/TgJelly/Strings?lang=en',
        'GET /jf/TgJelly/Status',
        'GET /jf/TgJelly/Links',
        'GET /jf/TgJelly/Settings',
        'GET /jf/TgJelly/Destinations',
        'GET /jf/TgJelly/Libraries',
        'GET /jf/TgJelly/Templates',
    ]);
    assert.ok(el('tgj-status').innerHTML.includes('Connected to Telegram'));
    assert.equal(el('tgjelly-root').writes, 1, 'the shell is written once');
    assert.ok(el('tgjelly-root').innerHTML.includes(`<span class="tgj-nav-label">${m.t(EN, 'web.nav.advanced')}</span>`));
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
    hooks.sections.demo = { id: 'tgj-demo', load: async () => { loads++; }, render: () => `demo ${++renders}` };
    await show(view);
    assert.equal(el('tgj-demo').innerHTML, 'demo 1');

    api.fetch = makeApiClient({ 'GET TgJelly/Status': status({ ConnectionMessage: 'second' }) }).fetch;
    timers.tick();
    await flush();

    assert.ok(el('tgj-status').innerHTML.includes('second'));
    assert.equal(loads, 1);
    assert.equal(renders, 1);
    assert.equal(el('tgj-demo').innerHTML, 'demo 1');
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
    hooks.sections.demo = { id: 'tgj-demo', load: async () => {}, render: () => '<p>demo</p>' };
    hooks.actions.boom = () => hooks.runAction('demo', () => hooks.api.post('Boom'));
    hooks.actions.crash = () => hooks.runAction('demo', async () => { throw new Error('kaput'); });
    await show(view);

    clickOn(view, { 'data-tgj-act': 'boom' });
    await flush();
    assert.ok(el('tgj-demo').innerHTML.includes('<p class="tgj-error">bad &lt;x&gt;</p>'));
    assert.ok(el('tgj-demo').innerHTML.includes('<p>demo</p>'));
    assert.ok(!el('tgj-status').innerHTML.includes('bad'));

    clickOn(view, { 'data-tgj-act': 'crash' });
    await flush();
    assert.ok(el('tgj-demo').innerHTML.includes(m.t(EN, 'web.requestFailed', { reason: 'kaput' })));
    assert.ok(!el('tgj-demo').innerHTML.includes('bad'));
});

test('a dirty form survives a re-show and a poll, merged onto fresh data', async () => {
    let server = { PublicUrl: 'https://a', Batch: { Seconds: 30 } };
    const { view, hooks, el, timers } = mount({ 'GET TgJelly/Settings': () => server });
    hooks.sections.demo = {
        id: 'tgj-demo',
        load: async () => hooks.syncForm('demo', (await hooks.api.get('Settings')).body),
        render: () => `${hooks.state.forms.demo.local.PublicUrl} ${hooks.state.forms.demo.local.Batch.Seconds}`,
    };
    await show(view);
    assert.equal(el('tgj-demo').innerHTML, 'https://a 30');

    hooks.markDirty('demo', 'PublicUrl', 'https://mine');
    server = { PublicUrl: 'https://theirs', Batch: { Seconds: 60 } };
    await show(view);
    timers.tick();
    await flush();

    const form = hooks.state.forms.demo;
    assert.equal(el('tgj-demo').innerHTML, 'https://mine 60');
    assert.deepEqual(form.local, { PublicUrl: 'https://mine', Batch: { Seconds: 60 } });
    assert.deepEqual(form.base, server);
    assert.equal(form.dirty, true);
    assert.deepEqual(form.changed, ['PublicUrl']);

    hooks.resetForm('demo', server);
    await hooks.reloadSection('demo');
    assert.equal(el('tgj-demo').innerHTML, 'https://theirs 60');
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

    assert.equal(el('tgjelly-root').textContent, 'Could not load TgJelly.');
    assert.equal(el('tgjelly-root').writes, 0, 'no shell without the strings');
    assert.equal(errors.length, 1);
    assert.equal(timers.active(), 0);
});

test('a network failure loading strings shows the fallback line', async t => {
    t.mock.method(console, 'error', () => {});
    const { view, el } = mount({ NETWORK: true });
    await show(view);
    assert.equal(el('tgjelly-root').textContent, 'Could not load TgJelly.');
});

test('a failed status poll says so, escaped through the strings', async t => {
    t.mock.method(console, 'error', () => {});
    const { view, el } = mount({ 'GET TgJelly/Status': { status: 500, body: { Message: 'x' } } });
    await show(view);
    assert.ok(el('tgj-status').innerHTML.includes(m.t(EN, 'web.status.failed')));
});

// --- core fixes from the Task 7 review ---

test('markDirty on a form that does not exist yet does nothing', async () => {
    const { view, hooks } = mount();
    await show(view);
    assert.doesNotThrow(() => hooks.markDirty('nope', 'A', 1));
    assert.doesNotThrow(() => hooks.markDirty('nope'));
    assert.equal('nope' in hooks.state.forms, false);
});

test('a later successful reload clears a section load error', async t => {
    t.mock.method(console, 'error', () => {});
    let fail = true;
    const { view, hooks, el } = mount();
    hooks.sections.demo = {
        id: 'tgj-demo',
        load: async () => {
            if (fail) {
                throw new Error('down');
            }
        },
        render: () => '<p>demo</p>',
    };
    await show(view);
    assert.ok(el('tgj-demo').innerHTML.includes('class="tgj-error"'));

    fail = false;
    await hooks.reloadSection('demo');
    assert.ok(!el('tgj-demo').innerHTML.includes('tgj-error'));
    assert.ok(el('tgj-demo').innerHTML.includes('<p>demo</p>'));
});

test('a throwing render is caught inside reloadSection', async t => {
    const errors = [];
    t.mock.method(console, 'error', (...args) => errors.push(args));
    const { view, hooks } = mount();
    hooks.sections.demo = {
        id: 'tgj-demo',
        load: async () => {},
        render: () => {
            throw new Error('bad render');
        },
    };
    await show(view);
    await assert.doesNotReject(hooks.reloadSection('demo'));
    assert.ok(errors.length > 0);
});

// --- connecting a bot (spec §6.1, §6.4, §6.6) ---

const noToken = () => status({ HasToken: false, BotUsername: null, ConnectionState: 'NoToken', ConnectionMessage: 'No bot connected' });
const input = (view, name, value, extra = {}) => dispatchOn(view, 'input', { 'data-tgj-input': name }, { value, ...extra });
const click = async (view, act) => {
    clickOn(view, { 'data-tgj-act': act });
    await flush();
};
const posted = (api, path) => api.calls.filter(c => c.type === 'POST' && c.url === `/jf/TgJelly/${path}`).map(c => JSON.parse(c.data));

test('first run shows both choices', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': noToken() });
    await show(view);
    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes('Connect a Telegram bot'));
    assert.ok(html.includes('Create a new bot'));
    assert.ok(html.includes('I already have a bot'));
    assert.ok(!html.includes('type="password"'), 'the form waits for a choice');
    assert.equal(el('tgj-bot').innerHTML, '');
});

test('the card is hidden once a token is saved', async () => {
    const { view, el } = mount();
    await show(view);
    assert.equal(el('tgj-connect').innerHTML, '');
});

test('create-new steps mention the Mini App', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': noToken() });
    await show(view);
    await click(view, 'connectNew');
    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes('https://t.me/BotFather'));
    assert.ok(html.includes('/newbot'));
    assert.ok(html.includes("In BotFather's Mini App, restrict access to yourself and anyone else who should manage Jellyfin (up to 10 people)."));
    assert.ok(html.includes('Copy the token and paste it below.'));
    assert.ok(html.includes('type="password"'));
    assert.ok(!html.includes("Don't use this bot in another program"), 'the existing-bot notes are not shown');
});

test('token input is type=password and never pre-filled', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': noToken() });
    await show(view);
    await click(view, 'connectExisting');
    const html = el('tgj-connect').innerHTML;
    assert.match(html, /<input[^>]*type="password"[^>]*data-tgj-input="token"/);
    assert.ok(!/data-tgj-input="token"[^>]*value="[^"]/.test(html) && !/value="[^"][^>]*data-tgj-input="token"/.test(html), 'value stays empty');
    assert.ok(html.includes("Restrict access in BotFather if you haven't, because anyone who can open the bot can manage notifications."));
    assert.ok(html.includes("Don't use this bot in another program at the same time."));
    assert.match(html, /data-tgj-input="setProfile"[^>]*checked/);
    assert.ok(html.includes("Let TgJelly set up this bot's profile: description, command menu and default admin rights (replaces the current ones)"));
});

test('Connected closes the connect card; the Bot section names the bot once', async () => {
    let saved = false;
    const { view, api, el } = mount({
        'GET TgJelly/Status': () => (saved ? status() : noToken()),
        'POST TgJelly/Token': () => { saved = true; return { Outcome: 'Connected', Message: '', BotUsername: 'jf_bot', WebhookDeleted: false }; },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '123:secret');
    await click(view, 'saveToken');

    assert.deepEqual(posted(api, 'Token'), [{ Token: '123:secret', SetProfile: true, ConfirmSwitch: false }]);
    assert.equal(el('tgj-connect').innerHTML, '');
    const html = el('tgj-bot').innerHTML;
    assert.equal(html.split('@jf_bot').length, 2);
    assert.ok(html.includes('href="https://t.me/jf_bot?start=setup"'));
    assert.ok(html.includes('Open in Telegram'));
    assert.ok(!html.includes('123:secret'));
});

test('Bot section access line does not repeat the username', () => {
    const html = m.renderBot(status(), { OpenBot: 'https://t.me/jf_bot?start=setup' }, EN, {});
    assert.ok(html.includes('Anyone who can open this bot'));
    assert.equal(html.split('@jf_bot').length, 2);
});

test('a removed webhook is mentioned after Connected', async () => {
    const { view, el } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'Connected', Message: '', BotUsername: 'jf_bot', WebhookDeleted: true },
    });
    await show(view);
    await click(view, 'connectNew');
    input(view, 'token', '1:a');
    await click(view, 'saveToken');
    assert.ok(el('tgj-connect').innerHTML.includes(m.t(EN, 'web.connect.webhookRemoved')));
});

test('the profile checkbox is sent as chosen', async () => {
    const { view, api } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'Connected', Message: '', BotUsername: 'jf_bot', WebhookDeleted: false },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '1:a');
    dispatchOn(view, 'change', { 'data-tgj-input': 'setProfile' }, { checked: false });
    await click(view, 'saveToken');
    assert.equal(posted(api, 'Token')[0].SetProfile, false);
});

test('buttons are disabled while a save is in flight', async () => {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const { view, el, api } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'Rejected', Message: 'no', BotUsername: null, WebhookDeleted: false },
    });
    const fetch = api.fetch;
    api.fetch = async request => {
        if (request.type === 'POST') {
            await gate;
        }

        return fetch(request);
    };
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '1:a');
    clickOn(view, { 'data-tgj-act': 'saveToken' });
    await flush();
    const html = el('tgj-connect').innerHTML;
    assert.ok((html.match(/<button/g) ?? []).length > 0);
    assert.equal((html.match(/<button(?![^>]*disabled)/g) ?? []).length, 0, 'every button is disabled');
    release();
    await flush();
    assert.ok(!/<button[^>]*disabled/.test(el('tgj-connect').innerHTML));
});

test('NeedsConfirm then Switch sends ConfirmSwitch', async () => {
    const answers = [
        { Outcome: 'NeedsConfirm', Message: '@new is a different bot. <b>x</b>', BotUsername: 'new', WebhookDeleted: false },
        { Outcome: 'Connected', Message: '', BotUsername: 'new', WebhookDeleted: false },
    ];
    const { view, api, el } = mount({ 'GET TgJelly/Status': noToken(), 'POST TgJelly/Token': () => answers.shift() });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '9:z');
    await click(view, 'saveToken');

    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes('@new is a different bot. &lt;b&gt;x&lt;/b&gt;'));
    assert.ok(html.includes('Switch bot') && html.includes('Cancel'));

    await click(view, 'switchBot');
    assert.deepEqual(posted(api, 'Token'), [
        { Token: '9:z', SetProfile: true, ConfirmSwitch: false },
        { Token: '9:z', SetProfile: true, ConfirmSwitch: true },
    ]);
    assert.equal(el('tgj-connect').innerHTML, '');
});

test('Cancel after NeedsConfirm forgets the token', async () => {
    const { view, api, el } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'NeedsConfirm', Message: 'different', BotUsername: 'new', WebhookDeleted: false },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '9:z');
    await click(view, 'saveToken');
    await click(view, 'cancelSwitch');
    assert.ok(!el('tgj-connect').innerHTML.includes('different'));
    await click(view, 'switchBot');
    assert.equal(posted(api, 'Token').length, 1, 'nothing to switch with');
});

test('other outcomes show the message, escaped', async () => {
    const { view, el } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'Rejected', Message: 'Telegram <rejected> it', BotUsername: null, WebhookDeleted: false },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '1:a');
    await click(view, 'saveToken');
    assert.ok(el('tgj-connect').innerHTML.includes('Telegram &lt;rejected&gt; it'));
});

test('Unreachable opens the proxy panel', async () => {
    const { view, el } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { Outcome: 'Unreachable', Message: 'Could not reach Telegram', BotUsername: null, WebhookDeleted: false },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '1:a');
    await click(view, 'saveToken');
    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes("Can't reach Telegram?"));
    assert.ok(html.includes('Could not reach Telegram'));
    assert.ok(html.includes('data-tgj-input="proxyMode"'));
    assert.ok(html.includes('data-tgj-act="saveProxy"'));
});

test('the Bot section shows the username, the access reminder and the buttons', async () => {
    const { view, el } = mount();
    await show(view);
    const html = el('tgj-bot').innerHTML;
    assert.ok(html.includes('@jf_bot'));
    assert.ok(html.includes('Anyone who can open this bot can manage notifications. Keep it restricted in BotFather.'));
    assert.ok(html.includes('Change token') && html.includes('Disconnect bot'));
});

test('Change token reuses the token form', async () => {
    const { view, el } = mount();
    await show(view);
    await click(view, 'changeToken');
    assert.ok(el('tgj-connect').innerHTML.includes('type="password"'));
    await click(view, 'cancelToken');
    assert.equal(el('tgj-connect').innerHTML, '');
});

test('disconnect needs two clicks', async () => {
    let connected = true;
    const { view, api, el } = mount({
        'GET TgJelly/Status': () => (connected ? status() : noToken()),
        'DELETE TgJelly/Token': () => {
            connected = false;
            return { status: 204 };
        },
    });
    await show(view);
    await click(view, 'disconnect');
    assert.equal(api.calls.filter(c => c.type === 'DELETE').length, 0);
    assert.ok(el('tgj-bot').innerHTML.includes('data-tgj-act="disconnectYes"'));

    await click(view, 'disconnectNo');
    assert.ok(!el('tgj-bot').innerHTML.includes('disconnectYes'));

    await click(view, 'disconnect');
    await click(view, 'disconnectYes');
    assert.equal(api.calls.filter(c => c.type === 'DELETE').length, 1);
    assert.equal(el('tgj-bot').innerHTML, '');
    assert.ok(el('tgj-connect').innerHTML.includes('Connect a Telegram bot'));
});

test('bot username is escaped', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': status({ BotUsername: '<img src=x>' }) });
    await show(view);
    const html = el('tgj-bot').innerHTML;
    assert.ok(html.includes('@&lt;img src=x&gt;'));
    assert.ok(!html.includes('<img'));
});

// --- proxy form ---

const proxyEl = el => el('tgj-proxy').innerHTML;

test('the proxy form shows the stored mode, and hides the address for System', async () => {
    const { view, el } = mount({ 'GET TgJelly/Settings': settings({ HasProxyPassword: true }) });
    await show(view);
    const html = proxyEl(el);
    assert.match(html, /<option value="System" selected>/);
    assert.ok(!html.includes('data-tgj-input="proxyUrl"'));
    assert.match(html, /data-tgj-input="proxyPassword"[^>]*placeholder="\(saved\)"/);
    assert.ok(!/data-tgj-input="proxyUsername"[^>]*placeholder=/.test(html));
});

test('choosing Socks5 shows the address field', async () => {
    const { view, el } = mount();
    await show(view);
    dispatchOn(view, 'change', { 'data-tgj-input': 'proxyMode' }, { value: 'Socks5' });
    await flush();
    assert.ok(proxyEl(el).includes('data-tgj-input="proxyUrl"'));
    assert.match(proxyEl(el), /<option value="Socks5" selected>/);
});

test('proxy untouched password is omitted, cleared sends empty', async () => {
    const { view, api, el } = mount({
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'p:8080', HasProxyUsername: true, HasProxyPassword: true }),
        'PUT TgJelly/Proxy': () => settings({ Revision: 6, ProxyMode: 'Http', ProxyUrl: 'p:8080' }),
    });
    await show(view);
    input(view, 'proxyUsername', 'bob');
    await click(view, 'saveProxy');
    const first = JSON.parse(api.calls.find(c => c.type === 'PUT').data);
    assert.deepEqual(first, { ExpectedRevision: 5, Mode: 'Http', Url: 'p:8080', Username: 'bob' });
    assert.ok(!('Password' in first));

    input(view, 'proxyPassword', 'x');
    input(view, 'proxyPassword', '');
    await click(view, 'saveProxy');
    const second = JSON.parse(api.calls.filter(c => c.type === 'PUT').at(-1).data);
    assert.deepEqual(second, { ExpectedRevision: 6, Mode: 'Http', Url: 'p:8080', Password: '' });
    assert.ok(!('Username' in second), 'the saved form starts untouched again');
    assert.ok(!proxyEl(el).includes('tgj-error'));
});

test('proxy 409 keeps typed url', async () => {
    const { view, hooks, el } = mount({
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'old:1' }),
        'PUT TgJelly/Proxy': { status: 409, body: { Current: settings({ Revision: 9, ProxyMode: 'Http', ProxyUrl: 'theirs:2' }) } },
    });
    await show(view);
    input(view, 'proxyUrl', 'mine:3');
    await click(view, 'saveProxy');

    const html = proxyEl(el);
    assert.ok(html.includes('value="mine:3"'));
    assert.ok(html.includes('tgj-changed'));
    assert.ok(html.includes(m.t(EN, 'web.proxy.conflict')));
    assert.equal(hooks.state.settings.Revision, 9);
    assert.equal(hooks.state.forms.proxy.dirty, true);
});

test('proxy 400 shows the field errors, escaped', async () => {
    const { view, el } = mount({
        'PUT TgJelly/Proxy': { status: 400, body: { Errors: [{ Field: 'Proxy.Url', Message: 'Must be <host:port>' }] } },
    });
    await show(view);
    dispatchOn(view, 'change', { 'data-tgj-input': 'proxyMode' }, { value: 'Http' });
    input(view, 'proxyUrl', 'nonsense');
    await click(view, 'saveProxy');
    assert.ok(proxyEl(el).includes('Must be &lt;host:port&gt;'));
    assert.ok(proxyEl(el).includes('value="nonsense"'));
});

// --- fix round 1: secrets stay out of the page and state ---

test('a typed proxy password is never rendered or kept in state, and survives a re-render in the input', async () => {
    const SECRET = 'hunter2-secret';
    const answers = [
        { status: 409, body: { Current: settings({ Revision: 9 }) } },
        { status: 400, body: { Errors: [{ Field: 'Proxy.Url', Message: 'bad' }] } },
    ];
    const { view, hooks, el } = mount({ 'PUT TgJelly/Proxy': () => answers.shift() });
    await show(view);
    const field = { name: 'proxyPassword', value: '' };
    view.fields.push(field);
    dispatchOn(view, 'change', { 'data-tgj-input': 'proxyMode' }, { value: 'Http' });
    input(view, 'proxyUrl', 'h:1');
    input(view, 'proxyPassword', SECRET);
    field.value = SECRET;

    for (let i = 0; i < 2; i++) {
        field.value = '';
        await click(view, 'saveProxy');
        assert.equal(field.value, SECRET, 'restored into the input after the re-render');
        assert.ok(!el('tgj-proxy').innerHTML.includes(SECRET));
        assert.ok(!JSON.stringify(hooks.state).includes(SECRET));
    }

    assert.ok(!m.renderProxyForm(settings(), EN, { form: { local: { Mode: 'Http', Url: 'x', Password: SECRET }, changed: [] } }).includes(SECRET));
});

test('the typed proxy password is dropped after a successful save', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Proxy': () => settings({ Revision: 6 }) });
    await show(view);
    const field = { name: 'proxyPassword', value: '' };
    view.fields.push(field);
    input(view, 'proxyPassword', 'pw');
    await click(view, 'saveProxy');
    assert.equal(field.value, '');
    await click(view, 'saveProxy');
    const puts = api.calls.filter(c => c.type === 'PUT').map(c => JSON.parse(c.data));
    assert.equal(puts[0].Password, 'pw');
    assert.ok(!('Password' in puts[1]));
});

test('a failed token request does not leave the token to be resent', async () => {
    const { view, api } = mount({
        'GET TgJelly/Status': noToken(),
        'POST TgJelly/Token': { status: 500, body: { Message: 'boom' } },
    });
    await show(view);
    await click(view, 'connectExisting');
    input(view, 'token', '1:old');
    await click(view, 'saveToken');
    await click(view, 'saveToken');
    assert.deepEqual(posted(api, 'Token').map(b => b.Token), ['1:old', '']);
});

test('a poll with a new message leaves the typed token and the open disconnect confirm alone', async () => {
    let message = 'first';
    const { view, timers, el } = mount({ 'GET TgJelly/Status': () => status({ ConnectionMessage: message }) });
    await show(view);
    await click(view, 'changeToken');
    input(view, 'token', '1:a');
    await click(view, 'disconnect');
    const connectWrites = el('tgj-connect').writes;
    const botWrites = el('tgj-bot').writes;

    message = 'second';
    timers.tick();
    await flush();
    assert.ok(el('tgj-status').innerHTML.includes('second'));
    assert.equal(el('tgj-connect').writes, connectWrites);
    assert.equal(el('tgj-bot').writes, botWrites);
    assert.ok(el('tgj-bot').innerHTML.includes('disconnectYes'));
});

test('another admin disconnecting closes the disconnect confirm', async () => {
    let connected = true;
    const { view, timers, el } = mount({ 'GET TgJelly/Status': () => (connected ? status() : noToken()) });
    await show(view);
    await click(view, 'disconnect');
    connected = false;
    timers.tick();
    await flush();
    assert.equal(el('tgj-bot').innerHTML, '');
    connected = true;
    timers.tick();
    await flush();
    assert.ok(!el('tgj-bot').innerHTML.includes('disconnectYes'));
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

// --- destinations (spec §6.6.3, §7.2, §7.3, §10.4) ---

const destEl = el => el('tgj-destinations').innerHTML;
const destAct = (view, act, attributes = {}) => {
    clickOn(view, { 'data-tgj-act': act, ...attributes });
    return flush();
};
const destInput = (view, name, attributes, extra, type = 'change') => {
    dispatchOn(view, type, { 'data-tgj-input': name, ...attributes }, extra);
    return flush();
};
const putBodies = api => api.calls.filter(c => c.type === 'PUT' && c.url.includes('Destinations/')).map(c => JSON.parse(c.data));

test('destination titles are escaped', async () => {
    const { view, el } = mount({ 'GET TgJelly/Destinations': destinations({ Items: [dest({ Title: '<script>alert(1)</script>' })] }) });
    await show(view);
    assert.ok(!destEl(el).includes('<script>'));
    assert.ok(destEl(el).includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
});

test('health badge text comes from the strings', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({
            Items: [dest({ Health: { Status: 'NeedsPermissions', PhotosBlocked: true } })],
        }),
    });
    await show(view);
    assert.ok(destEl(el).includes(m.t(EN, 'dest.health.NeedsPermissions')));
    assert.ok(destEl(el).includes(m.t(EN, 'dest.health.PhotosBlocked')));
    assert.ok(destEl(el).includes(m.t(EN, 'dest.kind.Supergroup')));
});

test('pause toggle sends only Enabled with the revision', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Destinations/d1': () => dest({ Enabled: false }) });
    await show(view);
    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 7, Enabled: false }]);
});

test('preset change sends Preset', async () => {
    const { view, api, el } = mount({ 'PUT TgJelly/Destinations/d1': () => dest({ Preset: 'NewMedia' }) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes('data-tgj-input="destPreset"'));
    await destInput(view, 'destPreset', { 'data-tgj-id': 'd1' }, { value: 'NewMedia' });
    await destAct(view, 'saveDest', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 7, Preset: 'NewMedia' }]);
});

test('event toggle sends Events', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Destinations/d1': () => dest({ Preset: 'Custom' }) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destEvent', { 'data-tgj-id': 'd1', 'data-tgj-key': 'server.started' }, { checked: false });
    await destAct(view, 'saveDest', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 7, Events: ['media.movie', 'media.episodes', 'security.login_failed'] }]);
});

test('the editor lists events by group, libraries and silent groups', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A], SilentGroups: ['media'] })] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    const html = destEl(el);
    assert.ok(html.includes(m.t(EN, 'dest.event.security.login_failed')));
    assert.ok(html.includes(m.t(EN, 'dest.preset.AdminAlerts')));
    assert.ok(html.includes('Shows &lt;i&gt;'));
    assert.match(html, new RegExp(`data-tgj-lib="${LIB_A}"[^>]*checked`));
    assert.match(html, /data-tgj-group="media"[^>]*checked/);
    assert.ok(html.includes(m.t(EN, 'dest.libraries.hint')));
    assert.ok(!html.includes(m.t(EN, 'dest.libraries.all')));
});

test('the all-libraries line follows the library boxes', async () => {
    const { view, el } = mount({ 'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A] })] }) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destLibrary', { 'data-tgj-id': 'd1', 'data-tgj-lib': LIB_A }, { checked: false });
    assert.ok(destEl(el).includes(m.t(EN, 'dest.libraries.all')));
    assert.ok(!destEl(el).includes(m.t(EN, 'dest.libraries.hint')));
});

test('the sending switch is checked exactly when the destination is enabled', async () => {
    for (const enabled of [true, false]) {
        const { view, el } = mount({ 'GET TgJelly/Destinations': destinations({ Items: [dest({ Enabled: enabled })] }) });
        await show(view);
        const html = destEl(el);
        assert.ok(html.includes(m.t(EN, 'web.dest.enabled')));
        assert.equal(/data-tgj-act="enabled"[^>]*checked/.test(html), enabled);
    }
});

test('picking Custom sends Preset Custom and keeps the events', async () => {
    const { view, api, hooks } = mount({ 'PUT TgJelly/Destinations/d1': () => dest({ Preset: 'Custom' }) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destPreset', { 'data-tgj-id': 'd1' }, { value: 'Custom' });
    assert.deepEqual(hooks.state.forms['dest:d1'].local.Events, KEYS);
    await destAct(view, 'saveDest', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 7, Preset: 'Custom' }]);
});

test('a 409 on a pause with nothing pending uses the neutral line', async () => {
    const { view, el } = mount({
        'PUT TgJelly/Destinations/d1': { status: 409, body: { Current: destinations({ Revision: 9 }) } },
    });
    await show(view);
    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes(m.t(EN, 'web.dest.conflictPlain')));
    assert.ok(!destEl(el).includes(m.t(EN, 'web.dest.conflict')));
});

test('a removed library is listed and removable', async () => {
    const ghost = 'cccccccccccccccccccccccccccccccc';
    const { view, api, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A, ghost], RemovedLibraryIds: [ghost], LibraryFilterBroken: false })] }),
        'PUT TgJelly/Destinations/d1': () => dest({ LibraryIds: [LIB_A] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes(m.t(EN, 'dest.libraries.removed', { id: ghost })));
    await destAct(view, 'removeLibrary', { 'data-tgj-id': 'd1', 'data-tgj-lib': ghost });
    assert.ok(!destEl(el).includes(ghost));
    await destAct(view, 'saveDest', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 7, LibraryIds: [LIB_A] }]);
});

test('a broken library filter says so', async () => {
    const ghost = 'cccccccccccccccccccccccccccccccc';
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [ghost], RemovedLibraryIds: [ghost], LibraryFilterBroken: true })] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes(m.t(EN, 'web.dest.librariesRemoved')));
});

test('remove needs a confirm and sends the leave flag', async () => {
    const { view, api, el } = mount({ 'DELETE TgJelly/Destinations/d1': { status: 204 } });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(view, 'remove', { 'data-tgj-id': 'd1' });
    assert.equal(api.calls.filter(c => c.type === 'DELETE').length, 0, 'the first click only asks');
    assert.ok(destEl(el).includes('data-tgj-input="destLeave"'));

    await destAct(view, 'removeNo', { 'data-tgj-id': 'd1' });
    assert.ok(!destEl(el).includes('data-tgj-input="destLeave"'));

    await destAct(view, 'remove', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destLeave', { 'data-tgj-id': 'd1' }, { checked: true });
    await destAct(view, 'removeYes', { 'data-tgj-id': 'd1' });
    const deletes = api.calls.filter(c => c.type === 'DELETE');
    assert.equal(deletes.length, 1);
    assert.equal(deletes[0].url, '/jf/TgJelly/Destinations/d1?leave=true&expectedRevision=7');
});

test('send test and check again show their outcome', async () => {
    const { view, api, el } = mount({
        'POST TgJelly/Destinations/d1/Test': { Sent: false, Error: 'bot <was> kicked', Health: { Status: 'Lost' } },
        'POST TgJelly/Destinations/d1/Check': { Health: { Status: 'Ok' }, Error: null },
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(view, 'testDest', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes('bot &lt;was&gt; kicked'));
    await destAct(view, 'checkDest', { 'data-tgj-id': 'd1' });
    assert.equal(api.calls.filter(c => c.type === 'POST' && c.url.endsWith('/Check')).length, 1);
});

test('editor 409 keeps local event choices and highlights both-changed', async () => {
    const { view, hooks, el } = mount({
        'PUT TgJelly/Destinations/d1': {
            status: 409,
            body: { Current: destinations({ Revision: 9, Items: [dest({ Preset: 'Custom', Events: ['media.movie', 'server.started'] })] }) },
        },
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destEvent', { 'data-tgj-id': 'd1', 'data-tgj-key': 'server.started' }, { checked: false });
    await destAct(view, 'saveDest', { 'data-tgj-id': 'd1' });

    const form = hooks.state.forms['dest:d1'];
    assert.deepEqual(form.local.Events, ['media.movie', 'media.episodes', 'security.login_failed']);
    assert.equal(form.dirty, true);
    assert.ok(form.changed.includes('Events'));
    assert.equal(hooks.state.destinations.Revision, 9);
    assert.ok(destEl(el).includes('tgj-changed'));
    assert.ok(destEl(el).includes(m.t(EN, 'web.dest.conflict')));
});

test('the set up manually link only shows when ManualUnlocked', async () => {
    const closed = mount();
    await show(closed.view);
    assert.ok(!destEl(closed.el).includes('open-manual'));
    assert.ok(destEl(closed.el).includes(LINKS.StartAdd));
    assert.ok(destEl(closed.el).includes('target="_blank"'));

    const open = mount({ 'GET TgJelly/Destinations': destinations({ ManualUnlocked: true }) });
    await show(open.view);
    assert.ok(destEl(open.el).includes('data-tgj-act="open-manual"'));
    // Jellyfin's dashboard routes on the hash, so the control must not carry an href="#…" (final review M1).
    assert.match(destEl(open.el), /<button is="emby-button" type="button" class="button-link"[^>]*data-tgj-act="open-manual"/);
    assert.ok(!destEl(open.el).includes('href="#'));
    open.el('tgj-manual').scrolled = 0;
    open.el('tgj-manual').scrollIntoView = () => { open.el('tgj-manual').scrolled++; };
    await destAct(open.view, 'open-manual');
    assert.equal(open.el('tgj-sec-advanced').hidden, false, 'the Advanced section is shown');
    assert.equal(open.el('tgj-sec-destinations').hidden, true);
    assert.equal(open.win.location.hash, '#/configurationpage?name=TgJelly&tgj=advanced');
    assert.equal(open.el('tgj-manual').scrolled, 1);
});

// --- manual block and detected chats (spec §6.5) ---

const manualEl = el => el('tgj-manual').innerHTML;
const DETECTED = [{ ChatId: -100777, Title: 'Movie <b>club</b>', Kind: 'Supergroup', DetectedAt: '2026-10-20T10:00:00Z' }];
const unlocked = overrides => ({ 'GET TgJelly/Destinations': destinations({ ManualUnlocked: true, DetectedChats: DETECTED, ...overrides }) });
const added = (outcome = 'Added') => ({ Outcome: outcome, Destination: dest({ Id: 'd2' }), TestSent: true, TestError: null });
const posts = (api, prefix) => api.calls.filter(c => c.type === 'POST' && c.url.startsWith(`/jf/TgJelly/${prefix}`));
const typeManual = (view, value) => destInput(view, 'manualInput', {}, { value }, 'input');

test('manual block hidden when locked', async () => {
    const { view, el } = mount({ 'GET TgJelly/Destinations': destinations({ DetectedChats: DETECTED }) });
    await show(view);
    assert.equal(manualEl(el), '');
    assert.equal(m.renderManual(destinations(), EN, {}), '');
    const open = mount(unlocked());
    await show(open.view);
    assert.ok(manualEl(open.el).includes(m.t(EN, 'web.manual.title')));
    assert.ok(manualEl(open.el).includes('data-tgj-act="manualAdd"'));
});

test('integer input sends ChatId, link sends TopicLink', async () => {
    const { view, api } = mount({ ...unlocked(), 'POST TgJelly/Destinations': () => added() });
    await show(view);
    await typeManual(view, ' -100123456 ');
    await destInput(view, 'manualPreset', {}, { value: 'NewMedia' });
    await destAct(view, 'manualAdd');
    await typeManual(view, 'https://t.me/c/123456/7');
    await destAct(view, 'manualAdd');
    await typeManual(view, 'https://t.me/club/9');
    await destAct(view, 'manualAdd');
    const bodies = posts(api, 'Destinations').map(c => JSON.parse(c.data));
    assert.deepEqual(bodies, [
        { ExpectedRevision: 7, ChatId: -100123456, Preset: 'NewMedia' },
        { ExpectedRevision: 7, TopicLink: 'https://t.me/c/123456/7', Preset: 'NewMedia' },
        { ExpectedRevision: 7, TopicLink: 'https://t.me/club/9', Preset: 'NewMedia' },
    ]);
});

test('a successful add shows the outcome and reloads destinations', async () => {
    const { view, api, el } = mount({ ...unlocked(), 'POST TgJelly/Destinations': () => ({ ...added(), TestSent: false, TestError: 'no <rights>' }) });
    await show(view);
    const before = api.calls.filter(c => c.type === 'GET' && c.url.endsWith('TgJelly/Destinations')).length;
    await typeManual(view, '-100123');
    await destAct(view, 'manualAdd');
    assert.ok(manualEl(el).includes(m.t(EN, 'web.manual.Added')));
    assert.ok(manualEl(el).includes('no &lt;rights&gt;'));
    assert.equal(api.calls.filter(c => c.type === 'GET' && c.url.endsWith('TgJelly/Destinations')).length, before + 1);
});

test('detected title escaped', async () => {
    const { view, el } = mount(unlocked());
    await show(view);
    assert.ok(manualEl(el).includes(m.t(EN, 'detected.line', { title: 'Movie <b>club</b>' })));
    assert.ok(!manualEl(el).includes('<b>club'));
    assert.ok(manualEl(el).includes('Movie &lt;b&gt;club&lt;/b&gt;'));
});

test('set up sends preset', async () => {
    const { view, api } = mount({ ...unlocked(), 'POST TgJelly/DetectedChats/-100777': () => added() });
    await show(view);
    await destInput(view, 'detectedPreset', { 'data-tgj-id': '-100777' }, { value: 'AdminAlerts' });
    await destAct(view, 'detectedSetUp', { 'data-tgj-id': '-100777' });
    const calls = posts(api, 'DetectedChats/-100777');
    assert.equal(calls.length, 1);
    assert.deepEqual(JSON.parse(calls[0].data), { ExpectedRevision: 7, Preset: 'AdminAlerts' });
});

test('set up failure shows the error', async () => {
    const { view, el } = mount({ ...unlocked(), 'POST TgJelly/DetectedChats/-100777': { status: 404, body: { Message: 'gone <x>' } } });
    await show(view);
    await destAct(view, 'detectedSetUp', { 'data-tgj-id': '-100777' });
    assert.ok(manualEl(el).includes('gone &lt;x&gt;'));
});

test('dismiss sends DELETE', async () => {
    const { view, api } = mount({ ...unlocked(), 'DELETE TgJelly/DetectedChats/-100777': { status: 204 } });
    await show(view);
    await destAct(view, 'detectedDismiss', { 'data-tgj-id': '-100777' });
    const deletes = api.calls.filter(c => c.type === 'DELETE');
    assert.equal(deletes.length, 1);
    assert.equal(deletes[0].url, '/jf/TgJelly/DetectedChats/-100777');
});

test('add errors are shown escaped', async () => {
    const { view, el } = mount({
        ...unlocked(),
        'POST TgJelly/Destinations': { status: 400, body: { Errors: [{ Field: 'ChatId', Message: 'bad <img src=x>' }] } },
    });
    await show(view);
    await typeManual(view, '-100123');
    await destAct(view, 'manualAdd');
    assert.ok(manualEl(el).includes('bad &lt;img src=x&gt;'));
    assert.ok(!manualEl(el).includes('<img'));
});

// --- messages, batching, updates, diagnostics ---

const putSettings = api => api.calls.filter(c => c.type === 'PUT' && c.url === '/jf/TgJelly/Settings').map(c => JSON.parse(c.data));
const msgChange = (view, key, checked) => dispatchOn(view, 'change', { 'data-tgj-input': 'msgToggle', 'data-tgj-key': key }, { checked });
const batchInput = (view, key, value) => dispatchOn(view, 'input', { 'data-tgj-input': 'batching', 'data-tgj-key': key }, { value });

test('use suggested fills the field', async () => {
    const { view, el } = mount({ 'GET TgJelly/Settings': settings({ PublicUrl: 'http://10.0.0.5:8096', PublicUrlValid: false, PublicUrlSuggestion: 'https://jf.example.com' }) });
    await show(view);
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes('https://jf.example.com'));
    assert.ok(html.includes(m.t(EN, 'web.messages.verify')));
    await click(view, 'useSuggested');
    assert.match(el('tgj-messages').innerHTML, /data-tgj-field="PublicUrl"[^>]*value="https:\/\/jf\.example\.com"|value="https:\/\/jf\.example\.com"[^>]*data-tgj-field="PublicUrl"/);
});

test('invalid url explains', async () => {
    const { view, el } = mount({ 'GET TgJelly/Settings': settings({ PublicUrl: 'http://localhost', PublicUrlValid: false }) });
    await show(view);
    assert.ok(el('tgj-messages').innerHTML.includes(m.t(EN, 'web.messages.urlInvalid').replaceAll('&#39;', "'")));
    const ok = mount({ 'GET TgJelly/Settings': settings({ PublicUrl: 'https://jf.example.com', PublicUrlValid: true }) });
    await show(ok.view);
    assert.ok(!ok.el('tgj-messages').innerHTML.includes(m.t(EN, 'web.messages.urlInvalid').replaceAll('&#39;', "'")));
});

test('toggles saved in Message', async () => {
    const saved = settings({ Revision: 6 });
    const { view, api, el } = mount({ 'PUT TgJelly/Settings': saved });
    await show(view);
    const html = el('tgj-messages').innerHTML;
    for (const key of ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks', 'AnnounceUpgrades']) {
        assert.ok(html.includes(`data-tgj-key="${key}"`), key);
    }
    msgChange(view, 'ShowProviderLinks', true);
    msgChange(view, 'ShowPoster', false);
    await click(view, 'saveSettings');
    const [body] = putSettings(api);
    assert.equal(body.ExpectedRevision, 5);
    assert.equal(body.Message.ShowProviderLinks, true);
    assert.equal(body.Message.ShowPoster, false);
    assert.equal(body.PublicUrl, '');
    assert.deepEqual(body.UpdateCheck, { Enabled: true, Mode: 'SameMajor' });
    assert.equal(body.Batching.QuietWindowSeconds, 120);
});

test('range error shown next to its field', async () => {
    const { view, el } = mount({
        'PUT TgJelly/Settings': { status: 400, body: { Errors: [{ Field: 'Batching.QuietWindowSeconds', Message: 'Must be 5 to <3600>.' }] } },
    });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '2');
    clickOn(view, { 'data-tgj-act': 'saveSettings', 'data-tgj-id': 'batching' });
    await flush();
    const html = el('tgj-batching').innerHTML;
    assert.match(html, /data-tgj-field="Batching\.QuietWindowSeconds"[\s\S]*?Must be 5 to &lt;3600&gt;\./);
    assert.ok(html.includes('min="5"') && html.includes('max="3600"'));
    assert.ok(html.includes('value="2"'));
    assert.ok(!el('tgj-messages').innerHTML.includes('Must be 5'));
});

test('settings 409 keeps typed batching value', async () => {
    const fresh = settings({ Revision: 9, Batching: { QuietWindowSeconds: 200, MaxHoldMinutes: 45, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 } });
    const { view, el, hooks } = mount({ 'PUT TgJelly/Settings': { status: 409, body: { Current: fresh } } });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '300');
    batchInput(view, 'DigestThreshold', '20');
    clickOn(view, { 'data-tgj-act': 'saveSettings', 'data-tgj-id': 'batching' });
    await flush();
    const html = el('tgj-batching').innerHTML;
    assert.ok(html.includes('value="300"'));
    assert.ok(html.includes('value="45"'), 'the server change in an untouched field shows');
    assert.ok(html.includes('tgj-changed'), 'the field both sides changed is highlighted');
    assert.ok(html.includes(m.t(EN, 'web.settings.conflict')));
    assert.equal(hooks.state.settings.Revision, 9);
    assert.equal(hooks.state.forms.settings.dirty, true);
});

test('Save messages sends the saved batching values, and the batching edit stays dirty', async () => {
    const saved = settings({ Revision: 6, Message: { ...settings().Message, ShowProviderLinks: true } });
    const { view, api, hooks } = mount({ 'PUT TgJelly/Settings': saved });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '9999');
    msgChange(view, 'ShowProviderLinks', true);
    await click(view, 'saveSettings');
    const [body] = putSettings(api);
    assert.equal(body.Batching.QuietWindowSeconds, 120);
    assert.equal(body.Message.ShowProviderLinks, true);
    assert.equal(hooks.state.forms.settings.local.Batching.QuietWindowSeconds, 9999);
    assert.equal(hooks.state.forms.settings.dirty, true);
    assert.equal(hooks.state.forms.settings.base.Message.ShowProviderLinks, true);
});

test('an error for a field in another section shows under the pressed Save', async () => {
    const { view, el } = mount({
        'PUT TgJelly/Settings': { status: 400, body: { Errors: [{ Field: 'Batching.MaxHoldMinutes', Message: 'Too <long>.' }] } },
    });
    await show(view);
    await click(view, 'saveSettings');
    assert.ok(el('tgj-messages').innerHTML.includes('Too &lt;long&gt;.'));
});

test('a field typed into while its section saves stays an edit', async () => {
    let view;
    const route = () => {
        batchInput(view, 'DigestThreshold', '33');
        return settings({ Revision: 6, Batching: { ...settings().Batching, QuietWindowSeconds: 300 } });
    };
    const mounted = mount({ 'PUT TgJelly/Settings': route });
    view = mounted.view;
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '300');
    clickOn(view, { 'data-tgj-act': 'saveSettings', 'data-tgj-id': 'batching' });
    await flush();
    const form = mounted.hooks.state.forms.settings;
    assert.equal(form.local.Batching.DigestThreshold, 33);
    assert.equal(form.dirty, true);
    assert.equal(form.local.Batching.QuietWindowSeconds, 300);
    assert.ok(mounted.el('tgj-batching').innerHTML.includes('value="33"'));
});

test('a proxy save keeps unsaved settings edits, and a settings save keeps the proxy edit', async () => {
    const { view, el, hooks } = mount({ 'PUT TgJelly/Proxy': settings({ Revision: 6, ProxyMode: 'Http', ProxyUrl: 'h:1' }) });
    await show(view);
    batchInput(view, 'DigestThreshold', '20');
    dispatchOn(view, 'change', { 'data-tgj-input': 'proxyMode' }, { value: 'Http' });
    input(view, 'proxyUrl', 'h:1');
    await click(view, 'saveProxy');
    assert.equal(hooks.state.settings.Revision, 6);
    assert.equal(hooks.state.forms.settings.local.Batching.DigestThreshold, 20);
    assert.ok(el('tgj-batching').innerHTML.includes('value="20"'));
});

test('updates form sends enabled and mode', async () => {
    const { view, api, el } = mount({ 'PUT TgJelly/Settings': settings({ Revision: 6 }) });
    await show(view);
    assert.match(el('tgj-updates').innerHTML, /<option value="SameMajor" selected>/);
    dispatchOn(view, 'change', { 'data-tgj-input': 'updEnabled' }, { checked: false });
    dispatchOn(view, 'change', { 'data-tgj-input': 'updMode' }, { value: 'AnyNewer' });
    clickOn(view, { 'data-tgj-act': 'saveSettings', 'data-tgj-id': 'updates' });
    await flush();
    assert.deepEqual(putSettings(api)[0].UpdateCheck, { Enabled: false, Mode: 'AnyNewer' });
});

test('a non-number batching value is not sent', async () => {
    const { view, api, el } = mount();
    await show(view);
    batchInput(view, 'MaxHoldMinutes', 'abc');
    clickOn(view, { 'data-tgj-act': 'saveSettings', 'data-tgj-id': 'batching' });
    await flush();
    assert.equal(putSettings(api).length, 0);
    assert.match(el('tgj-batching').innerHTML, /data-tgj-field="Batching\.MaxHoldMinutes"[\s\S]*?tgj-error/);
});

test('copy diagnostics writes to the clipboard', async () => {
    const written = [];
    Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: async text => { written.push(text); } } }, configurable: true });
    const { view, el } = mount({ 'GET TgJelly/Diagnostics': { Text: 'TgJelly 1.0 <diag>' } });
    await show(view);
    await click(view, 'copyDiagnostics');
    assert.deepEqual(written, ['TgJelly 1.0 <diag>']);
    assert.ok(el('tgj-diagnostics').innerHTML.includes(m.t(EN, 'web.diag.copied')));
    assert.ok(!el('tgj-diagnostics').innerHTML.includes('<textarea'));
});

test('copy diagnostics falls back to a textarea when clipboard rejects', async () => {
    Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: async () => { throw new Error('denied'); } } }, configurable: true });
    const { view, el } = mount({ 'GET TgJelly/Diagnostics': { Text: 'TgJelly 1.0 <diag>' } });
    let selected = 0;
    view.elements.set('tgj-diagnostics-text', { select: () => { selected++; } });
    await show(view);
    await click(view, 'copyDiagnostics');
    const html = el('tgj-diagnostics').innerHTML;
    assert.match(html, /<textarea[^>]*readonly[^>]*>TgJelly 1\.0 &lt;diag&gt;<\/textarea>/);
    assert.equal(selected, 1);
});

// --- message templates (Plan 7 Task 8) ---

function templates(overrides = {}) {
    return {
        Revision: 4,
        Items: [
            {
                EventKey: 'movie.added', Label: 'Movie added', Media: true, Text: '<b>{{ title }}</b>', BuiltIn: '<b>{{ title }}</b>', Edited: false,
                SampleFull: {}, SampleSparse: {}, Flag: null,
                Fields: [{ Name: 'title', Type: 'string', Description: 'The <title>.', Optional: false }, { Name: 'year', Type: 'number', Description: 'The year.', Optional: true }],
            },
            {
                EventKey: 'media.digest', Label: 'Digest', Media: false, Text: 'Hi {{ title }}', BuiltIn: 'Digest {{ count }}', Edited: true,
                SampleFull: {}, SampleSparse: {},
                Flag: { Kind: 'error', Error: 'bad <tag>', At: '2026-10-20T10:00:00Z' },
                Fields: [{ Name: 'title', Type: 'string', Description: 'T', Optional: false }],
            },
        ],
        ...overrides,
    };
}

/** A Validate reply: no errors unless given, Telegram HTML in Html. */
function validation(overrides = {}) {
    return { Errors: [], Warnings: [], Preview: '', Length: 10, Limit: 1024, Html: 'ok', Caption: true, Poster: true, Buttons: [], ...overrides };
}

const tplInput = (view, name, extra, type = 'input') => dispatchOn(view, type, { 'data-tgj-input': name }, extra);
const tplUi = (overrides = {}) => ({ key: 'movie.added', forms: {}, result: null, good: null, destinations: [{ Id: 'd1', Title: 'Family' }], ...overrides });

test('previewHtml_keepsWhitelist', () => {
    assert.equal(m.previewHtml('<b>a</b><i>b</i>'), '<b>a</b><i>b</i>');
    assert.equal(m.previewHtml('<strong>a</strong><em>b</em><u>c</u><ins>d</ins><s>e</s><strike>f</strike><del>g</del><code>h</code><pre>i</pre>'),
        '<strong>a</strong><em>b</em><u>c</u><ins>d</ins><s>e</s><strike>f</strike><del>g</del><code>h</code><pre>i</pre>');
    assert.equal(m.previewHtml('<pre><code class="language-js">x</code></pre>'), '<pre><code>x</code></pre>');
    assert.equal(m.previewHtml('<tg-spoiler>a</tg-spoiler><span class="tg-spoiler">b</span>'),
        '<span class="tgj-tg-spoiler">a</span><span class="tgj-tg-spoiler">b</span>');
    assert.equal(m.previewHtml('<blockquote>q</blockquote>'), '<blockquote>q</blockquote>');
    assert.equal(m.previewHtml('<a href="https://example.com/?a=1&amp;b=2">x</a>'),
        '<a target="_blank" rel="noopener noreferrer" href="https://example.com/?a=1&amp;b=2">x</a>');
    assert.equal(m.previewHtml('<a href=" TG://user?id=1 ">x</a>'), '<a target="_blank" rel="noopener noreferrer" href="TG://user?id=1">x</a>');
    assert.equal(m.previewHtml('<tg-emoji emoji-id="5368324170671202286">👍</tg-emoji>'), '👍');
    assert.equal(m.previewHtml('a &lt;b&gt; &amp;amp; &#65;&#x42; &quot;'), 'a &lt;b&gt; &amp;amp; AB &quot;');
});

test('previewHtml_neutralizesUnsafeMarkup', () => {
    const link = m.previewHtml('<a href="javascript:alert(1)">x</a>');
    assert.ok(!link.includes('href'), link);
    assert.ok(link.includes('x'));
    assert.equal(link, 'x');
    for (const href of ['JavaScript:alert(1)', 'javascript&#58;alert(1)', ' data:text/html,x', 'vbscript:x', '//evil.example', '"><script>']) {
        assert.ok(!m.previewHtml(`<a href="${href}">x</a>`).includes('href'), href);
    }

    const img = m.previewHtml('<img src=x onerror=alert(1)>');
    assert.ok(img.startsWith('&lt;img'), img);
    assert.ok(!img.includes('<img'));
    assert.equal(m.previewHtml('<b onclick="x">y</b>'), '<b>y</b>');
    assert.equal(m.previewHtml('<b>unclosed'), '&lt;b&gt;unclosed');
    assert.equal(m.previewHtml('stray</b>'), 'stray&lt;/b&gt;');
    assert.equal(m.previewHtml('<script>alert(1)</script>'), '&lt;script&gt;alert(1)&lt;/script&gt;');
    assert.equal(m.previewHtml('<span style="color:red">r</span>'), 'r');
    assert.equal(m.previewHtml('<a href="https://x.example" onclick="y" style="z">l</a>'),
        '<a target="_blank" rel="noopener noreferrer" href="https://x.example">l</a>');
    // Mis-nested: the inner tag never closes inside the outer one, so it and the stray close are text.
    assert.equal(m.previewHtml('<b><i>x</b></i>'), '<b>&lt;i&gt;x</b>&lt;/i&gt;');
    assert.equal(m.previewHtml('a < b > c'), 'a &lt; b &gt; c');
    assert.equal(m.previewHtml('<a href="https://x.example/&quot;onmouseover=&quot;y">l</a>'),
        '<a target="_blank" rel="noopener noreferrer" href="https://x.example/&quot;onmouseover=&quot;y">l</a>');
    assert.equal(m.previewHtml(null), '');
});

test('previewHtml_expandableQuote_isDetails', () => {
    assert.equal(m.previewHtml('<blockquote expandable>First line\nsecond <b>bold</b>\nthird</blockquote>'),
        '<details class="tgj-tg-quote"><summary>First line</summary>second <b>bold</b>\nthird</details>');
    assert.equal(m.previewHtml('<blockquote expandable>one line</blockquote>'),
        '<details class="tgj-tg-quote"><summary>one line</summary></details>');
});

test('previewHtml_tgTime_showsFallbackText', () => {
    assert.equal(m.previewHtml('At <tg-time unix="1700000000" format="t">20:00</tg-time>.'), 'At 20:00.');
});

test('renderTemplates_builtInBadge_noResetButton', () => {
    const html = m.renderTemplates(templates(), EN, tplUi());
    assert.ok(html.includes(`<span class="tgj-badge">${m.t(EN, 'web.templates.builtIn')}</span>`), html);
    assert.ok(!html.includes(m.t(EN, 'web.templates.edited')));
    assert.ok(!html.includes('data-tgj-act="tplReset"'));
    assert.ok(html.includes('data-tgj-act="tplSave"'));
    assert.ok(html.includes(`<h2 class="sectionTitle">${m.t(EN, 'web.templates.title')}</h2>`));
    // The select lists every event, the edited one marked.
    assert.ok(html.includes('<option value="movie.added" selected>Movie added</option>'), html);
    assert.ok(html.includes('<option value="media.digest">● Digest</option>'), html);
});

test('renderTemplates_editedBadge_resetAsksInline', () => {
    const html = m.renderTemplates(templates(), EN, tplUi({ key: 'media.digest' }));
    assert.ok(html.includes(`<span class="tgj-badge">${m.t(EN, 'web.templates.edited')}</span>`), html);
    assert.ok(html.includes('data-tgj-act="tplReset"'));
    assert.ok(!html.includes('data-tgj-act="tplResetConfirm"'));

    const asking = m.renderTemplates(templates(), EN, tplUi({ key: 'media.digest', resetAsk: true }));
    assert.ok(asking.includes(m.t(EN, 'web.templates.resetAsk')));
    assert.ok(asking.includes('data-tgj-act="tplResetConfirm"') && asking.includes('data-tgj-act="tplResetCancel"'));
    assert.ok(!asking.includes('data-tgj-act="tplReset"'));
});

test('renderTemplates_noEnabledCheckbox', () => {
    const html = m.renderTemplates(templates(), EN, tplUi());
    assert.ok(!html.includes('type="checkbox"'));
    assert.ok(!html.includes('tplEnabled') && !html.includes('tplSample') && !html.includes('tplValidate'));
    assert.ok(!('web.templates.enabled' in EN) && !('web.templates.digestNote' in EN));
});

test('renderTemplates_gridHasPreviewPane', () => {
    const good = validation({ Html: '<b>Dune</b>' });
    const html = m.renderTemplates(templates(), EN, tplUi({ result: good, good }));
    const grid = html.indexOf('<div class="tgj-tpl-grid">');
    const textarea = html.indexOf('<textarea');
    const fields = html.indexOf(`<summary>${m.t(EN, 'web.templates.fields')}</summary>`);
    const pane = html.indexOf('<div id="tgj-tpl-preview"');
    assert.ok(grid >= 0 && grid < textarea && textarea < fields && fields < pane, html);
    assert.ok(html.includes('>&lt;b&gt;{{ title }}&lt;/b&gt;</textarea>'));
    assert.ok(html.slice(pane).includes('<b>Dune</b>'));
    assert.ok(html.includes(m.t(EN, 'web.templates.testNote')));
    assert.ok(html.includes('<option value="d1" selected>Family</option>'), html);
    assert.equal(m.t(EN, 'web.templates.testNote'), 'The test message goes to that chat for real, and its members see it.');
});

test('renderTemplatePreview_posterPlaceholderAndButtons', () => {
    const html = m.renderTemplatePreview(validation({
        Html: 'Dune <i>2021</i>', Length: 9, Limit: 1024,
        Buttons: [[{ Text: 'Open <x>', Url: 'https://jf.example/web/' }], [{ Text: 'IMDb', Url: 'javascript:alert(1)' }, { Text: 'TMDB', Url: 'https://tmdb.example' }]],
    }), EN, 'en');
    assert.ok(html.includes('class="tgj-tg-poster"'), html);
    assert.ok(html.includes('Dune <i>2021</i>'));
    assert.equal((html.match(/class="tgj-tg-row"/g) ?? []).length, 2);
    assert.ok(html.includes('href="https://jf.example/web/"') && html.includes('Open &lt;x&gt;'));
    assert.ok(!html.includes('javascript:'));
    assert.ok(html.includes('IMDb'));
    assert.ok(html.includes('9 / 1,024'));

    const plain = m.renderTemplatePreview(validation({ Poster: false, Html: 'x' }), EN, 'en');
    assert.ok(!plain.includes('tgj-tg-poster'));
    assert.equal(m.renderTemplatePreview(null, EN, 'en'), '');
});

test('renderTemplatePreview_errorsDimLastGood', () => {
    const good = validation({ Html: '<b>last good</b>' });
    const bad = validation({ Errors: [{ Message: 'Unexpected <end>', Line: 2, Column: 7 }, { Message: 'No position' }], Warnings: ['careful & slow'], Html: '', Length: 0 });
    const html = m.renderTemplatePreview(bad, EN, 'en', good);
    assert.ok(html.includes('line 2, column 7: Unexpected &lt;end&gt;'), html);
    assert.ok(html.includes('No position') && html.includes('careful &amp; slow'));
    assert.ok(/class="tgj-tg-bubble[^"]*tgj-tg-stale/.test(html), html);
    assert.ok(html.includes('<b>last good</b>'));

    const fresh = m.renderTemplatePreview(good, EN, 'en', good);
    assert.ok(!fresh.includes('tgj-tg-stale'));
});

test('sendTest_disabledWithErrors', () => {
    const testButton = html => /<button[^>]*data-tgj-act="tplTest"[^>]*>/.exec(html)[0];
    const bad = validation({ Errors: [{ Message: 'x' }] });
    assert.ok(testButton(m.renderTemplates(templates(), EN, tplUi({ result: bad }))).includes(' disabled'));
    assert.ok(!testButton(m.renderTemplates(templates(), EN, tplUi({ result: validation() }))).includes(' disabled'));
    assert.ok(testButton(m.renderTemplates(templates(), EN, tplUi({ result: validation(), testing: true }))).includes(' disabled'));
    const none = m.renderTemplates(templates(), EN, tplUi({ result: validation(), destinations: [] }));
    assert.ok(testButton(none).includes(' disabled'));
});

/** Holds every Validate request until the test resolves it; other requests go to the routes. */
function holdValidate(api) {
    const held = [];
    const original = api.fetch;
    api.fetch = request => (request.url.endsWith('/Templates/Validate')
        ? new Promise(resolve => held.push({ body: JSON.parse(request.data), reply: body => resolve(makeResponse(200, body)) }))
        : original(request));
    return held;
}

test('opening the section validates the selected template at once', async () => {
    const { view, api, el } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': validation({ Html: '<b>Dune</b>' }) });
    await show(view);
    assert.deepEqual(posted(api, 'Templates/Validate'), [{ EventKey: 'movie.added', Text: '<b>{{ title }}</b>' }]);
    assert.ok(el('tgj-templates').innerHTML.includes('<b>Dune</b>'));
});

test('preview_ignoresStaleResponse', async () => {
    const { view, api, el, timers } = mount({ 'GET TgJelly/Templates': templates() });
    const held = holdValidate(api);
    await show(view);
    assert.equal(held.length, 1);

    tplInput(view, 'tplText', { value: 'newer {{ title }}' });
    await flush();
    assert.deepEqual(timers.timeouts(), [500]);
    timers.runTimeouts();
    await flush();
    assert.equal(held.length, 2);
    assert.deepEqual(held[1].body, { EventKey: 'movie.added', Text: 'newer {{ title }}' });

    held[1].reply(validation({ Html: 'NEWER' }));
    await flush();
    held[0].reply(validation({ Html: 'OLDER' }));
    await flush();
    const pane = el('tgj-tpl-preview').innerHTML;
    assert.ok(pane.includes('NEWER'), pane);
    assert.ok(!pane.includes('OLDER'));
});

test('typing_doesNotRerenderTextarea', async () => {
    const { view, el, timers } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': validation({ Html: 'typed' }) });
    await show(view);
    const before = el('tgj-templates').writes;
    const previewBefore = el('tgj-tpl-preview').writes;
    tplInput(view, 'tplText', { value: 'abc' });
    tplInput(view, 'tplText', { value: 'abcd' });
    await flush();
    assert.deepEqual(timers.timeouts(), [500], 'one debounce, restarted');
    timers.runTimeouts();
    await flush();
    assert.equal(el('tgj-templates').writes, before, 'the section, and so the textarea, is not written again');
    assert.equal(el('tgj-tpl-preview').writes, previewBefore + 1, 'only the preview pane is replaced');
    assert.ok(el('tgj-tpl-preview').innerHTML.includes('typed'));
});

test('a validate failure shows in the preview and keeps the last good preview, dimmed', async () => {
    let reply = validation({ Html: 'good one' });
    const { view, el, timers } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': () => reply });
    await show(view);
    reply = validation({ Errors: [{ Message: 'Unexpected end', Line: 1, Column: 3 }], Html: '' });
    tplInput(view, 'tplText', { value: '{{' });
    await flush();
    timers.runTimeouts();
    await flush();
    const pane = el('tgj-tpl-preview').innerHTML;
    assert.ok(pane.includes('line 1, column 3: Unexpected end'), pane);
    assert.ok(pane.includes('tgj-tg-stale') && pane.includes('good one'));
});

test('switching events keeps unsaved text, validates at once and drops the pending debounce', async () => {
    const { view, el, api, timers } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': validation() });
    await show(view);
    tplInput(view, 'tplText', { value: 'draft one' });
    tplInput(view, 'tplEvent', { value: 'media.digest' }, 'change');
    await flush();
    assert.deepEqual(timers.timeouts(), []);
    assert.ok(el('tgj-templates').innerHTML.includes('>Hi {{ title }}</textarea>'));
    assert.deepEqual(posted(api, 'Templates/Validate').at(-1), { EventKey: 'media.digest', Text: 'Hi {{ title }}' });
    tplInput(view, 'tplEvent', { value: 'movie.added' }, 'change');
    await flush();
    assert.ok(el('tgj-templates').innerHTML.includes('>draft one</textarea>'));
});

test('save sends the revision and the text', async () => {
    const { view, api } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/movie.added': templates({ Revision: 5 }),
    });
    await show(view);
    tplInput(view, 'tplText', { value: 'New {{ title }}' });
    await flush();
    await click(view, 'tplSave');
    const put = api.calls.find(c => c.type === 'PUT');
    assert.equal(put.url, '/jf/TgJelly/Templates/movie.added');
    assert.deepEqual(JSON.parse(put.data), { ExpectedRevision: 4, Text: 'New {{ title }}' });
});

test('409 keeps typed text', async () => {
    const fresh = templates({ Revision: 9 });
    fresh.Items[0].Text = 'theirs';
    const { view, el, hooks } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/movie.added': { status: 409, body: { Current: fresh } },
    });
    await show(view);
    tplInput(view, 'tplText', { value: 'mine' });
    await click(view, 'tplSave');
    const html = el('tgj-templates').innerHTML;
    assert.ok(html.includes('>mine</textarea>'), html);
    assert.ok(html.includes('tgj-changed'), 'both sides changed the text');
    assert.equal(hooks.state.templates.Revision, 9);
    assert.equal(hooks.state.forms['tpl:movie.added'].dirty, true);
});

test('reset asks inline, cancel keeps the template, confirm sends DELETE with the revision', async () => {
    const after = templates({ Revision: 5 });
    after.Items[1] = { ...after.Items[1], Text: 'Digest {{ count }}', Edited: false, Flag: null };
    const { view, el, api } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'DELETE TgJelly/Templates/media.digest': after,
    });
    await show(view);
    tplInput(view, 'tplEvent', { value: 'media.digest' }, 'change');
    await flush();
    await click(view, 'tplReset');
    assert.ok(el('tgj-templates').innerHTML.includes(m.t(EN, 'web.templates.resetAsk')));
    await click(view, 'tplResetCancel');
    assert.ok(!el('tgj-templates').innerHTML.includes(m.t(EN, 'web.templates.resetAsk')));
    assert.ok(!api.calls.some(c => c.type === 'DELETE'));

    await click(view, 'tplReset');
    await click(view, 'tplResetConfirm');
    const del = api.calls.find(c => c.type === 'DELETE');
    assert.equal(del.url, '/jf/TgJelly/Templates/media.digest?expectedRevision=4');
    const html = el('tgj-templates').innerHTML;
    assert.ok(html.includes('>Digest {{ count }}</textarea>'), html);
    assert.ok(html.includes(`<span class="tgj-badge">${m.t(EN, 'web.templates.builtIn')}</span>`));
    assert.ok(!html.includes(m.t(EN, 'web.templates.resetAsk')));
    assert.deepEqual(posted(api, 'Templates/Validate').at(-1), { EventKey: 'media.digest', Text: 'Digest {{ count }}' });
});

test('send test posts the typed text to the chosen destination and shows the outcome', async () => {
    let reply = { Sent: true, Error: null, Health: { Status: 'Ok' } };
    const { view, el, api } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest(), dest({ Id: 'd2', Title: 'Admins <x>' })] }),
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'POST TgJelly/Templates/Test': () => reply,
    });
    await show(view);
    assert.ok(el('tgj-templates').innerHTML.includes('<option value="d2">Admins &lt;x&gt;</option>'));
    tplInput(view, 'tplText', { value: 'Test {{ title }}' });
    tplInput(view, 'tplTestTo', { value: 'd2' }, 'change');
    await click(view, 'tplTest');
    assert.deepEqual(posted(api, 'Templates/Test'), [{ EventKey: 'movie.added', Text: 'Test {{ title }}', DestinationId: 'd2' }]);
    assert.ok(el('tgj-templates').innerHTML.includes(m.t(EN, 'web.templates.sent')));

    reply = { Sent: false, Error: 'Bot was <kicked>', Health: { Status: 'Lost' } };
    await click(view, 'tplTest');
    const html = el('tgj-templates').innerHTML;
    assert.ok(html.includes('Bot was &lt;kicked&gt;'), html);
    assert.ok(!html.includes(`>${m.t(EN, 'web.templates.sent')}<`));
});

test('the send test button is disabled while a send is in flight', async () => {
    let finish;
    const { view, el, api } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': validation() });
    await show(view);
    const original = api.fetch;
    api.fetch = request => (request.url.endsWith('/Templates/Test')
        ? new Promise(resolve => { finish = () => resolve(makeResponse(200, { Sent: true, Error: null, Health: null })); })
        : original(request));
    clickOn(view, { 'data-tgj-act': 'tplTest' });
    await flush();
    assert.ok(/<button[^>]*data-tgj-act="tplTest"[^>]*disabled/.test(el('tgj-templates').innerHTML));
    finish();
    await flush();
    assert.ok(!/<button[^>]*data-tgj-act="tplTest"[^>]*disabled/.test(el('tgj-templates').innerHTML));
});

test('flag shown for flagged key', async () => {
    const { view, el } = mount({ 'GET TgJelly/Templates': templates() });
    await show(view);
    tplInput(view, 'tplEvent', { value: 'media.digest' }, 'change');
    await flush();
    const html = el('tgj-templates').innerHTML;
    assert.ok(html.includes(m.t(EN, 'web.templates.flagError')));
    assert.ok(m.t(EN, 'web.templates.flagError').includes('so the built-in template was used instead:'));
    assert.ok(!html.includes(m.t(EN, 'web.templates.flagTruncated')));
    assert.ok(html.includes('bad &lt;tag&gt;'));
    tplInput(view, 'tplEvent', { value: 'movie.added' }, 'change');
    await flush();
    assert.ok(!el('tgj-templates').innerHTML.includes('bad &lt;tag&gt;'));
});

test('a truncated flag says the message was shortened, not that the template failed', async () => {
    const dto = templates();
    dto.Items[1].Flag = { Kind: 'truncated', Error: '', At: '2026-10-20T10:00:00Z' };
    const { view, el } = mount({ 'GET TgJelly/Templates': dto });
    await show(view);
    tplInput(view, 'tplEvent', { value: 'media.digest' }, 'change');
    await flush();
    const html = el('tgj-templates').innerHTML;
    assert.ok(html.includes(m.t(EN, 'web.templates.flagTruncated')));
    assert.ok(!html.includes(m.t(EN, 'web.templates.flagError')));
});

test('template section shows events, fields and the docs link', async () => {
    const { view, el } = mount({ 'GET TgJelly/Templates': templates() });
    await show(view);
    const html = el('tgj-templates').innerHTML;
    // Spec §6.1: the guide link sits under the template, after the fields list, inside the left column.
    const link = html.indexOf('href="https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md"');
    const left = html.indexOf('<div class="tgj-tpl-edit">');
    const textarea = html.indexOf('<textarea');
    const fieldsEnd = html.indexOf('</details>', html.indexOf(`<summary>${m.t(EN, 'web.templates.fields')}</summary>`));
    const pane = html.indexOf('<div id="tgj-tpl-preview"');
    assert.ok(left >= 0 && left < textarea && textarea < fieldsEnd && fieldsEnd < link && link < pane, html);
    assert.equal(html.split('docs/templates.md').length, 2, 'one guide link');
    const intro = html.indexOf(m.t(EN, 'web.templates.intro'));
    assert.ok(intro >= 0 && intro < left, 'the intro stays at the top');
    assert.ok(html.includes('Movie added') && html.includes('Digest'));
    assert.ok(html.includes('The &lt;title&gt;.'));
    assert.ok(html.includes('class="tgj-template-text"'));
});

test('a spoiler in the preview is revealed on click', async () => {
    const { view } = mount();
    const classes = new Set(['tgj-tg-spoiler']);
    const spoiler = { classList: { toggle: name => (classes.has(name) ? classes.delete(name) : classes.add(name)) } };
    const event = new Event('click');
    Object.defineProperty(event, 'target', { value: { closest: selector => (selector === '.tgj-tg-spoiler' ? spoiler : null) } });
    view.dispatchEvent(event);
    assert.ok(classes.has('tgj-tg-revealed'));
});

test('the page is a thin shell: the stylesheet and the root, no inline styles', () => {
    const page = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html', import.meta.url), 'utf8');
    assert.match(page, /^<div data-role="page" id="tgjConfigPage" [^>]*data-controller="__plugin\/tgjelly.js">/);
    assert.ok(page.includes('<link id="tgjelly-css" rel="stylesheet" href="configurationpage?name=tgjelly.css">'));
    assert.ok(page.includes('<div id="tgjelly-root"></div>'));
    assert.ok(!page.includes('<style'), 'the old rules live in tgjelly.css');
    assert.ok(!page.includes('${'));
    const css = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');
    assert.ok(css.includes('#tgjelly-root .tgj-tpl-grid'), 'the template editor rules moved');
    assert.ok(css.includes('#tgjelly-root .tgj-tg-bubble'), 'the preview bubble rules moved, tgj-tg- prefixed');
});

// --- source checks ---

test('every t(…) key used in tgjelly.js exists in en.json', () => {
    const keys = [...SOURCE.matchAll(/\b(?:t|text)\(S, '([^']+)'/g)].map(x => x[1]);
    const plurals = [...SOURCE.matchAll(/\b(?:plural|pluralText)\(S, '([^']+)'/g)].map(x => x[1]);
    assert.ok(keys.length > 0);
    for (const key of keys) {
        assert.ok(key in EN, key);
    }
    for (const key of plurals) {
        assert.ok(`${key}.one` in EN && `${key}.other` in EN, key);
    }
    assert.ok(!/\bt\((?!S, )[a-z]/.test(SOURCE.replace(/function t\(/, '')), 'every t( call passes S first');
});

// Sent by TgJellyController as the Connected status message, not rendered by the page itself.
const SERVER_SIDE_KEY = 'web.status.connected';
// Plan 8 task 3 adds the cue strings before the controller renders them; drop this exemption when the controller is wired.
const NOT_YET_WIRED = /^web\.autosave\./;

test('every web.* key in en.json is used by tgjelly.js', () => {
    for (const key of Object.keys(EN).filter(k => k.startsWith('web.') && k !== SERVER_SIDE_KEY && !NOT_YET_WIRED.test(k))) {
        const base = key.replace(/\.(one|other)$/, '');
        assert.ok(SOURCE.includes(`'${base}'`), key);
    }
});

// --- one revision across the sections (final review I1, spec §10.4) ---

/** A server with one Revision for every section, like PluginConfiguration.Revision; each save moves it on. */
function oneRevisionServer() {
    const server = { revision: 5, items: [dest()], tplText: '' };
    const conflictOr = (request, ok, current) => {
        if (JSON.parse(request.data).ExpectedRevision !== server.revision) {
            return { status: 409, body: { Current: current() } };
        }

        server.revision++;
        return ok();
    };
    const tpl = () => templates({ Revision: server.revision });
    server.routes = {
        'GET TgJelly/Status': () => status({ Revision: server.revision }),
        'GET TgJelly/Settings': () => settings({ Revision: server.revision }),
        'GET TgJelly/Destinations': () => destinations({ Revision: server.revision, Items: server.items }),
        'GET TgJelly/Templates': tpl,
        'PUT TgJelly/Settings': r => conflictOr(r, () => settings({ Revision: server.revision }), () => settings({ Revision: server.revision })),
        'PUT TgJelly/Destinations/d1': r => conflictOr(r, () => dest({ Enabled: false }), () => destinations({ Revision: server.revision, Items: server.items })),
        'PUT TgJelly/Templates/movie.added': r => conflictOr(r, tpl, tpl),
    };
    return server;
}

test('save Messages, then toggle Sending: the PUT carries the new revision, no 409', async () => {
    const server = oneRevisionServer();
    const { view, api, el } = mount(server.routes);
    await show(view);
    msgChange(view, 'ShowPoster', false);
    await click(view, 'saveSettings');
    assert.equal(server.revision, 6);

    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });

    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 6, Enabled: false }]);
    assert.equal(server.revision, 7);
    assert.ok(!destEl(el).includes(m.t(EN, 'web.dest.conflictPlain')));
});

test('a destination change, then a template save and a settings save: each sends the current revision', async () => {
    const server = oneRevisionServer();
    const { view, api, el } = mount(server.routes);
    await show(view);
    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });
    tplInput(view, 'tplText', { value: 'New {{ title }}' });
    await click(view, 'tplSave');
    msgChange(view, 'ShowPoster', false);
    await click(view, 'saveSettings');

    const revisions = api.calls.filter(c => c.type === 'PUT').map(c => JSON.parse(c.data).ExpectedRevision);
    assert.deepEqual(revisions, [5, 6, 7]);
    assert.equal(server.revision, 8);
    assert.ok(!el('tgj-templates').innerHTML.includes(m.t(EN, 'web.templates.conflict')));
    assert.ok(!el('tgj-messages').innerHTML.includes(m.t(EN, 'web.settings.conflict')));
});

test('a status poll with a higher revision reloads the sections, keeping unsaved edits', async () => {
    const server = oneRevisionServer();
    const { view, api, el, hooks, timers } = mount(server.routes);
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destInput(view, 'destPreset', { 'data-tgj-id': 'd1' }, { value: 'NewMedia' });
    input(view, 'publicUrl', 'https://typed.example.com');
    await flush();

    // The admin adds a destination through the bot's picker in another tab.
    server.revision = 6;
    server.items = [dest(), dest({ Id: 'd2', Title: 'Added in the bot' })];
    timers.tick();
    await flush();

    assert.ok(destEl(el).includes('Added in the bot'));
    assert.equal(hooks.state.destinations.Revision, 6);
    assert.equal(hooks.state.settings.Revision, 6);
    assert.equal(hooks.state.templates.Revision, 6);
    assert.equal(hooks.state.forms['dest:d1'].local.Preset, 'NewMedia');
    assert.equal(hooks.state.forms['dest:d1'].dirty, true);
    assert.equal(hooks.state.forms.settings.local.PublicUrl, 'https://typed.example.com');

    // A poll at the same revision loads nothing else.
    const gets = () => api.calls.filter(c => c.type === 'GET' && c.url !== '/jf/TgJelly/Status').length;
    const before = gets();
    timers.tick();
    await flush();
    assert.equal(gets(), before);
});

// --- the template textarea keeps its caret across section renders (final review M11) ---

/** The template textarea as a live field: every write of the templates section replaces it (see liveFields). */
const liveTextarea = (view, doc) => liveFields(view, doc, 'tgj-templates', [{ tag: 'textarea', attributes: { id: 'tgj-tpl-text', 'data-tgj-input': 'tplText' } }]);

test('a Destinations render while typing keeps the textarea focus, selection and scroll', async () => {
    const server = oneRevisionServer();
    const { view, el, doc } = mount(server.routes);
    await show(view);
    const textarea = liveTextarea(view, doc);
    const typing = textarea();
    typing.focus();
    Object.assign(typing, { selectionStart: 3, selectionEnd: 5, selectionDirection: 'forward', scrollTop: 40, scrollLeft: 2 });
    tplInput(view, 'tplText', { value: 'abcdef {{ title }}' });
    const writes = el('tgj-templates').writes;

    // Toggling a destination renders Destinations, which renders the templates section with the typed text.
    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });

    assert.ok(el('tgj-templates').writes > writes, 'the section was written again');
    assert.ok(el('tgj-templates').innerHTML.includes('abcdef {{ title }}</textarea>'), 'the unsaved text is kept');
    const now = textarea();
    assert.notEqual(now, typing, 'the textarea was replaced');
    assert.equal(doc.activeElement, now);
    assert.deepEqual(now.focusOptions, { preventScroll: true });
    assert.deepEqual([now.selectionStart, now.selectionEnd, now.selectionDirection], [3, 5, 'forward']);
    assert.deepEqual([now.scrollTop, now.scrollLeft], [40, 2]);
});

test('a render without the textarea focused does not take the focus', async () => {
    const server = oneRevisionServer();
    const { view, el, doc } = mount(server.routes);
    await show(view);
    const textarea = liveTextarea(view, doc);
    doc.activeElement = null;
    tplInput(view, 'tplText', { value: 'typed elsewhere' });
    const writes = el('tgj-templates').writes;

    await destAct(view, 'enabled', { 'data-tgj-id': 'd1' });

    assert.ok(el('tgj-templates').writes > writes);
    assert.equal(textarea().focusCalls, 0);
    assert.equal(doc.activeElement, null);
});

// --- the shell, section navigation and focus-safe writes (plan 8 Task 4, design §5.1-5.2) ---

/** Each <section data-tgj-section> of the shell markup: name → { attrs, body }. */
function shellSections(html) {
    return new Map([...html.matchAll(/<section([^>]*)data-tgj-section="(\w+)"([^>]*)>([\s\S]*?)<\/section>/g)]
        .map(([, before, name, after, body]) => [name, { attrs: before + after, body }]));
}

/** Each nav button of the shell markup: name → its outer HTML. */
function shellNav(html) {
    return new Map([...html.matchAll(/<button[^>]*data-tgj-nav="(\w+)"[^>]*>[\s\S]*?<\/button>/g)].map(match => [match[1], match[0]]));
}

/** The section the page shows: the one host not hidden (exactly one). */
function shownSection(el) {
    const shown = m.SECTIONS.filter(name => el(`tgj-sec-${name}`).hidden === false);
    assert.equal(shown.length, 1, `exactly one section is shown (${shown.join(', ')})`);
    return shown[0];
}

const HOSTS = {
    overview: ['tgj-status', 'tgj-connect', 'tgj-bot', 'tgj-summary'],
    destinations: ['tgj-destinations'],
    messages: ['tgj-messages'],
    templates: ['tgj-templates'],
    advanced: ['tgj-proxy', 'tgj-batching', 'tgj-updates', 'tgj-manual', 'tgj-diagnostics'],
};

test('shell_rendersAllSections_onlyActiveVisible', () => {
    assert.deepEqual(m.SECTIONS, ['overview', 'destinations', 'messages', 'templates', 'advanced']);
    const html = m.renderShell(EN, {
        active: 'messages',
        connection: { kind: 'ok', text: 'Connected · @jf_bot' },
        counts: { destinations: '3', templates: '1 edited' },
    });

    assert.match(html, /<h1 class="tgj-header-title">TgJelly<\/h1>/);
    assert.ok(html.includes(ui.stateLabel({ kind: 'ok', text: 'Connected · @jf_bot' })), 'the connection is a state label');
    assert.match(html, /<nav class="tgj-sidebar" aria-label="[^"]+">/);

    const nav = shellNav(html);
    assert.deepEqual([...nav.keys()], m.SECTIONS);
    const labels = { overview: 'Overview', destinations: 'Destinations', messages: 'Messages', templates: 'Templates', advanced: 'Advanced' };
    for (const [name, button] of nav) {
        assert.equal(EN[`web.nav.${name}`], labels[name]);
        assert.ok(button.includes(`<span class="tgj-nav-label">${labels[name]}</span>`), button);
        assert.ok(button.startsWith('<button type="button"'), 'a real button, never a link with href="#…"');
        assert.equal(/aria-current="page"/.test(button), name === 'messages', `${name} aria-current`);
    }

    assert.match(nav.get('destinations'), /<span id="tgj-nav-count-destinations" class="tgj-nav-count">3<\/span>/);
    assert.match(nav.get('templates'), /<span id="tgj-nav-count-templates" class="tgj-nav-count">1 edited<\/span>/);
    assert.match(nav.get('overview'), /<span id="tgj-nav-count-overview" class="tgj-nav-count"><\/span>/);

    const sections = shellSections(html);
    assert.deepEqual([...sections.keys()], m.SECTIONS);
    for (const [name, { attrs, body }] of sections) {
        assert.equal(/\shidden(\s|$)/.test(attrs), name !== 'messages', `${name} hidden`);
        assert.ok(attrs.includes(`id="tgj-sec-${name}"`));
        const ids = [...body.matchAll(/id="([^"]+)"/g)].map(x => x[1]);
        assert.deepEqual(ids, HOSTS[name], `${name} holds today's containers, in order`);
    }

    // Templates sit between Messages and Advanced, outside Advanced (as in v0.3).
    assert.ok(html.indexOf('id="tgj-messages"') < html.indexOf('id="tgj-templates"'));
    assert.ok(html.indexOf('id="tgj-templates"') < html.indexOf('data-tgj-section="advanced"'));
    assert.doesNotMatch(html, /[●✓✔✅⚠❌✗🟢🔴]/u);
});

test('renderShell escapes the label and counts, and an unknown active section shows Overview', () => {
    const html = m.renderShell(EN, { active: 'nope', connection: { kind: 'warning', text: '<b>x</b>' }, counts: { destinations: '<i>' } });
    assert.ok(html.includes('&lt;b&gt;x&lt;/b&gt;') && !html.includes('<b>x</b>'));
    assert.ok(html.includes('class="tgj-nav-count">&lt;i&gt;</span>'));
    const hidden = [...shellSections(html)].filter(([, s]) => !/\shidden(\s|$)/.test(s.attrs)).map(([name]) => name);
    assert.deepEqual(hidden, ['overview']);
});

test('text is the plain-text lookup: no escaping, same fallbacks as t', () => {
    const S = { k: "Can't & <b>{x}</b> {missing}" };
    assert.equal(m.text(S, 'k', { x: '<i>' }), "Can't & <b><i></b> {missing}");
    assert.equal(m.text(S, 'no.such'), 'no.such');
    assert.equal(m.text(null, 'no.such'), 'no.such');
    assert.equal(m.pluralText({ 'x.one': "{count} chat's", 'x.other': '{count} chats' }, 'x', 1000), '1,000 chats');
});

test('connectionLabel: connected names the bot, no token is muted, problems are a warning', () => {
    assert.deepEqual(m.connectionLabel(status(), EN), { kind: 'ok', text: 'Connected · @jf_bot' });
    assert.deepEqual(m.connectionLabel(status({ HasToken: false, BotUsername: null, ConnectionState: 'NoToken' }), EN), { kind: 'muted', text: EN['web.header.notConnected'] });
    assert.deepEqual(m.connectionLabel(status({ ConnectionState: 'TokenRejected' }), EN), { kind: 'warning', text: EN['web.header.attention'] });
    assert.deepEqual(m.connectionLabel(status({ ConnectionState: 'Connecting' }), EN), { kind: 'muted', text: EN['web.header.connecting'] });
    assert.deepEqual(m.connectionLabel(null, EN, true), { kind: 'warning', text: EN['web.header.unknown'] });
    for (const key of ['web.header.connected', 'web.header.notConnected', 'web.header.attention', 'web.header.connecting', 'web.header.unknown']) {
        assert.doesNotMatch(EN[key], /[●✓✔✅⚠❌✗🟢🔴]/u, key);
    }
});

test('the header and the sidebar counts follow the data', async () => {
    let current = status();
    const { view, el, timers } = mount({ 'GET TgJelly/Status': () => current, 'GET TgJelly/Templates': templates() });
    await show(view);
    assert.equal(el('tgj-conn').innerHTML, ui.stateLabel({ kind: 'ok', text: 'Connected · @jf_bot' }));
    assert.equal(el('tgj-nav-count-destinations').innerHTML, '1');
    assert.equal(el('tgj-nav-count-templates').innerHTML, '1 edited');

    current = status({ ConnectionState: 'Unreachable' });
    timers.tick();
    await flush();
    assert.equal(el('tgj-conn').innerHTML, ui.stateLabel({ kind: 'warning', text: EN['web.header.attention'] }));
    assert.equal(el('tgjelly-root').writes, 1, 'the shell is not written again');
});

test('nav_click_switchesSection_updatesHash', async () => {
    const { view, el, win, api } = mount();
    await show(view);
    assert.equal(shownSection(el), 'overview');
    const calls = api.calls.length;

    const { event } = clickOn(view, { 'data-tgj-nav': 'destinations' });
    assert.ok(event.defaultPrevented);
    assert.equal(shownSection(el), 'destinations');
    for (const name of m.SECTIONS) {
        assert.equal(el(`tgj-nav-${name}`).getAttribute('aria-current'), name === 'destinations' ? 'page' : null, name);
    }

    assert.equal(win.location.hash, '#/configurationpage?name=TgJelly&tgj=destinations', "kept beside the router's own hash");
    assert.deepEqual(win.history.state, { idx: 3 }, "the router's history state is kept");

    clickOn(view, { 'data-tgj-nav': 'messages' });
    assert.equal(shownSection(el), 'messages');
    assert.equal(win.location.hash, '#/configurationpage?name=TgJelly&tgj=messages', 'replaced, not appended');
    assert.equal(win.history.replaced.length, 2, 'replaceState only: no history entries, no router navigation');

    clickOn(view, { 'data-tgj-nav': 'bogus' });
    assert.equal(shownSection(el), 'messages', 'an unknown name changes nothing');
    await flush();
    assert.equal(api.calls.length, calls, 'switching sections loads nothing');
});

test('hash_restoresSection_unknownFallsBack', async () => {
    assert.equal(m.sectionFromHash('#/configurationpage?name=TgJelly&tgj=templates'), 'templates');
    assert.equal(m.sectionFromHash('#tgj=messages'), 'messages');
    assert.equal(m.sectionFromHash('#tgj=bogus'), 'overview');
    assert.equal(m.sectionFromHash('#tgj=%E0%A4%A'), 'overview', 'a malformed escape');
    assert.equal(m.sectionFromHash('#/configurationpage?name=tgjelly'), 'overview', 'name=tgjelly is not the section');
    assert.equal(m.sectionFromHash(''), 'overview');
    assert.equal(m.sectionFromHash(undefined), 'overview');
    assert.equal(m.hashWithSection('', 'messages'), '#tgj=messages');
    assert.equal(m.hashWithSection('#tgj=messages', 'advanced'), '#tgj=advanced');
    assert.equal(m.hashWithSection('#/configurationpage', 'advanced'), '#/configurationpage?tgj=advanced');
    assert.equal(m.hashWithSection('#/configurationpage?name=TgJelly&tgj=messages&x=1', 'overview'), '#/configurationpage?name=TgJelly&tgj=overview&x=1');

    const restored = mount({}, { hash: '#/configurationpage?name=TgJelly&tgj=advanced' });
    await show(restored.view);
    assert.equal(shownSection(restored.el), 'advanced');
    assert.equal(restored.el('tgj-nav-advanced').getAttribute('aria-current'), 'page');

    const unknown = mount({}, { hash: '#/configurationpage?name=TgJelly&tgj=nope' });
    await show(unknown.view);
    assert.equal(shownSection(unknown.el), 'overview');

    const bare = mount({}, { hash: '#tgj=templates' });
    await show(bare.view);
    assert.equal(shownSection(bare.el), 'templates');
});

test('notConnected_opensOverview', async () => {
    const { view, el, timers } = mount({ 'GET TgJelly/Status': noToken() }, { hash: '#/configurationpage?name=TgJelly&tgj=messages' });
    await show(view);
    assert.equal(shownSection(el), 'overview', 'Overview leads with Connect');
    assert.equal(el('tgj-conn').innerHTML, ui.stateLabel({ kind: 'muted', text: EN['web.header.notConnected'] }));

    // The other sections stay reachable.
    clickOn(view, { 'data-tgj-nav': 'messages' });
    assert.equal(shownSection(el), 'messages');
    timers.tick();
    await flush();
    assert.equal(shownSection(el), 'messages', 'a poll does not send the admin back');
});

test('the shell waits for the stylesheet, so its transitions do not play on first show', async () => {
    const loaded = mount();
    const link = new EventTarget();
    link.sheet = null;
    loaded.view.elements.set('tgjelly-css', link);
    loaded.view.dispatchEvent(new Event('viewshow'));
    await flush();
    assert.equal(loaded.el('tgjelly-root').writes, 0, 'nothing shown before the stylesheet');
    link.dispatchEvent(new Event('load'));
    await flush();
    assert.equal(loaded.el('tgjelly-root').writes, 1);
    assert.ok(!loaded.timers.timeouts().includes(1000), 'the fallback timer is cleared');

    const failed = mount();
    const broken = new EventTarget();
    broken.sheet = null;
    failed.view.elements.set('tgjelly-css', broken);
    failed.view.dispatchEvent(new Event('viewshow'));
    await flush();
    broken.dispatchEvent(new Event('error'));
    await flush();
    assert.equal(failed.el('tgjelly-root').writes, 1, 'a failed stylesheet still shows the page');

    const slow = mount();
    const stuck = new EventTarget();
    stuck.sheet = null;
    slow.view.elements.set('tgjelly-css', stuck);
    slow.view.dispatchEvent(new Event('viewshow'));
    await flush();
    assert.deepEqual(slow.timers.timeouts(), [1000]);
    slow.timers.runTimeouts();
    await flush();
    assert.equal(slow.el('tgjelly-root').writes, 1, 'after 1 s the page shows anyway');

    const ready = mount();
    ready.view.elements.set('tgjelly-css', Object.assign(new EventTarget(), { sheet: {} }));
    await show(ready.view);
    assert.equal(ready.el('tgjelly-root').writes, 1, 'an already loaded stylesheet does not wait');
});

test('fieldSelector finds a field again by its id or its data-tgj attributes', () => {
    const field = attrs => ({ getAttribute: name => attrs[name] ?? null });
    assert.equal(m.fieldSelector(field({ id: 'tgj-tpl-text', 'data-tgj-input': 'tplText' })), '[id="tgj-tpl-text"]');
    assert.equal(m.fieldSelector(field({ 'data-tgj-input': 'event', 'data-tgj-id': 'd"1', 'data-tgj-key': 'media.movie' })),
        '[data-tgj-input="event"][data-tgj-id="d\\"1"][data-tgj-key="media.movie"]');
    assert.equal(m.fieldSelector(field({})), null);
});

const URL_FIELD = [{ tag: 'input', attributes: { 'data-tgj-input': 'publicUrl', 'data-tgj-field': 'PublicUrl' } }];

test('sectionWrite_keepsFocusInAnyField', async () => {
    let current = settings({ PublicUrl: 'https://a.example' });
    const { view, el, doc, hooks } = mount({ 'GET TgJelly/Settings': () => current });
    await show(view);
    const field = liveFields(view, doc, 'tgj-messages', URL_FIELD);
    const typing = field();
    assert.equal(typing.value, 'https://a.example');
    typing.focus();
    // Typed, and its input event not handled yet: the field's own value is the truth.
    typing.value = 'https://a.example/jf';
    Object.assign(typing, { selectionStart: 8, selectionEnd: 10, selectionDirection: 'backward', scrollLeft: 30 });
    const writes = el('tgj-messages').writes;

    // Another tab changes a different setting; the Messages section renders again.
    current = settings({ PublicUrl: 'https://a.example', Revision: 6, Message: { ...settings().Message, ShowPoster: false } });
    await hooks.reloadSection('messages', { fresh: true });

    assert.ok(el('tgj-messages').writes > writes, 'the section was written again');
    const now = field();
    assert.notEqual(now, typing, 'the input was replaced');
    assert.equal(doc.activeElement, now, 'the new input has the focus');
    assert.deepEqual(now.focusOptions, { preventScroll: true });
    assert.equal(now.value, 'https://a.example/jf', 'what the admin typed stays');
    assert.deepEqual([now.selectionStart, now.selectionEnd, now.selectionDirection, now.scrollLeft], [8, 10, 'backward', 30]);
});

test('a focused field the admin has not changed takes the new value', async () => {
    let current = settings({ PublicUrl: 'https://a.example' });
    const { view, doc, hooks } = mount({ 'GET TgJelly/Settings': () => current });
    await show(view);
    const field = liveFields(view, doc, 'tgj-messages', URL_FIELD);
    field().focus();

    current = settings({ PublicUrl: 'https://b.example', Revision: 6 });
    await hooks.reloadSection('messages', { fresh: true });

    assert.equal(field().value, 'https://b.example');
    assert.equal(doc.activeElement, field(), 'and keeps the focus');
});

test('a focused select keeps the focus; a field outside the section is left alone', async () => {
    let current = settings();
    const { view, doc, hooks } = mount({ 'GET TgJelly/Settings': () => current });
    await show(view);
    const mode = liveFields(view, doc, 'tgj-updates', [{ tag: 'select', attributes: { 'data-tgj-input': 'updMode' } }]);
    const url = liveFields(view, doc, 'tgj-messages', URL_FIELD);
    mode().focus();
    const focused = mode();

    current = settings({ Revision: 6, UpdateCheck: { Enabled: true, Mode: 'AnyNewer' } });
    await hooks.reloadSection('messages', { fresh: true });
    assert.equal(doc.activeElement, focused, 'a write of Messages does not touch the Updates select');
    await hooks.reloadSection('updates', { fresh: true });
    assert.equal(doc.activeElement, mode());
    assert.notEqual(mode(), focused);
    assert.equal(mode().value, 'AnyNewer', 'a select shows the new value: its choice was already made');
    assert.equal(url().focusCalls, 0);
});

test('pendingField_keepsTypedValueAcrossRefresh', async () => {
    let current = settings({ PublicUrl: 'https://old.example' });
    const { view, el, doc, hooks } = mount({ 'GET TgJelly/Settings': () => current });
    await show(view);
    const field = liveFields(view, doc, 'tgj-messages', URL_FIELD);
    // Typed, then the focus moved on; its auto-save hasn't gone out yet.
    field().value = 'https://typed.example';
    hooks.pendingFields.add(m.fieldSelector(field()));

    current = settings({ PublicUrl: 'https://server.example', Revision: 6 });
    await hooks.reloadSection('messages', { fresh: true });

    assert.ok(el('tgj-messages').innerHTML.includes('value="https://server.example"'), 'the markup has the server value');
    assert.equal(field().value, 'https://typed.example', 'the pending field keeps the typed value');
    assert.equal(field().focusCalls, 0, 'and does not take the focus');
    assert.equal(doc.activeElement, undefined);

    // Once its save has settled, the field follows the server again.
    hooks.pendingFields.clear();
    current = settings({ PublicUrl: 'https://later.example', Revision: 7 });
    await hooks.reloadSection('messages', { fresh: true });
    assert.equal(field().value, 'https://later.example');
});

// --- Overview section (plan 8, task 5) ---

const SYMBOLS = /[●✓✅⚠❌]|️/u;

/** Every status the Overview must render without a symbol. */
const STATUS_FIXTURES = {
    connected: () => status(),
    noToken: () => noToken(),
    webhookConflict: () => status({ ConnectionState: 'WebhookConflict', ConnectionMessage: 'Another webhook is set' }),
    incompatible: () => status({ Compatible: false, CompatMessage: 'Install the 10.11 build', ConnectionState: 'Incompatible' }),
    rejected: () => status({ ConnectionState: 'TokenRejected', ConnectionMessage: 'Telegram rejected the token' }),
    withNoticesAndErrors: () => status({
        Notices: [{ Id: 'n1', Kind: 'X', Text: 'A notice' }],
        LastErrors: [{ Source: 'outbox', Message: 'chat gone', At: '2026-10-20T10:00:00Z' }],
        UnhealthyDestinations: 1,
    }),
};

const overviewHtml = el => ['tgj-status', 'tgj-connect', 'tgj-bot', 'tgj-summary'].map(id => el(id).innerHTML).join('\n');

test('overview_cards_statusBotSummary', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest(), dest({ Id: 'd2', Health: { Status: 'NeedsPermissions', PhotosBlocked: false } })] }),
        'GET TgJelly/Templates': templates(),
    });
    await show(view);

    const statusHtml = el('tgj-status').innerHTML;
    assert.ok(statusHtml.includes('class="tgj-card"') && statusHtml.includes('>Status</h2>'));
    assert.ok(statusHtml.includes(ui.stateLabel({ kind: 'ok', text: 'Connected to Telegram' })), 'health is a state label');
    assert.ok(statusHtml.includes('class="tgj-row'));

    const botHtml = el('tgj-bot').innerHTML;
    assert.ok(botHtml.includes('class="tgj-card"') && botHtml.includes('>Bot</h2>'));
    assert.ok(botHtml.includes('@jf_bot'));
    for (const act of ['changeToken', 'disconnect']) {
        assert.match(botHtml, new RegExp(`<button type="button" class="tgj-btn[^"]*" data-tgj-act="${act}"`));
    }
    assert.match(botHtml, /<a [^>]*class="tgj-btn"[^>]*href="https:\/\/t\.me\/jf_bot\?start=setup"[^>]*>Open in Telegram<\/a>/);
    assert.equal(el('tgj-connect').innerHTML, '', 'no connect form while a bot is connected');

    const summary = el('tgj-summary').innerHTML;
    assert.ok(summary.includes('class="tgj-card"'));
    assert.ok(summary.includes('2 destinations'));
    assert.ok(summary.includes(ui.stateLabel({ kind: 'warning', text: '1 needs attention' })));
    assert.ok(summary.includes('1 edited'));
    assert.ok(summary.includes(ui.stateLabel({ kind: 'warning', text: '1 flagged' })));
    assert.ok(summary.includes('No notices'));
});

test('overview_noBot_leadsWithConnectCard_noSave', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': noToken() });
    await show(view);
    assert.ok(el('tgj-connect').innerHTML.includes('class="tgj-card"'));
    assert.equal(el('tgj-bot').innerHTML, '');
    assert.equal(el('tgj-summary').innerHTML, '', 'no summary before a bot is connected');
    await click(view, 'connectExisting');
    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes('data-tgj-act="saveToken"') && html.includes('>Connect</button>'));
    assert.ok(!/>Save</.test(html), 'no Save button');
    assert.match(html, /<input[^>]*type="password"[^>]*data-tgj-input="token"/);
});

test('summary_linksToSections', async () => {
    const { view, el, win } = mount({
        'GET TgJelly/Destinations': destinations(),
        'GET TgJelly/Templates': templates(),
    });
    await show(view);
    const summary = el('tgj-summary').innerHTML;
    const targets = [...summary.matchAll(/data-tgj-nav="(\w+)"/g)].map(match => match[1]);
    assert.deepEqual(targets, ['destinations', 'templates', 'advanced']);
    for (const target of targets) {
        assert.match(summary, new RegExp(`<button type="button" class="tgj-btn"[^>]*data-tgj-nav="${target}"`));
    }

    clickOn(view, { 'data-tgj-nav': 'templates' });
    assert.equal(shownSection(el), 'templates');
    assert.match(win.location.hash, /tgj=templates/);
    clickOn(view, { 'data-tgj-nav': 'destinations' });
    assert.equal(shownSection(el), 'destinations');
});

test('summary_noDestinations_saysSo', () => {
    const html = m.renderOverviewSummary(status(), destinations({ Items: [] }), templates({ Items: [] }), EN);
    assert.ok(html.includes('No destinations yet'));
    assert.ok(html.includes('data-tgj-nav="destinations"'));
});

test('overview_noSymbols', async () => {
    for (const [name, make] of Object.entries(STATUS_FIXTURES)) {
        const { view, el } = mount({
            'GET TgJelly/Status': make(),
            'GET TgJelly/Destinations': destinations({ Items: [dest({ Health: { Status: 'Lost', PhotosBlocked: true } })] }),
            'GET TgJelly/Templates': templates(),
        });
        await show(view);
        assert.ok(!SYMBOLS.test(overviewHtml(el)), `${name}: ${overviewHtml(el).match(SYMBOLS)}`);
        if (make().HasToken) {
            await click(view, 'disconnect');
            assert.ok(!SYMBOLS.test(overviewHtml(el)), `${name} confirming`);
        } else {
            await click(view, 'connectNew');
            assert.ok(!SYMBOLS.test(overviewHtml(el)), `${name} connect form`);
        }
    }
});

test('disconnect_asksInline', async () => {
    const { view, api, el } = mount();
    await show(view);
    await click(view, 'disconnect');
    const html = el('tgj-bot').innerHTML;
    assert.ok(html.includes('class="tgj-confirm"'), 'the inline confirmation component');
    assert.ok(html.includes('Disconnect this bot? Notifications stop until you connect a bot.'));
    assert.match(html, /data-tgj-act="disconnectYes"/);
    assert.match(html, /data-tgj-act="disconnectNo"/);
    assert.equal(api.calls.filter(c => c.type === 'DELETE').length, 0, 'nothing is sent until confirmed');
    assert.ok(!/\b(confirm|alert|prompt)\(/.test(SOURCE.replace(/inlineConfirm\(/g, '')), 'no browser dialog in the page');
});
