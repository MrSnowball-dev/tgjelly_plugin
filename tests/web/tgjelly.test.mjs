// Dashboard page tests (spec §11.1). Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import * as msg from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-messages.js';
import { makeView, makeApiClient, makeResponse, fakeTimers, flush, clickOn, dispatchOn, fakeTarget, liveFields } from './fake-view.mjs';

// The page's own modules: the controller, and the Messages section and the preview it was split into (plan 9).
const SOURCE = ['tgjelly.js', 'tgjelly-messages.js', 'tgjelly-preview.js']
    .map(file => readFileSync(new URL(`../../src/Jellyfin.Plugin.TgJelly/Web/${file}`, import.meta.url), 'utf8')).join('\n');
const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));

// The page has only #tgjelly-root; the shell the controller writes into it adds the rest (makeView registers them).
// The section hosts are listed too, so a test can hold an element before the shell is written.
const IDS = [
    'tgjelly-root', 'tgj-status', 'tgj-connect', 'tgj-bot', 'tgj-destinations', 'tgj-messages', 'tgj-demo',
    'tgj-proxy', 'tgj-batching', 'tgj-updates', 'tgj-tpl-preview', 'tgj-manual', 'tgj-diagnostics',
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

const MEDIA_MESSAGES = ['media.movie', 'media.episodes', 'media.album', 'media.musicvideo', 'media.book', 'media.digest'];
const FLAT_MESSAGE = { ShowPoster: true, ShowOverview: true, ShowRatingGenres: true, ShowOpenInJellyfin: true, ShowProviderLinks: false };

/** A SettingsDto Message as the server sends it: every message's switches (media ones from flat; alerts off). */
function messageDto(flat = FLAT_MESSAGE, announce = false) {
    const off = { ShowPoster: false, ShowOverview: false, ShowRatingGenres: false, ShowOpenInJellyfin: false, ShowProviderLinks: false };
    return {
        PerMessage: [...MEDIA_MESSAGES.map(EventKey => ({ EventKey, Switches: { ...flat } })), ...['security.lockout', 'server.started'].map(EventKey => ({ EventKey, Switches: { ...off } }))],
        AnnounceUpgrades: announce,
    };
}

/** Merges a PUT body's Message into a SettingsDto Message, as the server does: by event key, switch by switch. */
function mergeMessage(dto, change) {
    return {
        PerMessage: dto.PerMessage.map(e => {
            const sent = (change.PerMessage ?? []).find(c => c.EventKey === e.EventKey);
            return sent ? { EventKey: e.EventKey, Switches: { ...e.Switches, ...sent.Switches } } : e;
        }),
        AnnounceUpgrades: change.AnnounceUpgrades ?? dto.AnnounceUpgrades,
    };
}

function settings(overrides = {}) {
    return {
        Revision: 5,
        PublicUrl: '',
        PublicUrlValid: true,
        PublicUrlSuggestion: '',
        Message: messageDto(),
        Batching: { QuietWindowSeconds: 120, MaxHoldMinutes: 30, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 },
        UpdateCheck: { Enabled: true, Fixes: true, NewReleases: true, NewMajor: false },
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
    // One Revision for every section, as the server has (settings() is at 5 too).
    return { Revision: 5, Items: [dest()], DetectedChats: [], ManualUnlocked: false, Catalog: CATALOG, ...overrides };
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
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
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
        'POST /jf/TgJelly/Templates/Validate',
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

    const post = api.calls.find(c => c.type === 'POST' && c.url.endsWith('/Status/Dismiss'));
    assert.ok(post);
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

test('Bot section does not repeat the username', () => {
    const html = m.renderBot(status(), { OpenBot: 'https://t.me/jf_bot?start=setup' }, EN, {});
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
    assert.ok(!html.includes('saveProxy'), 'the proxy auto-saves: no Save button');
});

test('the Bot section shows the username and the buttons', async () => {
    const { view, el } = mount();
    await show(view);
    const html = el('tgj-bot').innerHTML;
    assert.ok(html.includes('@jf_bot'));
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

// --- auto-save helpers (plan 8, task 6) ---

/** The newest cue spot for a key, as { className, innerHTML } (an empty one when the key has none). */
const cue = (view, key) => ({ className: 'tgj-cue', innerHTML: '', ...view.cues.get(key) });
const SAVED_CUE = { className: 'tgj-cue tgj-cue--saved', innerHTML: ui.label(EN['web.autosave.saved'], 'good') };
const SAVING_CUE = { className: 'tgj-cue tgj-cue--saving', innerHTML: ui.label(EN['web.autosave.saving'], 'progress') };
const IDLE_CUE = { className: 'tgj-cue', innerHTML: '' };
/** A Not saved cue: the bad label, then the reason as plain text (escaped: nothing in it is markup). */
const errorCue = reason => ({
    className: 'tgj-cue tgj-cue--error',
    innerHTML: `${ui.label(EN['web.autosave.notSaved'], 'bad')} <span class="tgj-cue-reason">${ui.esc(reason)}</span>`,
});
/** A focus change: focus leaves the field with these attributes, for the one with to's attributes (null: outside). */
const leave = (view, attrs, to = null) => dispatchOn(view, 'focusout', attrs, {}, { relatedTarget: to ? fakeTarget(to) : null });
const proxyAttrs = name => ({ 'data-tgj-input': name, 'data-tgj-save': 'proxy' });
const proxyType = (view, name, value) => dispatchOn(view, 'input', proxyAttrs(name), { value });
/** Picking a proxy mode: a browser fires input, then change. */
const proxyMode = (view, value) => {
    dispatchOn(view, 'input', proxyAttrs('proxyMode'), { value });
    dispatchOn(view, 'change', proxyAttrs('proxyMode'), { value });
};
const putProxy = api => api.calls.filter(c => c.type === 'PUT' && c.url === '/jf/TgJelly/Proxy').map(c => JSON.parse(c.data));
/** Holds the reply to every PUT (the call is recorded) until release(), so a test can look at a save in flight. */
function holdPuts(api) {
    const fetch = api.fetch;
    const held = [];
    api.fetch = request => {
        const answer = fetch(request);
        if (request.type !== 'PUT') {
            return answer;
        }

        answer.catch(() => {});
        return new Promise(resolve => held.push(() => resolve(answer)));
    };
    return { release: () => held.splice(0).forEach(go => go()) };
}

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
    proxyType(view, 'proxyUsername', 'bob');
    leave(view, proxyAttrs('proxyUsername'));
    await flush();
    const [first] = putProxy(api);
    assert.deepEqual(first, { ExpectedRevision: 5, Mode: 'Http', Url: 'p:8080', Username: 'bob' });
    assert.ok(!('Password' in first));

    proxyType(view, 'proxyPassword', 'x');
    proxyType(view, 'proxyPassword', '');
    leave(view, proxyAttrs('proxyPassword'));
    await flush();
    const second = putProxy(api).at(-1);
    assert.deepEqual(second, { ExpectedRevision: 6, Mode: 'Http', Url: 'p:8080', Password: '' });
    assert.ok(!('Username' in second), 'the saved form starts untouched again');
    assert.deepEqual(cue(view, 'proxy'), SAVED_CUE);
    assert.ok(!proxyEl(el).includes('tgj-error'));
});

test('proxy 409 retries once on the server data, then says it changed elsewhere and keeps the typed url', async () => {
    const { view, api, hooks, el } = mount({
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'old:1' }),
        'PUT TgJelly/Proxy': { status: 409, body: { Current: settings({ Revision: 9, ProxyMode: 'Http', ProxyUrl: 'theirs:2' }) } },
    });
    await show(view);
    proxyType(view, 'proxyUrl', 'mine:3');
    leave(view, proxyAttrs('proxyUrl'));
    await flush();

    assert.deepEqual(putProxy(api).map(b => [b.ExpectedRevision, b.Url]), [[5, 'mine:3'], [9, 'mine:3']]);
    assert.ok(proxyEl(el).includes('value="mine:3"'));
    assert.deepEqual(cue(view, 'proxy'), errorCue(EN['web.autosave.conflict']));
    assert.equal(hooks.state.settings.Revision, 9);
    assert.equal(hooks.state.forms.proxy.dirty, true);
});

test('proxy 400 shows the field errors, escaped', async () => {
    const { view, el, hooks } = mount({
        'PUT TgJelly/Proxy': { status: 400, body: { Errors: [{ Field: 'Proxy.Url', Message: 'Must be <host:port>' }] } },
    });
    await show(view);
    proxyMode(view, 'Http');
    proxyType(view, 'proxyUrl', 'nonsense');
    leave(view, proxyAttrs('proxyUrl'));
    await flush();
    assert.deepEqual(cue(view, 'proxy'), errorCue('Must be <host:port>'), 'the cue is text: nothing in it is markup');
    await hooks.reloadSection('proxy', { fresh: true });
    assert.ok(proxyEl(el).includes('value="nonsense"'), 'a refresh keeps what was typed');
});

// --- labels (plan 9, task 1; design §3): every state word is an outlined label ---

test('cue_saved_isGoodLabel', async () => {
    const { view, api, timers } = mount({
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'p:8080' }),
        'PUT TgJelly/Proxy': () => settings({ Revision: 6, ProxyMode: 'Http', ProxyUrl: 'p:9090' }),
    });
    await show(view);
    const hold = holdPuts(api);
    proxyType(view, 'proxyUrl', 'p:9090');
    leave(view, proxyAttrs('proxyUrl'));
    await flush();
    await timers.advance(300);
    assert.equal(view.cues.get('proxy').innerHTML, ui.label(EN['web.autosave.saving'], 'progress'), 'Saving… is a progress label');

    hold.release();
    await flush();
    const spot = view.cues.get('proxy');
    assert.equal(spot.className, 'tgj-cue tgj-cue--saved', 'the spot keeps its fade');
    assert.equal(spot.innerHTML, ui.label(EN['web.autosave.saved'], 'good'));
    assert.equal(EN['web.autosave.saved'], 'Saved');
});

test('cue_notSaved_labelPlusReason', async () => {
    const { view } = mount({
        'PUT TgJelly/Proxy': { status: 400, body: { Errors: [{ Field: 'Proxy.Url', Message: 'Must be <host:port>' }] } },
    });
    await show(view);
    proxyMode(view, 'Http');
    proxyType(view, 'proxyUrl', 'nonsense');
    leave(view, proxyAttrs('proxyUrl'));
    await flush();
    const spot = view.cues.get('proxy');
    assert.equal(EN['web.autosave.notSaved'], 'Not saved');
    assert.equal(spot.className, 'tgj-cue tgj-cue--error');
    assert.equal(spot.innerHTML, `${ui.label('Not saved', 'bad')} <span class="tgj-cue-reason">Must be &lt;host:port&gt;</span>`,
        'the state word is a bad label, and the reason follows as plain text');
});

test('header_connected_isGoodLabel', async () => {
    assert.deepEqual(m.connectionLabel(status(), EN), { tone: 'good', text: 'Connected · @jf_bot' });
    assert.deepEqual(m.connectionLabel(status({ ConnectionState: 'Connecting' }), EN), { tone: 'progress', text: EN['web.header.connecting'] });
    const { view, el } = mount();
    await show(view);
    assert.equal(el('tgj-conn').innerHTML, ui.label('Connected · @jf_bot', 'good'));
});

test('templateBadge_edited_isAccentLabel', () => {
    const edited = renderTemplates(templates(), EN, tplUi({ key: 'media.digest' }));
    assert.ok(edited.includes(ui.label(EN['web.templates.edited'], 'accent')), edited);
    const builtIn = renderTemplates(templates(), EN, tplUi());
    assert.ok(builtIn.includes(ui.label(EN['web.templates.builtIn'], 'neutral')), builtIn);
    assert.ok(!/tgj-badge/.test(edited + builtIn), 'one label style, not a parallel badge');
});

// --- fix round 1: secrets stay out of the page and state ---

test('a typed proxy password is never rendered or kept in state, and survives a re-render in the input', async () => {
    const SECRET = 'hunter2-secret';
    const answers = [
        { status: 409, body: { Current: settings({ Revision: 9 }) } },
        { status: 400, body: { Errors: [{ Field: 'Proxy.Url', Message: 'bad' }] } },
    ];
    const { view, api, hooks, el } = mount({ 'PUT TgJelly/Proxy': () => answers.shift() });
    await show(view);
    const field = { name: 'proxyPassword', value: '' };
    view.fields.push(field);
    proxyMode(view, 'Http');
    proxyType(view, 'proxyUrl', 'h:1');
    proxyType(view, 'proxyPassword', SECRET);
    // A re-render empties the input; the controller puts the typed text back.
    field.value = '';
    leave(view, proxyAttrs('proxyPassword'));
    await flush();

    assert.equal(putProxy(api).length, 2, 'the 409 is retried once, and the retry gets the 400');
    assert.equal(field.value, SECRET, 'restored into the input after the re-render');
    assert.ok(!el('tgj-proxy').innerHTML.includes(SECRET));
    assert.ok(!JSON.stringify(hooks.state).includes(SECRET));
    assert.deepEqual(cue(view, 'proxy'), errorCue('bad'));

    assert.ok(!m.renderProxyForm(settings(), EN, { form: { local: { Mode: 'Http', Url: 'x', Password: SECRET }, changed: [] } }).includes(SECRET));
});

test('the typed proxy password is dropped after a successful save', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Proxy': () => settings({ Revision: 6 }) });
    await show(view);
    const field = { name: 'proxyPassword', value: '' };
    view.fields.push(field);
    proxyType(view, 'proxyPassword', 'pw');
    leave(view, proxyAttrs('proxyPassword'));
    await flush();
    assert.equal(field.value, '');
    leave(view, proxyAttrs('proxyPassword'));
    await flush();
    assert.equal(putProxy(api).length, 1, 'leaving again with nothing changed sends nothing');
    proxyType(view, 'proxyUsername', 'u');
    leave(view, proxyAttrs('proxyUsername'));
    await flush();
    const puts = putProxy(api);
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

// --- destinations (spec §6.6.3, §7.2, §7.3, §10.4; plan 8 Task 7: cards, auto-save, the preset confirmation) ---

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
/** A click on a destination switch (a button with role=switch), as the page's switch listener sees it. */
const flip = (view, name, attributes) => {
    dispatchOn(view, 'click', { 'data-tgj-input': name, role: 'switch', ...attributes });
    return flush();
};
const pickPreset = (view, value) => destInput(view, 'destPreset', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Preset' }, { value });
/** Whether the switch with this input and these attributes is on in the markup; null when there is none. */
function switchIn(html, name, attributes = {}) {
    for (const [tag] of html.matchAll(/<button[^>]*role="switch"[^>]*>/g)) {
        if (tag.includes(` data-tgj-input="${name}"`) && Object.entries(attributes).every(([k, v]) => tag.includes(` ${k}="${v}"`))) {
            return /aria-checked="(true|false)"/.exec(tag)[1] === 'true';
        }
    }

    return null;
}
const selectedPreset = html => /<select[^>]*data-tgj-input="destPreset"[^>]*>[\s\S]*?<option value="(\w+)" selected>/.exec(html)?.[1];
// The bot's strings keep their emoji (design §7), so the dashboard must not show them: these too.
const DEST_SYMBOLS = /[●✓✅⚠❌⛔▫🔔🔕]|️/u;

/**
 * A PUT Destinations/d1 that applies the request to the destination, as the server does, and answers with the
 * DestinationDto and the Revision it moved to.
 */
function echoDest(start = 5, base = dest()) {
    let revision = start;
    let item = base;
    return request => {
        const body = JSON.parse(request.data);
        const next = { ...item };
        if (body.Events) {
            Object.assign(next, { Preset: 'Custom', Events: body.Events });
        } else if (body.Preset) {
            Object.assign(next, { Preset: body.Preset, Events: CATALOG.Presets[body.Preset] ?? next.Events });
        }

        for (const key of ['LibraryIds', 'SilentGroups', 'Enabled']) {
            if (key in body) {
                next[key] = body[key];
            }
        }

        item = next;
        revision++;
        return { ...next, Revision: revision };
    };
}

test('destination titles are escaped', async () => {
    const { view, el } = mount({ 'GET TgJelly/Destinations': destinations({ Items: [dest({ Title: '<script>alert(1)</script>' })] }) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(!destEl(el).includes('<script>'));
    assert.ok(destEl(el).includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
});

test('destList_rowsWithStateLabels_noSymbols', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({
            Items: [
                dest(),
                dest({ Id: 'd2', Title: 'Admins', Kind: 'Group', Preset: 'AdminAlerts', Health: { Status: 'NeedsPermissions', PhotosBlocked: true } }),
                dest({ Id: 'd3', Title: 'Topic', Preset: 'Custom', Health: { Status: 'TopicMissing', PhotosBlocked: false } }),
                dest({ Id: 'd4', Title: 'Gone', Kind: 'Channel', Enabled: false, Health: { Status: 'Lost', PhotosBlocked: false } }),
            ],
        }),
    });
    await show(view);
    const html = destEl(el);
    assert.match(html, /<section class="tgj-card"/, 'the list is a card');
    assert.ok(!/emby-|inputContainer|selectContainer|sectionTitle|verticalSection|fieldDescription|tgj-badge/.test(html), 'only the dashboard components');
    assert.ok(!DEST_SYMBOLS.test(html), 'no symbols');

    // Each destination is a row: its title, the kind and preset muted, and the health as a coloured word.
    assert.equal((html.match(/<div class="tgj-row tgj-dest-row">/g) ?? []).length, 4);
    for (const [kind, preset] of [['Supergroup', 'Everything'], ['Group', 'AdminAlerts'], ['Channel', 'Everything']]) {
        assert.ok(html.includes(`<span class="tgj-row-hint">${m.t(EN, 'web.dest.meta', { kind: EN[`dest.kind.${kind}`], preset: EN[`dest.preset.${preset}`] })}</span>`), kind);
    }
    assert.ok(html.includes('<span class="tgj-row-label">Admins</span>'));
    assert.ok(!html.includes(ui.label('OK', 'good')), 'a healthy destination shows no label');
    assert.ok(html.includes(ui.label('Needs permissions', 'bad')));
    assert.ok(html.includes(ui.label('Photos blocked', 'attention')));
    assert.ok(html.includes(ui.label('Topic missing', 'bad')));
    assert.ok(html.includes(ui.label('Lost', 'bad')));
    // The dashboard has its own words; the bot's labels keep their emoji.
    assert.equal(EN['dest.health.Ok'], '✅ OK');
    assert.equal(EN['dest.health.Lost'], '⛔ Lost');
    for (const status of ['Ok', 'PhotosBlocked', 'NeedsPermissions', 'TopicMissing', 'Lost']) {
        assert.ok(!DEST_SYMBOLS.test(EN[`web.dest.health.${status}`]), status);
    }

    // The Sending switch is a switch with its own cue; Edit is an action.
    assert.equal(switchIn(html, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' }), true);
    assert.equal(switchIn(html, 'destEnabled', { 'data-tgj-id': 'd4' }), false);
    assert.ok(html.includes('data-tgj-cue="dest:d1:Enabled"'));
    assert.ok(html.includes(m.t(EN, 'web.dest.enabled')));
    assert.ok(html.includes(ui.actionButton({ label: EN['web.dest.edit'], act: 'edit', id: 'd2' })));
    // The add actions stay.
    assert.ok(html.includes(LINKS.StartAdd));

    const empty = mount({ 'GET TgJelly/Destinations': destinations({ Items: [], ManualUnlocked: true }) });
    await show(empty.view);
    const none = destEl(empty.el);
    assert.match(none, /<div class="tgj-empty"><p class="tgj-empty-text">No destinations yet[^<]*<\/p><div class="tgj-empty-actions">/);
    assert.match(none, /class="tgj-empty-actions">[\s\S]*https:\/\/t\.me\/jf_bot\?start=add[\s\S]*data-tgj-act="open-manual"/, 'the add actions in the empty state');
});

test('eventSwitch_savesImmediately', async () => {
    const { view, api, el, hooks, timers } = mount({ 'PUT TgJelly/Destinations/d1': echoDest() });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.equal(switchIn(destEl(el), 'destEvent', { 'data-tgj-key': 'server.started' }), true);

    await flip(view, 'destEvent', { 'data-tgj-id': 'd1', 'data-tgj-key': 'server.started', 'data-tgj-save': 'dest:d1:event:server.started' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 5, Events: ['media.movie', 'media.episodes', 'security.login_failed'] }], 'sent at once');
    assert.deepEqual(cue(view, 'dest:d1:event:server.started'), SAVED_CUE, 'Saved, beside the switch that changed');
    assert.deepEqual(cue(view, 'dest:d1:event:media.movie'), IDLE_CUE);
    assert.equal(switchIn(destEl(el), 'destEvent', { 'data-tgj-key': 'server.started' }), false);
    assert.equal(selectedPreset(destEl(el)), 'Custom', 'a hand-picked list is Custom');
    assert.equal(hooks.pageRevision(), 6, 'the reply carries the revision');
    assert.equal(hooks.state.destinations.Items[0].Preset, 'Custom');
    assert.equal(hooks.pendingFields.size, 0);

    // Libraries, silent groups and the Sending switch save at once too, each with the revision the last save moved to.
    await flip(view, 'destLibrary', { 'data-tgj-id': 'd1', 'data-tgj-lib': LIB_A, 'data-tgj-save': `dest:d1:lib:${LIB_A}` });
    await flip(view, 'destSilent', { 'data-tgj-id': 'd1', 'data-tgj-group': 'media', 'data-tgj-save': 'dest:d1:silent:media' });
    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });
    assert.deepEqual(putBodies(api).slice(1), [
        { ExpectedRevision: 6, LibraryIds: [LIB_A] },
        { ExpectedRevision: 7, SilentGroups: ['media'] },
        { ExpectedRevision: 8, Enabled: false },
    ]);
    assert.equal(switchIn(destEl(el), 'destLibrary', { 'data-tgj-lib': LIB_A }), true);
    assert.equal(switchIn(destEl(el), 'destSilent', { 'data-tgj-group': 'media' }), true);
    assert.equal(switchIn(destEl(el), 'destEnabled', { 'data-tgj-id': 'd1' }), false);
    assert.deepEqual(cue(view, 'dest:d1:Enabled'), SAVED_CUE);
    await timers.advance(2000);
    assert.deepEqual(cue(view, 'dest:d1:Enabled'), IDLE_CUE);
});

test('presetFromCustom_asksInline_cancelKeepsCustom_replaceSends', async () => {
    const custom = dest({ Preset: 'Custom', Events: ['media.movie'] });
    const { view, api, el, hooks } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [custom] }),
        'PUT TgJelly/Destinations/d1': echoDest(5, custom),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });

    await pickPreset(view, 'NewMedia');
    assert.deepEqual(putBodies(api), [], 'nothing is sent before Replace');
    let html = destEl(el);
    assert.ok(html.includes(ui.inlineConfirm({
        question: EN['web.dest.presetConfirm'], confirmLabel: EN['web.dest.presetReplace'], confirmAct: 'presetReplace',
        cancelAct: 'presetCancel', cancelLabel: EN['dest.btn.cancel'], id: 'd1',
    })), 'the inline confirmation, not a dialog');
    assert.equal(EN['web.dest.presetConfirm'], 'Replace your custom events with this preset?');
    assert.equal(selectedPreset(html), 'Custom', 'the select shows Custom until confirmed');
    assert.deepEqual(hooks.state.forms['dest:d1'].local.Events, ['media.movie']);

    await destAct(view, 'presetCancel', { 'data-tgj-id': 'd1' });
    html = destEl(el);
    assert.ok(!html.includes(EN['web.dest.presetConfirm']));
    assert.equal(selectedPreset(html), 'Custom');
    assert.deepEqual(putBodies(api), []);
    assert.equal(hooks.state.forms['dest:d1'].local.Preset, 'Custom');

    await pickPreset(view, 'NewMedia');
    await destAct(view, 'presetReplace', { 'data-tgj-id': 'd1' });
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 5, Preset: 'NewMedia' }]);
    html = destEl(el);
    assert.ok(!html.includes(EN['web.dest.presetConfirm']));
    assert.equal(selectedPreset(html), 'NewMedia');
    assert.equal(switchIn(html, 'destEvent', { 'data-tgj-key': 'media.episodes' }), true, 'the preset events show');
    assert.deepEqual(cue(view, 'dest:d1:Preset'), SAVED_CUE);
});

test('presetToPreset_savesImmediately', async () => {
    const { view, api, el } = mount({ 'PUT TgJelly/Destinations/d1': echoDest() });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });

    await pickPreset(view, 'NewMedia');
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 5, Preset: 'NewMedia' }], 'between presets: at once');
    assert.ok(!destEl(el).includes(EN['web.dest.presetConfirm']));
    assert.equal(switchIn(destEl(el), 'destEvent', { 'data-tgj-key': 'server.started' }), false);
    assert.deepEqual(cue(view, 'dest:d1:Preset'), SAVED_CUE);

    await pickPreset(view, 'Custom');
    assert.deepEqual(putBodies(api)[1], { ExpectedRevision: 6, Preset: 'Custom' }, 'into Custom: at once, keeping the events');
    assert.equal(switchIn(destEl(el), 'destEvent', { 'data-tgj-key': 'media.movie' }), true);

    await pickPreset(view, 'AdminAlerts');
    assert.equal(putBodies(api).length, 2, 'out of Custom asks first');
    assert.ok(destEl(el).includes(EN['web.dest.presetConfirm']));

    // An input event before the change is not a second choice.
    await destInput(view, 'destPreset', { 'data-tgj-id': 'd1' }, { value: 'Everything' }, 'input');
    assert.equal(putBodies(api).length, 2);
});

test('destConflict_retries', async () => {
    const theirs = dest({ SilentGroups: ['security'] });
    const answers = [{ status: 409, body: { Current: destinations({ Revision: 9, Items: [theirs] }) } }];
    const echo = echoDest(9, theirs);
    const { view, api, el, hooks } = mount({ 'PUT TgJelly/Destinations/d1': request => answers.shift() ?? echo(request) });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await flip(view, 'destEvent', { 'data-tgj-id': 'd1', 'data-tgj-key': 'server.started', 'data-tgj-save': 'dest:d1:event:server.started' });

    const puts = putBodies(api);
    assert.deepEqual(puts.map(b => b.ExpectedRevision), [5, 9], 'retried once, with the server revision');
    assert.deepEqual(puts[1].Events, ['media.movie', 'media.episodes', 'security.login_failed'], 'the admin change is re-applied');
    assert.deepEqual(cue(view, 'dest:d1:event:server.started'), SAVED_CUE);
    assert.equal(hooks.pageRevision(), 10);
    assert.equal(switchIn(destEl(el), 'destSilent', { 'data-tgj-group': 'security' }), true, 'the change made elsewhere shows');
    assert.ok(!destEl(el).includes(EN['web.dest.conflictPlain']), 'no section error line');

    // Changed elsewhere twice in a row: stop, and say so beside the switch, which keeps the admin choice.
    const always = mount({ 'PUT TgJelly/Destinations/d1': { status: 409, body: { Current: destinations({ Revision: 9, Items: [theirs] }) } } });
    await show(always.view);
    await destAct(always.view, 'edit', { 'data-tgj-id': 'd1' });
    await flip(always.view, 'destEvent', { 'data-tgj-id': 'd1', 'data-tgj-key': 'server.started', 'data-tgj-save': 'dest:d1:event:server.started' });
    assert.equal(putBodies(always.api).length, 2);
    assert.deepEqual(cue(always.view, 'dest:d1:event:server.started'), errorCue('changed elsewhere, check the value'));
    assert.equal(switchIn(destEl(always.el), 'destEvent', { 'data-tgj-key': 'server.started' }), false);
    assert.ok(!always.hooks.state.forms['dest:d1'].local.Events.includes('server.started'));
});

test('noSaveButton_inEditor', async () => {
    const ghost = 'cccccccccccccccccccccccccccccccc';
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A, ghost], RemovedLibraryIds: [ghost] })] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    const html = destEl(el);
    assert.equal((html.match(/<section class="tgj-card/g) ?? []).length, 2, 'the list and the editor are cards');
    assert.ok(!/saveDest|\bSave\b/.test(html), 'no Save button');
    for (const key of ['web.dest.save', 'web.dest.conflict']) {
        assert.ok(!(key in EN), `${key} is gone`);
    }
    assert.ok(!/emby-|inputContainer|selectContainer|sectionTitle|verticalSection|fieldDescription|tgj-changed|type="checkbox"/.test(html), 'only the dashboard components');
    assert.ok(!DEST_SYMBOLS.test(html));

    // Every auto-saved control names its save, has a cue beside it, and can be found again after a render.
    const controls = html.match(/<select\b[^>]*>|<button[^>]*role="switch"[^>]*>/g);
    assert.equal(controls.length, 1 + 1 + KEYS.length + LIBRARIES.length + CATALOG.Groups.length, 'Sending, preset, events, libraries, silent groups');
    const keys = new Set();
    for (const control of controls) {
        const save = /data-tgj-save="([^"]+)"/.exec(control)?.[1];
        assert.ok(save && /data-tgj-input="[^"]+"/.test(control), control);
        assert.ok(html.includes(`data-tgj-cue="${save}"`), save);
        keys.add(save);
        const attrs = Object.fromEntries([...control.matchAll(/ ([\w-]+)="([^"]*)"/g)].map(([, k, v]) => [k, v]));
        assert.ok(m.fieldSelector({ getAttribute: name => attrs[name] ?? null }).includes('data-tgj-input'), control);
    }
    assert.equal(keys.size, controls.length, 'one save and cue per control');

    // The actions stay buttons: Send test, Check again, Remove (danger), and removing a leftover library.
    for (const act of ['testDest', 'checkDest', 'removeLibrary']) {
        assert.ok(html.includes(`data-tgj-act="${act}"`), act);
    }
    assert.ok(html.includes(ui.actionButton({ label: EN['dest.btn.remove'], act: 'remove', kind: 'danger', id: 'd1' })));
});

test('the editor lists events by group, libraries and silent groups', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A], SilentGroups: ['media'] })] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    const html = destEl(el);
    assert.ok(html.includes(m.t(EN, 'web.event.security.login_failed')));
    assert.ok(html.includes(m.t(EN, 'groups.security')));
    assert.ok(html.includes(m.t(EN, 'dest.preset.AdminAlerts')));
    assert.ok(html.includes('Shows &lt;i&gt;'));
    assert.equal(switchIn(html, 'destLibrary', { 'data-tgj-lib': LIB_A }), true);
    assert.equal(switchIn(html, 'destLibrary', { 'data-tgj-lib': LIB_B }), false);
    assert.equal(switchIn(html, 'destSilent', { 'data-tgj-group': 'media' }), true);
    assert.ok(html.includes(m.t(EN, 'dest.libraries.hint')));
    assert.ok(!html.includes(m.t(EN, 'dest.libraries.all')));
});

test('health_ok_rendersNothing', () => {
    const row = m.renderDestinations(destinations(), LIBRARIES, LINKS, EN, { editing: null });
    assert.ok(!row.includes('tgj-label'), 'no label in the row');
    assert.ok(!row.includes('OK'));
    const editor = m.renderDestinations(destinations(), LIBRARIES, LINKS, EN, { editing: 'd1' });
    assert.ok(!editor.includes('tgj-label'), 'no label in the editor');
    assert.ok(!editor.includes('>OK<'));
});

test('health_lost_isBadLabel', () => {
    const lost = dest({ Health: { Status: 'Lost', PhotosBlocked: false, LastError: null, At: null } });
    const html = m.renderDestinations(destinations({ Items: [lost] }), LIBRARIES, LINKS, EN, { editing: null });
    assert.ok(html.includes(ui.label('Lost', 'bad')));
});

test('editor_eventsGroupedInThreeCards', () => {
    const html = m.renderDestinations(destinations(), LIBRARIES, LINKS, EN, { editing: 'd1' });
    const cards = html.split('<div class="tgj-subcard"').slice(1);
    assert.equal(cards.length, 3);
    const titles = cards.map(c => c.match(/<h3 class="tgj-subcard-title">([^<]*)<\/h3>/)[1]);
    assert.deepEqual(titles, ['Media', 'Security', 'Server']);
    assert.ok(cards[0].includes('data-tgj-key="media.movie"') && cards[0].includes('data-tgj-key="media.episodes"'));
    assert.ok(cards[1].includes('data-tgj-key="security.login_failed"'));
    assert.ok(cards[2].includes('data-tgj-key="server.started"'));
    assert.ok(!cards[0].includes('security.login_failed'));
});

test('editor_eventLabels_haveNoGroupPrefix', () => {
    const html = m.renderDestinations(destinations(), LIBRARIES, LINKS, EN, { editing: 'd1' });
    const labels = [...html.matchAll(/<span class="tgj-row-label">([^<]*)<\/span>/g)].map(x => x[1]);
    for (const k of KEYS) {
        assert.ok(labels.includes(EN[`web.event.${k}`]), k);
    }
    assert.ok(labels.length > 0 && !labels.some(l => /^(Media|Security|Server):/.test(l)));
    for (const k of Object.keys(EN).filter(k => k.startsWith('dest.event.'))) {
        assert.ok(EN[`web.event.${k.slice('dest.event.'.length)}`], k);
    }
});

test('botEventStrings_unchanged', () => {
    assert.equal(EN['dest.event.media.movie'], 'Media: Movies');
    assert.equal(EN['dest.event.server.started'], 'Server: Started');
});

test('the all-libraries line follows the library switches', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [LIB_A] })] }),
        'PUT TgJelly/Destinations/d1': echoDest(5, dest({ LibraryIds: [LIB_A] })),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await flip(view, 'destLibrary', { 'data-tgj-id': 'd1', 'data-tgj-lib': LIB_A, 'data-tgj-save': `dest:d1:lib:${LIB_A}` });
    assert.ok(destEl(el).includes(m.t(EN, 'dest.libraries.all')));
    assert.ok(!destEl(el).includes(m.t(EN, 'dest.libraries.hint')));
});

test('a removed library is listed, and removing it saves at once', async () => {
    const ghost = 'cccccccccccccccccccccccccccccccc';
    const start = dest({ LibraryIds: [LIB_A, ghost], RemovedLibraryIds: [ghost], LibraryFilterBroken: false });
    const { view, api, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [start] }),
        'PUT TgJelly/Destinations/d1': echoDest(5, { ...start, RemovedLibraryIds: [] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes(m.t(EN, 'dest.libraries.removed', { id: ghost })));
    await destAct(view, 'removeLibrary', { 'data-tgj-id': 'd1', 'data-tgj-lib': ghost });
    assert.ok(!destEl(el).includes(ghost));
    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 5, LibraryIds: [LIB_A] }]);
    assert.deepEqual(cue(view, 'dest:d1:libraries'), SAVED_CUE);
});

test('a broken library filter says so', async () => {
    const ghost = 'cccccccccccccccccccccccccccccccc';
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest({ LibraryIds: [ghost], RemovedLibraryIds: [ghost], LibraryFilterBroken: true })] }),
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes(ui.notice({ kind: 'error', html: m.t(EN, 'web.dest.librariesRemoved') })));
});

test('remove needs a confirm and sends the leave flag', async () => {
    const { view, api, el } = mount({ 'DELETE TgJelly/Destinations/d1': { status: 204 } });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(view, 'remove', { 'data-tgj-id': 'd1' });
    assert.equal(api.calls.filter(c => c.type === 'DELETE').length, 0, 'the first click only asks');
    assert.equal(switchIn(destEl(el), 'destLeave'), false);
    assert.ok(destEl(el).includes(ui.inlineConfirm({
        question: m.text(EN, 'web.dest.confirmRemove', { title: 'Family' }), confirmLabel: EN['dest.btn.remove'], confirmAct: 'removeYes',
        cancelAct: 'removeNo', cancelLabel: EN['dest.btn.cancel'], id: 'd1',
    })));

    await destAct(view, 'removeNo', { 'data-tgj-id': 'd1' });
    assert.equal(switchIn(destEl(el), 'destLeave'), null);

    await destAct(view, 'remove', { 'data-tgj-id': 'd1' });
    await flip(view, 'destLeave', { 'data-tgj-id': 'd1' });
    assert.equal(switchIn(destEl(el), 'destLeave'), true);
    assert.equal(putBodies(api).length, 0, 'the leave switch is part of the question, not a setting');
    await destAct(view, 'removeYes', { 'data-tgj-id': 'd1' });
    const deletes = api.calls.filter(c => c.type === 'DELETE');
    assert.equal(deletes.length, 1);
    assert.equal(deletes[0].url, '/jf/TgJelly/Destinations/d1?leave=true&expectedRevision=5');
});

test('send test and check again show their outcome, in words', async () => {
    const { view, api, el } = mount({
        'POST TgJelly/Destinations/d1/Test': { Sent: false, Error: 'bot <was> kicked', Health: { Status: 'Lost' } },
        'POST TgJelly/Destinations/d1/Check': { Health: { Status: 'NeedsPermissions' }, Error: null },
    });
    await show(view);
    await destAct(view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(view, 'testDest', { 'data-tgj-id': 'd1' });
    assert.ok(destEl(el).includes('bot &lt;was&gt; kicked'));
    await destAct(view, 'checkDest', { 'data-tgj-id': 'd1' });
    assert.equal(api.calls.filter(c => c.type === 'POST' && c.url.endsWith('/Check')).length, 1);
    assert.ok(destEl(el).includes(EN['web.dest.health.NeedsPermissions']));
    assert.ok(!DEST_SYMBOLS.test(destEl(el)));
});

test('a destination save, then a settings save at once: the settings PUT sends the revision the reply carries, no 409', async () => {
    const server = oneRevisionServer();
    const { view, api, hooks } = mount(server.routes);
    await show(view);
    dispatchOn(view, 'click', { 'data-tgj-input': 'destEnabled', role: 'switch', 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });
    toggle(view, 'ShowPoster');
    await flush();

    assert.deepEqual(api.calls.filter(c => c.type === 'PUT').map(c => [c.url.replace('/jf/TgJelly/', ''), JSON.parse(c.data).ExpectedRevision]),
        [['Destinations/d1', 5], ['Settings', 6]]);
    assert.equal(server.conflicts, 0, 'no 409');
    assert.equal(hooks.pageRevision(), 7);
    assert.deepEqual(cue(view, 'dest:d1:Enabled'), SAVED_CUE);
    assert.deepEqual(cue(view, toggleCue('ShowPoster')), SAVED_CUE);
});

test('the set up manually link only shows when ManualUnlocked', async () => {
    const closed = mount();
    await show(closed.view);
    assert.ok(!destEl(closed.el).includes('open-manual'));
    assert.ok(destEl(closed.el).includes(LINKS.StartAdd));
    assert.ok(destEl(closed.el).includes('target="_blank"'));

    const open = mount({ 'GET TgJelly/Destinations': destinations({ ManualUnlocked: true }) });
    await show(open.view);
    // Jellyfin's dashboard routes on the hash, so the control must not carry an href="#…" (final review M1).
    assert.ok(destEl(open.el).includes(ui.actionButton({ label: EN['web.dest.manualLink'], act: 'open-manual' })));
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
        { ExpectedRevision: 5, ChatId: -100123456, Preset: 'NewMedia' },
        { ExpectedRevision: 5, TopicLink: 'https://t.me/c/123456/7', Preset: 'NewMedia' },
        { ExpectedRevision: 5, TopicLink: 'https://t.me/club/9', Preset: 'NewMedia' },
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
    assert.deepEqual(JSON.parse(calls[0].data), { ExpectedRevision: 5, Preset: 'AdminAlerts' });
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
/** A movie message switch, or Announce upgrades (one setting for every media message), as the Messages section renders it. */
const toggleAttrs = key => (key === 'AnnounceUpgrades'
    ? { 'data-tgj-input': 'msgAnnounce', 'data-tgj-field': 'Message.AnnounceUpgrades', 'data-tgj-save': 'Message.AnnounceUpgrades', role: 'switch' }
    : { 'data-tgj-input': 'msgSwitch', 'data-tgj-id': 'media.movie', 'data-tgj-key': key, 'data-tgj-field': `Message.Switches.media.movie.${key}`, 'data-tgj-save': `msg:media.movie:${key}`, role: 'switch' });
/** The cue key of toggleAttrs's switch. */
const toggleCue = key => (key === 'AnnounceUpgrades' ? 'Message.AnnounceUpgrades' : `msg:media.movie:${key}`);
/** A click on a message switch (a button with role=switch). */
const toggle = (view, key) => dispatchOn(view, 'click', toggleAttrs(key));
const batchAttrs = key => ({ 'data-tgj-input': 'batching', 'data-tgj-key': key, 'data-tgj-field': `Batching.${key}`, 'data-tgj-save': `Batching.${key}` });
const batchInput = (view, key, value) => dispatchOn(view, 'input', batchAttrs(key), { value });
const URL_ATTRS = { 'data-tgj-input': 'publicUrl', 'data-tgj-field': 'PublicUrl', 'data-tgj-save': 'PublicUrl' };
const urlInput = (view, value) => dispatchOn(view, 'input', URL_ATTRS, { value });

/** PUT Settings that saves what it is sent and moves the revision on, like the server. */
function echoSettings(start = 5, startMessage = messageDto()) {
    let revision = start;
    let message = startMessage;
    return request => {
        const body = JSON.parse(request.data);
        revision++;
        message = mergeMessage(message, body.Message);
        return settings({ Revision: revision, PublicUrl: body.PublicUrl, Message: message, Batching: body.Batching, UpdateCheck: body.UpdateCheck });
    };
}

test('use suggested fills the field and saves it', async () => {
    const { view, el, api } = mount({
        'GET TgJelly/Settings': settings({ PublicUrl: 'http://10.0.0.5:8096', PublicUrlValid: false, PublicUrlSuggestion: 'https://jf.example.com' }),
        'PUT TgJelly/Settings': echoSettings(),
    });
    await show(view);
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes('https://jf.example.com'));
    assert.ok(html.includes(m.t(EN, 'web.messages.verify')));
    await click(view, 'useSuggested');
    assert.match(el('tgj-messages').innerHTML, /data-tgj-field="PublicUrl"[^>]*value="https:\/\/jf\.example\.com"|value="https:\/\/jf\.example\.com"[^>]*data-tgj-field="PublicUrl"/);
    assert.deepEqual(putSettings(api).map(b => b.PublicUrl), ['https://jf.example.com']);
    assert.deepEqual(cue(view, 'PublicUrl'), SAVED_CUE);
});

test('invalid url explains', async () => {
    const { view, el } = mount({ 'GET TgJelly/Settings': settings({ PublicUrl: 'http://localhost', PublicUrlValid: false }) });
    await show(view);
    assert.ok(el('tgj-messages').innerHTML.includes(m.t(EN, 'web.messages.urlInvalid').replaceAll('&#39;', "'")));
    const ok = mount({ 'GET TgJelly/Settings': settings({ PublicUrl: 'https://jf.example.com', PublicUrlValid: true }) });
    await show(ok.view);
    assert.ok(!ok.el('tgj-messages').innerHTML.includes(m.t(EN, 'web.messages.urlInvalid').replaceAll('&#39;', "'")));
});

test('each message toggle is a switch with its own cue, and a save sends only its own field', async () => {
    const { view, api, el, hooks } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    const html = el('tgj-messages').innerHTML;
    for (const key of ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks', 'AnnounceUpgrades']) {
        assert.ok(html.includes(`data-tgj-save="${toggleCue(key)}"`), key);
        assert.ok(html.includes(`data-tgj-cue="${toggleCue(key)}"`), key);
    }
    toggle(view, 'ShowProviderLinks');
    toggle(view, 'ShowPoster');
    toggle(view, 'AnnounceUpgrades');
    await flush();
    const [first, second, third] = putSettings(api);
    // A switch belongs to the message shown (the movie, the first media message), and only that switch is sent.
    assert.equal(first.ExpectedRevision, 5);
    assert.deepEqual(first.Message, { PerMessage: [{ EventKey: 'media.movie', Switches: { ShowProviderLinks: true } }] });
    assert.equal(second.ExpectedRevision, 6);
    assert.deepEqual(second.Message, { PerMessage: [{ EventKey: 'media.movie', Switches: { ShowPoster: false } }] }, 'the second toggle is its own save');
    assert.deepEqual(third.Message, { PerMessage: [], AnnounceUpgrades: true });
    assert.ok(el('tgj-messages').innerHTML.includes('data-tgj-key="ShowProviderLinks"'));
    assert.equal(hooks.state.forms.settings.base.Message.Switches.media.movie.ShowProviderLinks, true, 'the saved value shows');
    assert.equal(hooks.state.forms.settings.base.Message.Switches.media.movie.ShowPoster, false);
    assert.equal(second.PublicUrl, '');
    assert.deepEqual(second.UpdateCheck, { Enabled: true, Fixes: true, NewReleases: true, NewMajor: false });
    assert.equal(second.Batching.QuietWindowSeconds, 120);
});

test('a server 400 shows its message in the cue beside the field, and the field keeps the value', async () => {
    const { view, el } = mount({
        'PUT TgJelly/Settings': { status: 400, body: { Errors: [{ Field: 'Batching.QuietWindowSeconds', Message: 'Must be 5 to <3600>.' }] } },
    });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '30');
    leave(view, batchAttrs('QuietWindowSeconds'));
    await flush();
    const html = el('tgj-batching').innerHTML;
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), errorCue('Must be 5 to <3600>.'));
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), IDLE_CUE);
    assert.ok(html.includes('min="5"') && html.includes('max="3600"'));
    assert.ok(html.includes('value="30"'));
});

test('a second settings 409 says the value changed elsewhere and keeps the typed batching value', async () => {
    const fresh = settings({ Revision: 9, Batching: { QuietWindowSeconds: 200, MaxHoldMinutes: 45, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 } });
    const { view, api, el, hooks } = mount({ 'PUT TgJelly/Settings': { status: 409, body: { Current: fresh } } });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '300');
    leave(view, batchAttrs('QuietWindowSeconds'));
    await flush();
    const puts = putSettings(api);
    assert.deepEqual(puts.map(b => [b.ExpectedRevision, b.Batching.QuietWindowSeconds]), [[5, 300], [9, 300]]);
    assert.equal(puts[1].Batching.MaxHoldMinutes, 45, 'the retry is built on the server data');
    const html = el('tgj-batching').innerHTML;
    assert.ok(html.includes('value="300"'));
    assert.ok(html.includes('value="45"'), 'the server change in an untouched field shows');
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), errorCue(EN['web.autosave.conflict']));
    assert.equal(hooks.state.settings.Revision, 9);
    assert.equal(hooks.state.forms.settings.dirty, true);
});

test('a toggle save sends the saved batching values, and a batching edit still waiting stays dirty', async () => {
    const saved = settings({ Revision: 6, Message: messageDto({ ...FLAT_MESSAGE, ShowProviderLinks: true }) });
    const { view, api, hooks } = mount({ 'PUT TgJelly/Settings': saved });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '9999');
    toggle(view, 'ShowProviderLinks');
    await flush();
    const [body] = putSettings(api);
    assert.equal(body.Batching.QuietWindowSeconds, 120);
    assert.equal(body.Message.PerMessage[0].Switches.ShowProviderLinks, true);
    assert.equal(hooks.state.forms.settings.local.Batching.QuietWindowSeconds, 9999);
    assert.equal(hooks.state.forms.settings.dirty, true);
    assert.equal(hooks.state.forms.settings.base.Message.Switches.media.movie.ShowProviderLinks, true);
});

test('a server error on another field shows in the cue of the field that was saved', async () => {
    const { view } = mount({
        'PUT TgJelly/Settings': { status: 400, body: { Errors: [{ Field: 'Batching.MaxHoldMinutes', Message: 'Too <long>.' }] } },
    });
    await show(view);
    batchInput(view, 'QuietWindowSeconds', '600');
    leave(view, batchAttrs('QuietWindowSeconds'));
    await flush();
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), errorCue('Too <long>.'));
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
    leave(view, batchAttrs('QuietWindowSeconds'));
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
    proxyMode(view, 'Http');
    proxyType(view, 'proxyUrl', 'h:1');
    leave(view, proxyAttrs('proxyUrl'));
    await flush();
    assert.equal(hooks.state.settings.Revision, 6);
    assert.equal(hooks.state.forms.settings.local.Batching.DigestThreshold, 20);
    assert.ok(el('tgj-batching').innerHTML.includes('value="20"'));
});

test('the update switch and the mode select each save at once', async () => {
    const { view, api, el } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    assert.match(el('tgj-updates').innerHTML, /<option value="SameMajor" selected>/);
    assert.match(el('tgj-updates').innerHTML, /role="switch"[^>]*aria-checked="true"[^>]*data-tgj-input="updEnabled"/);
    dispatchOn(view, 'click', { 'data-tgj-input': 'updEnabled', 'data-tgj-field': 'UpdateCheck.Enabled', role: 'switch' });
    await flush();
    // A select fires input, then change: one save.
    dispatchOn(view, 'input', { 'data-tgj-input': 'updMode', 'data-tgj-field': 'UpdateCheck.Mode' }, { value: 'AnyNewer' });
    dispatchOn(view, 'change', { 'data-tgj-input': 'updMode', 'data-tgj-field': 'UpdateCheck.Mode' }, { value: 'AnyNewer' });
    await flush();
    assert.deepEqual(putSettings(api).map(b => b.UpdateCheck), [{ Enabled: false, Fixes: true, NewReleases: true, NewMajor: false }, { Enabled: false, Fixes: true, NewReleases: true, NewMajor: true }]);
    assert.deepEqual(cue(view, 'UpdateCheck.Enabled'), SAVED_CUE);
    assert.deepEqual(cue(view, 'UpdateCheck.Mode'), SAVED_CUE);
});

test('a non-number batching value is not sent', async () => {
    const { view, api, timers } = mount();
    await show(view);
    batchInput(view, 'MaxHoldMinutes', 'abc');
    await timers.advance(800);
    assert.equal(putSettings(api).length, 0);
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), errorCue(EN['web.settings.notNumber']));
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
        Revision: 5,
        Items: [
            {
                EventKey: 'media.movie', Label: 'Movie added', Media: true, Text: '<b>{{ title }}</b>', BuiltIn: '<b>{{ title }}</b>', Edited: false,
                SampleFull: {}, SampleSparse: {}, Flag: null, ApplicableSwitches: ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks'],
                Fields: [{ Name: 'title', Type: 'string', Description: 'The <title>.', Optional: false }, { Name: 'year', Type: 'number', Description: 'The year.', Optional: true }],
            },
            {
                EventKey: 'media.digest', Label: 'Digest', Media: false, Text: 'Hi {{ title }}', BuiltIn: 'Digest {{ count }}', Edited: true,
                SampleFull: {}, SampleSparse: {}, ApplicableSwitches: [],
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
const tplUi = (overrides = {}) => ({ key: 'media.movie', forms: {}, result: null, good: null, destinations: [{ Id: 'd1', Title: 'Family' }], ...overrides });
/** The Messages section with only the templates loaded: the pickers, the template card and the preview (plan 9 Task 8). */
const renderTemplates = (dto, S, ui) => msg.renderMessagesSection({ templates: dto, settings: null, forms: ui.forms ?? {} }, S, ui);
/** The template textarea's attributes, as renderTemplates writes them for an event key. */
const tplAttrs = (key = 'media.movie') => ({ 'data-tgj-input': 'tplText', 'data-tgj-key': key, 'data-tgj-save': `template:${key}` });
/** Typing in the template textarea. */
const tplType = (view, value, key) => dispatchOn(view, 'input', tplAttrs(key), { value });

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
    const html = renderTemplates(templates(), EN, tplUi());
    assert.ok(html.includes(ui.label(EN['web.templates.builtIn'], 'neutral')), html);
    assert.ok(!html.includes(ui.label(EN['web.templates.edited'], 'accent')));
    assert.ok(!html.includes('data-tgj-act="tplReset"'));
    assert.ok(html.includes(`<h2 class="tgj-card-title">${m.t(EN, 'web.templates.title')}</h2>`));
    // The Message picker lists the section's messages (messages_pickerOptions_haveNoEditedSuffix checks the options).
    assert.ok(html.includes(`<option value="media.movie" selected>${EN['web.event.media.movie']}</option>`), html);
});

test('renderTemplates_editedBadge_resetAsksInline', () => {
    const html = renderTemplates(templates(), EN, tplUi({ key: 'media.digest' }));
    assert.ok(html.includes(ui.label(EN['web.templates.edited'], 'accent')), html);
    assert.ok(html.includes('data-tgj-act="tplReset"'));
    assert.ok(!html.includes('data-tgj-act="tplResetConfirm"'));

    const asking = renderTemplates(templates(), EN, tplUi({ key: 'media.digest', resetAsk: true }));
    assert.ok(asking.includes(m.t(EN, 'web.templates.resetAsk')));
    assert.ok(asking.includes('data-tgj-act="tplResetConfirm"') && asking.includes('data-tgj-act="tplResetCancel"'));
    assert.ok(!asking.includes('data-tgj-act="tplReset"'));
});

test('renderTemplates_noEnabledCheckbox', () => {
    const html = renderTemplates(templates(), EN, tplUi());
    assert.ok(!html.includes('type="checkbox"'));
    assert.ok(!html.includes('tplEnabled') && !html.includes('tplSample') && !html.includes('tplValidate'));
    assert.ok(!('web.templates.enabled' in EN) && !('web.templates.digestNote' in EN));
});

test('renderTemplates_gridHasPreviewPane', () => {
    const good = validation({ Html: '<b>Dune</b>' });
    const html = renderTemplates(templates(), EN, tplUi({ result: good, good }));
    const grid = html.indexOf('<div class="tgj-msg-layout">');
    const textarea = html.indexOf('<textarea');
    const fields = html.indexOf('class="tgj-tpl-tables"');
    const pane = html.indexOf('<div id="tgj-tpl-preview"');
    assert.ok(grid >= 0 && grid < textarea && textarea < fields && fields < pane, html);
    assert.ok(html.includes('>&lt;b&gt;{{ title }}&lt;/b&gt;</textarea>'));
    assert.ok(html.slice(pane).includes('<b>Dune</b>'));
    assert.ok(html.includes(m.t(EN, 'web.templates.testNote')));
    assert.ok(html.includes('<option value="d1" selected>Family</option>'), html);
    assert.equal(m.t(EN, 'web.templates.testNote'), 'The test message goes to that chat for real, and its members see it.');
});

test('renderTemplatePreview_posterPlaceholderAndButtons', () => {
    const result = validation({
        Html: 'Dune <i>2021</i>', Length: 9, Limit: 1024,
        Buttons: [[{ Text: 'Open <x>', Url: 'https://jf.example/web/' }], [{ Text: 'IMDb', Url: 'javascript:alert(1)' }, { Text: 'TMDB', Url: 'https://tmdb.example' }]],
    });
    const html = m.renderTemplatePreview(result, EN);
    assert.ok(html.includes('class="tgj-tg-poster"'), html);
    assert.ok(html.includes('Dune <i>2021</i>'));
    assert.equal((html.match(/class="tgj-tg-row"/g) ?? []).length, 2);
    assert.ok(html.includes('href="https://jf.example/web/"') && html.includes('Open &lt;x&gt;'));
    assert.ok(!html.includes('javascript:'));
    assert.ok(html.includes('IMDb'));
    // The length sits under the template (Task 8 fix round 1), not in the preview.
    assert.ok(!html.includes('9 / 1,024'));
    assert.ok(m.renderTemplateCheck(result, EN, 'en').includes('9 / 1,024'));

    const plain = m.renderTemplatePreview(validation({ Poster: false, Html: 'x' }), EN);
    assert.ok(!plain.includes('tgj-tg-poster'));
    assert.equal(m.renderTemplatePreview(null, EN), '');
    assert.equal(m.renderTemplateCheck(null, EN, 'en'), '');
});

test('renderTemplatePreview_errorsDimLastGood', () => {
    const good = validation({ Html: '<b>last good</b>' });
    const bad = validation({ Errors: [{ Message: 'Unexpected <end>', Line: 2, Column: 7 }, { Message: 'No position' }], Warnings: ['careful & slow'], Html: '', Length: 0 });
    const html = m.renderTemplatePreview(bad, EN, good);
    const check = m.renderTemplateCheck(bad, EN, 'en');
    assert.ok(check.includes('line 2, column 7: Unexpected &lt;end&gt;'), check);
    assert.ok(check.includes('No position') && check.includes('careful &amp; slow'));
    assert.ok(/class="tgj-tg-bubble[^"]*tgj-tg-stale/.test(html), html);
    assert.ok(html.includes('<b>last good</b>'));

    const fresh = m.renderTemplatePreview(good, EN, good);
    assert.ok(!fresh.includes('tgj-tg-stale'));
});

test('sendTest_disabledWithErrors', () => {
    const testButton = html => /<button[^>]*data-tgj-act="tplTest"[^>]*>/.exec(html)[0];
    const bad = validation({ Errors: [{ Message: 'x' }] });
    assert.ok(testButton(renderTemplates(templates(), EN, tplUi({ result: bad }))).includes(' disabled'));
    assert.ok(!testButton(renderTemplates(templates(), EN, tplUi({ result: validation() }))).includes(' disabled'));
    assert.ok(testButton(renderTemplates(templates(), EN, tplUi({ result: validation(), testing: true }))).includes(' disabled'));
    const none = renderTemplates(templates(), EN, tplUi({ result: validation(), destinations: [] }));
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
    assert.deepEqual(posted(api, 'Templates/Validate'), [{ EventKey: 'media.movie', Text: '<b>{{ title }}</b>' }]);
    assert.ok(el('tgj-tpl-preview').innerHTML.includes('<b>Dune</b>'));
});

test('preview_ignoresStaleResponse', async () => {
    const { view, api, el, timers } = mount({ 'GET TgJelly/Templates': templates() });
    const held = holdValidate(api);
    await show(view);
    assert.equal(held.length, 1);

    tplType(view, 'newer {{ title }}');
    await flush();
    assert.deepEqual(timers.timeouts(), [500, 1000], 'the preview, then the save');
    await timers.advance(500);
    assert.equal(held.length, 2);
    assert.deepEqual(held[1].body, { EventKey: 'media.movie', Text: 'newer {{ title }}' });

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
    const before = el('tgj-messages').writes;
    const previewBefore = el('tgj-tpl-preview').writes;
    tplType(view, 'abc');
    tplType(view, 'abcd');
    await flush();
    assert.deepEqual(timers.timeouts(), [500, 1000], 'one preview debounce and one save debounce, each restarted');
    await timers.advance(500);
    assert.equal(el('tgj-messages').writes, before, 'the section, and so the textarea, is not written again');
    assert.equal(el('tgj-tpl-preview').writes, previewBefore + 1, 'only the preview pane is replaced');
    assert.ok(el('tgj-tpl-preview').innerHTML.includes('typed'));
});

test('a validate failure shows in the preview and keeps the last good preview, dimmed', async () => {
    let reply = validation({ Html: 'good one' });
    const { view, el, timers } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': () => reply });
    await show(view);
    reply = validation({ Errors: [{ Message: 'Unexpected end', Line: 1, Column: 3 }], Html: '' });
    tplType(view, '{{');
    await timers.advance(500);
    const pane = el('tgj-tpl-preview').innerHTML;
    assert.ok(el('tgj-tpl-check').innerHTML.includes('line 1, column 3: Unexpected end'), 'the error, under the template');
    assert.ok(pane.includes('tgj-tg-stale') && pane.includes('good one'));
});

test('switching events keeps unsaved text, validates at once and drops the pending debounce', async () => {
    const { view, el, api, timers } = mount({ 'GET TgJelly/Templates': templates(), 'POST TgJelly/Templates/Validate': validation() });
    await show(view);
    tplType(view, 'draft one');
    tplInput(view, 'msgKey', { value: 'media.digest' }, 'change');
    await flush();
    assert.deepEqual(timers.timeouts(), [1000], 'the preview debounce is dropped; the save of the draft still waits');
    assert.ok(el('tgj-messages').innerHTML.includes('>Hi {{ title }}</textarea>'));
    assert.deepEqual(posted(api, 'Templates/Validate').at(-1), { EventKey: 'media.digest', Text: 'Hi {{ title }}' });
    tplInput(view, 'msgKey', { value: 'media.movie' }, 'change');
    await flush();
    assert.ok(el('tgj-messages').innerHTML.includes('>draft one</textarea>'));
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
    tplInput(view, 'msgKey', { value: 'media.digest' }, 'change');
    await flush();
    await click(view, 'tplReset');
    assert.ok(el('tgj-messages').innerHTML.includes(m.t(EN, 'web.templates.resetAsk')));
    await click(view, 'tplResetCancel');
    assert.ok(!el('tgj-messages').innerHTML.includes(m.t(EN, 'web.templates.resetAsk')));
    assert.ok(!api.calls.some(c => c.type === 'DELETE'));

    await click(view, 'tplReset');
    await click(view, 'tplResetConfirm');
    const del = api.calls.find(c => c.type === 'DELETE');
    assert.equal(del.url, '/jf/TgJelly/Templates/media.digest?expectedRevision=5');
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes('>Digest {{ count }}</textarea>'), html);
    assert.ok(html.includes(ui.label(EN['web.templates.builtIn'], 'neutral')));
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
    assert.ok(el('tgj-messages').innerHTML.includes('<option value="d2">Admins &lt;x&gt;</option>'));
    tplType(view, 'Test {{ title }}');
    tplInput(view, 'tplTestTo', { value: 'd2' }, 'change');
    await click(view, 'tplTest');
    assert.deepEqual(posted(api, 'Templates/Test'), [{ EventKey: 'media.movie', Text: 'Test {{ title }}', DestinationId: 'd2' }]);
    assert.ok(el('tgj-messages').innerHTML.includes(m.t(EN, 'web.templates.sent')));

    reply = { Sent: false, Error: 'Bot was <kicked>', Health: { Status: 'Lost' } };
    await click(view, 'tplTest');
    const html = el('tgj-messages').innerHTML;
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
    assert.ok(/<button[^>]*data-tgj-act="tplTest"[^>]*disabled/.test(el('tgj-messages').innerHTML));
    finish();
    await flush();
    assert.ok(!/<button[^>]*data-tgj-act="tplTest"[^>]*disabled/.test(el('tgj-messages').innerHTML));
});

test('flag shown for flagged key', async () => {
    const { view, el } = mount({ 'GET TgJelly/Templates': templates() });
    await show(view);
    tplInput(view, 'msgKey', { value: 'media.digest' }, 'change');
    await flush();
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes(m.t(EN, 'web.templates.flagError')));
    assert.ok(m.t(EN, 'web.templates.flagError').includes('so the built-in template was used instead:'));
    assert.ok(!html.includes(m.t(EN, 'web.templates.flagTruncated')));
    assert.ok(html.includes('bad &lt;tag&gt;'));
    tplInput(view, 'msgKey', { value: 'media.movie' }, 'change');
    await flush();
    assert.ok(!el('tgj-messages').innerHTML.includes('bad &lt;tag&gt;'));
});

test('a truncated flag says the message was shortened, not that the template failed', async () => {
    const dto = templates();
    dto.Items[1].Flag = { Kind: 'truncated', Error: '', At: '2026-10-20T10:00:00Z' };
    const { view, el } = mount({ 'GET TgJelly/Templates': dto });
    await show(view);
    tplInput(view, 'msgKey', { value: 'media.digest' }, 'change');
    await flush();
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes(m.t(EN, 'web.templates.flagTruncated')));
    assert.ok(!html.includes(m.t(EN, 'web.templates.flagError')));
});

test('template section shows events, fields and the docs link', async () => {
    const { view, el } = mount({ 'GET TgJelly/Templates': templates() });
    await show(view);
    const html = el('tgj-messages').innerHTML;
    // Spec §6.1: the guide link sits under the template, after the fields list, inside the left column.
    const link = html.indexOf('href="https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md"');
    const left = html.indexOf('<div class="tgj-tpl-edit"');
    const textarea = html.indexOf('<textarea');
    const fieldsEnd = html.indexOf('data-tgj-id="formatting"');
    const pane = html.indexOf('<div id="tgj-tpl-preview"');
    assert.ok(left >= 0 && left < textarea && textarea < fieldsEnd && fieldsEnd < link && link < pane, html);
    assert.equal(html.split('docs/templates.md').length, 2, 'one guide link');
    const intro = html.indexOf(m.t(EN, 'web.templates.intro'));
    assert.ok(intro >= 0 && intro < left, 'the intro stays at the top');
    assert.ok(html.includes(EN['web.event.media.movie']) && html.includes('Digest'));
    assert.ok(html.includes('The &lt;title&gt;.'));
    assert.ok(/<textarea class="[^"]*\btgj-template-text\b/.test(html));
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

// --- templates save themselves (plan 8 Task 8, design §6.1 and §6.3) ---

/** The template PUTs, as [path, body]. */
const tplPuts = api => api.calls.filter(c => c.type === 'PUT' && c.url.startsWith('/jf/TgJelly/Templates/'))
    .map(c => [c.url.replace('/jf/TgJelly/', ''), JSON.parse(c.data)]);
const TPL_CUE = 'template:media.movie';
/** A Validate route: errors for any text holding "oops", none otherwise. */
const validateOops = request => (JSON.parse(request.data).Text.includes('oops')
    ? validation({ Errors: [{ Message: 'Unexpected end', Line: 1, Column: 3 }], Html: '', Length: 0 })
    : validation());

test('template_autosavesAfter1s', async () => {
    const after = templates({ Revision: 6 });
    after.Items[0] = { ...after.Items[0], Text: 'New {{ title }}', Edited: true };
    const { view, api, el, hooks, timers } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/media.movie': after,
    });
    await show(view);
    tplType(view, 'New');
    await timers.advance(600);
    tplType(view, 'New {{ title }}');
    await timers.advance(999);
    assert.deepEqual(tplPuts(api), [], 'nothing is sent until 1 s after the last keystroke');
    assert.deepEqual(posted(api, 'Templates/Validate').at(-1), { EventKey: 'media.movie', Text: 'New {{ title }}' }, 'the preview keeps its 500 ms debounce');

    await timers.advance(1);
    assert.deepEqual(tplPuts(api), [['Templates/media.movie', { ExpectedRevision: 5, Text: 'New {{ title }}' }]]);
    assert.deepEqual(cue(view, TPL_CUE), SAVED_CUE);
    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes(`>${EN['web.templates.edited']}</span>`), 'the badge follows the save');
    assert.ok(html.includes('>New {{ title }}</textarea>'));
    assert.equal(hooks.pageRevision(), 6, 'the reply moves the page revision');
    assert.equal(hooks.state.forms['tpl:media.movie'].dirty, false);
    assert.equal(hooks.pendingFields.size, 0, 'the textarea follows the server again');

    await timers.advance(2000);
    assert.deepEqual(cue(view, TPL_CUE), IDLE_CUE);
    assert.equal(tplPuts(api).length, 1);
});

test('template_withErrors_notSaved', async () => {
    const { view, api, hooks, timers } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validateOops,
        'PUT TgJelly/Templates/media.movie': templates({ Revision: 6 }),
    });
    await show(view);
    tplType(view, 'Hi {{ oops');
    await timers.advance(1000);
    assert.deepEqual(tplPuts(api), []);
    assert.equal(EN['web.templates.notSavedErrors'], 'fix the template errors');
    assert.deepEqual(cue(view, TPL_CUE), errorCue(EN['web.templates.notSavedErrors']));

    // Leaving the field doesn't send it either, and the reason stays until the text is fixed (design §6.2).
    leave(view, tplAttrs());
    await timers.advance(5000);
    assert.deepEqual(tplPuts(api), []);
    assert.deepEqual(cue(view, TPL_CUE), errorCue(EN['web.templates.notSavedErrors']));
    assert.equal(hooks.state.forms['tpl:media.movie'].local.Text, 'Hi {{ oops', 'the typed text is kept');
});

test('a template with errors is not saved while its preview check is still on its way: the save checks that text first', async () => {
    const { view, api, timers } = mount({ 'GET TgJelly/Templates': templates(), 'PUT TgJelly/Templates/media.movie': templates({ Revision: 6 }) });
    const held = holdValidate(api);
    await show(view);
    held[0].reply(validation());
    tplType(view, 'Hi {{ oops');
    await timers.advance(1000);
    assert.equal(held.length, 3, 'the preview check, then the save asks for its own');
    assert.deepEqual(held[2].body, { EventKey: 'media.movie', Text: 'Hi {{ oops' });
    assert.deepEqual(tplPuts(api), [], 'nothing is sent before the check comes back');

    for (const request of held.slice(1)) {
        request.reply(validation({ Errors: [{ Message: 'Unexpected end' }] }));
    }

    await flush();
    assert.deepEqual(tplPuts(api), []);
    assert.deepEqual(cue(view, TPL_CUE), errorCue(EN['web.templates.notSavedErrors']));
});

test('template_empty_notSaved', async () => {
    const { view, api, hooks, timers } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/media.movie': templates({ Revision: 6 }),
    });
    await show(view);
    for (const value of ['', '   \n\t ']) {
        tplType(view, value);
        await timers.advance(1000);
        leave(view, tplAttrs());
        await flush();
        assert.deepEqual(tplPuts(api), [], JSON.stringify(value));
        assert.deepEqual(cue(view, TPL_CUE), errorCue(EN['web.templates.notSavedEmpty']), JSON.stringify(value));
    }

    assert.equal(hooks.state.forms['tpl:media.movie'].local.Text, '   \n\t ');
});

test('template_fixedAfterError_saves', async () => {
    const { view, api, timers } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validateOops,
        'PUT TgJelly/Templates/media.movie': templates({ Revision: 6 }),
    });
    await show(view);
    tplType(view, 'Hi {{ oops');
    await timers.advance(1000);
    assert.deepEqual(cue(view, TPL_CUE), errorCue(EN['web.templates.notSavedErrors']));

    tplType(view, 'Hi {{ title }}');
    await timers.advance(1000);
    assert.deepEqual(tplPuts(api), [['Templates/media.movie', { ExpectedRevision: 5, Text: 'Hi {{ title }}' }]]);
    assert.deepEqual(cue(view, TPL_CUE), SAVED_CUE);
});

test('a template save conflict takes the server data and retries once; a second conflict says it changed elsewhere', async () => {
    const fresh = templates({ Revision: 9 });
    fresh.Items[0].Text = 'theirs';
    const answers = [{ status: 409, body: { Current: fresh } }, templates({ Revision: 10 })];
    const first = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/media.movie': () => answers.shift(),
    });
    await show(first.view);
    tplType(first.view, 'mine');
    await first.timers.advance(1000);
    assert.deepEqual(tplPuts(first.api).map(([, body]) => body), [{ ExpectedRevision: 5, Text: 'mine' }, { ExpectedRevision: 9, Text: 'mine' }]);
    assert.deepEqual(cue(first.view, TPL_CUE), SAVED_CUE);

    const again = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Templates/media.movie': { status: 409, body: { Current: fresh } },
    });
    await show(again.view);
    tplType(again.view, 'mine');
    await again.timers.advance(1000);
    assert.equal(tplPuts(again.api).length, 2, 'retried once only');
    assert.deepEqual(cue(again.view, TPL_CUE), errorCue(EN['web.autosave.conflict']));
    assert.ok(again.el('tgj-messages').innerHTML.includes('>mine</textarea>'), 'the typed text is kept');
    assert.equal(again.hooks.state.templates.Revision, 9);
    assert.equal(again.hooks.state.forms['tpl:media.movie'].dirty, true);
});

test('reset drops a template save still waiting, so the typed text does not come back', async () => {
    const after = templates({ Revision: 5 });
    after.Items[1] = { ...after.Items[1], Text: 'Digest {{ count }}', Edited: false, Flag: null };
    const { view, api, el, timers } = mount({
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': validation(),
        'DELETE TgJelly/Templates/media.digest': after,
        'PUT TgJelly/Templates/media.digest': templates({ Revision: 6 }),
    });
    await show(view);
    tplInput(view, 'msgKey', { value: 'media.digest' }, 'change');
    await flush();
    tplType(view, 'typed', 'media.digest');
    await click(view, 'tplReset');
    await click(view, 'tplResetConfirm');
    await timers.advance(3000);
    assert.deepEqual(tplPuts(api), []);
    assert.ok(el('tgj-messages').innerHTML.includes('>Digest {{ count }}</textarea>'));
    assert.deepEqual(cue(view, 'template:media.digest'), IDLE_CUE);
});

test('noSaveButton_inTemplates', () => {
    for (const key of ['media.movie', 'media.digest']) {
        const html = renderTemplates(templates(), EN, tplUi({ key, result: validation(), good: validation() }));
        assert.ok(!html.includes('tplSave'), key);
        assert.ok(!/emby-|selectContainer|sectionTitle|verticalSection|fieldDescription|tgj-changed/.test(html), `${key}: only the dashboard components`);
        assert.ok(html.includes('<section class="tgj-card tgj-tpl"'), 'a card');
        // The text saves itself: the textarea names its save key, and the cue sits beside the textarea's label.
        const textarea = /<textarea[^>]*>/.exec(html)[0];
        assert.ok(textarea.includes(`data-tgj-save="template:${key}"`) && textarea.includes(`data-tgj-key="${key}"`), textarea);
        const label = html.indexOf(`>${m.t(EN, 'web.templates.text')}</`);
        const spot = html.indexOf(`<span class="tgj-cue" data-tgj-cue="template:${key}"`);
        assert.ok(label >= 0 && label < spot && spot < html.indexOf('<textarea'), html);
        assert.equal(html.split('data-tgj-cue=').length, 2, 'one cue spot');
        assert.ok(html.includes('data-tgj-act="tplTest"'), 'Send test stays an action');
    }

    assert.ok(!('web.templates.save' in EN));
    assert.ok(!SOURCE.includes('tplSave'));
});

test('the page is a thin shell: the stylesheet and the root, no inline styles', () => {
    const page = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html', import.meta.url), 'utf8');
    assert.match(page, /^<div data-role="page" id="tgjConfigPage" [^>]*data-controller="__plugin\/tgjelly.js">/);
    assert.ok(page.includes('<link id="tgjelly-css" rel="stylesheet" href="configurationpage?name=tgjelly.css">'));
    assert.ok(page.includes('<div id="tgjelly-root"></div>'));
    assert.ok(!page.includes('<style'), 'the old rules live in tgjelly.css');
    assert.ok(!page.includes('${'));
    const css = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');
    assert.ok(css.includes('#tgjelly-root .tgj-msg-layout'), 'the Messages layout rules');
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

test('every web.* key in en.json is used by tgjelly.js', () => {
    // web.event.* keys are built from the catalog's event keys (editor_eventLabels_haveNoGroupPrefix covers them).
    for (const key of Object.keys(EN).filter(k => k.startsWith('web.') && !k.startsWith('web.event.') && k !== SERVER_SIDE_KEY)) {
        const base = key.replace(/\.(one|other)$/, '');
        assert.ok(SOURCE.includes(`'${base}'`), key);
    }
});

// --- one revision across the sections (final review I1, spec §10.4) ---

/** A server with one Revision for every section, like PluginConfiguration.Revision; each save moves it on. */
function oneRevisionServer() {
    const server = { revision: 5, items: [dest()], tplText: '', conflicts: 0 };
    const conflictOr = (request, ok, current) => {
        if (JSON.parse(request.data).ExpectedRevision !== server.revision) {
            server.conflicts++;
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
        'POST TgJelly/Templates/Validate': validation(),
        'PUT TgJelly/Settings': r => conflictOr(r, () => settings({ Revision: server.revision }), () => settings({ Revision: server.revision })),
        // Like the server, the reply carries the Revision the change moved to.
        'PUT TgJelly/Destinations/d1': r => conflictOr(r, () => ({ ...dest({ Enabled: false }), Revision: server.revision }), () => destinations({ Revision: server.revision, Items: server.items })),
        'PUT TgJelly/Templates/media.movie': r => conflictOr(r, tpl, tpl),
    };
    return server;
}

test('save Messages, then toggle Sending: the PUT carries the new revision, no 409', async () => {
    const server = oneRevisionServer();
    const { view, api, el } = mount(server.routes);
    await show(view);
    toggle(view, 'ShowPoster');
    await flush();
    assert.equal(server.revision, 6);

    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });

    assert.deepEqual(putBodies(api), [{ ExpectedRevision: 6, Enabled: false }]);
    assert.equal(server.revision, 7);
    assert.ok(!destEl(el).includes(m.t(EN, 'web.dest.conflictPlain')));
});

test('a destination change, then a template save and a settings save: each sends the current revision', async () => {
    const server = oneRevisionServer();
    const { view, api, el, timers } = mount(server.routes);
    await show(view);
    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });
    tplType(view, 'New {{ title }}');
    await timers.advance(1000);
    assert.deepEqual(cue(view, 'template:media.movie'), SAVED_CUE);
    toggle(view, 'ShowPoster');
    await flush();

    const revisions = api.calls.filter(c => c.type === 'PUT').map(c => JSON.parse(c.data).ExpectedRevision);
    assert.deepEqual(revisions, [5, 6, 7]);
    assert.equal(server.revision, 8);
    assert.ok(!el('tgj-messages').innerHTML.includes(m.t(EN, 'web.templates.conflict')));
    assert.deepEqual(cue(view, toggleCue('ShowPoster')), SAVED_CUE);
});

test('a status poll with a higher revision reloads the sections, keeping unsaved edits', async () => {
    const server = oneRevisionServer();
    const { view, api, el, hooks, timers } = mount(server.routes);
    await show(view);
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
const liveTextarea = (view, doc) => liveFields(view, doc, 'tgj-messages', [{ tag: 'textarea', attributes: { 'data-tgj-input': 'tplText', 'data-tgj-key': 'media.movie' } }]);

test('a Destinations render while typing keeps the textarea focus, selection and scroll', async () => {
    const server = oneRevisionServer();
    const { view, el, doc } = mount(server.routes);
    await show(view);
    const textarea = liveTextarea(view, doc);
    const typing = textarea();
    typing.focus();
    Object.assign(typing, { selectionStart: 3, selectionEnd: 5, selectionDirection: 'forward', scrollTop: 40, scrollLeft: 2 });
    tplType(view, 'abcdef {{ title }}');
    const writes = el('tgj-messages').writes;

    // Toggling a destination renders Destinations, which renders the templates section with the typed text.
    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });

    assert.ok(el('tgj-messages').writes > writes, 'the section was written again');
    assert.ok(el('tgj-messages').innerHTML.includes('abcdef {{ title }}</textarea>'), 'the unsaved text is kept');
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
    tplType(view, 'typed elsewhere');
    const writes = el('tgj-messages').writes;

    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });

    assert.ok(el('tgj-messages').writes > writes);
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
    overview: ['tgj-connect', 'tgj-status', 'tgj-bot'],
    destinations: ['tgj-destinations'],
    messages: ['tgj-messages'],
    advanced: ['tgj-proxy', 'tgj-updates', 'tgj-batching', 'tgj-manual', 'tgj-diagnostics'],
};

test('shell_rendersAllSections_onlyActiveVisible', () => {
    assert.deepEqual(m.SECTIONS, ['overview', 'destinations', 'messages', 'advanced']);
    const html = m.renderShell(EN, {
        active: 'messages',
        connection: { tone: 'good', text: 'Connected · @jf_bot' },
        counts: { destinations: '3', messages: '1 edited' },
    });

    assert.match(html, /<h1 class="tgj-header-title">TgJelly<\/h1>/);
    assert.ok(html.includes(ui.label('Connected · @jf_bot', 'good')), 'the connection is a state label');
    assert.match(html, /<nav class="tgj-sidebar" aria-label="[^"]+">/);

    const nav = shellNav(html);
    assert.deepEqual([...nav.keys()], m.SECTIONS);
    const labels = { overview: 'Overview', destinations: 'Destinations', messages: 'Messages', advanced: 'Advanced' };
    for (const [name, button] of nav) {
        assert.equal(EN[`web.nav.${name}`], labels[name]);
        assert.ok(button.includes(`<span class="tgj-nav-label">${labels[name]}</span>`), button);
        assert.ok(button.startsWith('<button type="button"'), 'a real button, never a link with href="#…"');
        assert.equal(/aria-current="page"/.test(button), name === 'messages', `${name} aria-current`);
    }

    assert.match(nav.get('destinations'), /<span id="tgj-nav-count-destinations" class="tgj-nav-count">3<\/span>/);
    assert.match(nav.get('messages'), /<span id="tgj-nav-count-messages" class="tgj-nav-count">1 edited<\/span>/);
    assert.match(nav.get('overview'), /<span id="tgj-nav-count-overview" class="tgj-nav-count"><\/span>/);

    const sections = shellSections(html);
    assert.deepEqual([...sections.keys()], m.SECTIONS);
    for (const [name, { attrs, body }] of sections) {
        assert.equal(/\shidden(\s|$)/.test(attrs), name !== 'messages', `${name} hidden`);
        assert.ok(attrs.includes(`id="tgj-sec-${name}"`));
        const ids = [...body.matchAll(/id="([^"]+)"/g)].map(x => x[1]);
        assert.deepEqual(ids, HOSTS[name], `${name} holds today's containers, in order`);
    }

    // The templates live in Messages now (plan 9): there is no Templates section or host.
    assert.ok(!html.includes('id="tgj-templates"'));
    assert.doesNotMatch(html, /[●✓✔✅⚠❌✗🟢🔴]/u);
});

test('renderShell escapes the label and counts, and an unknown active section shows Overview', () => {
    const html = m.renderShell(EN, { active: 'nope', connection: { tone: 'attention', text: '<b>x</b>' }, counts: { destinations: '<i>' } });
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

test('connectionLabel: connected names the bot, no token is neutral, problems need attention', () => {
    assert.deepEqual(m.connectionLabel(status(), EN), { tone: 'good', text: 'Connected · @jf_bot' });
    assert.deepEqual(m.connectionLabel(status({ HasToken: false, BotUsername: null, ConnectionState: 'NoToken' }), EN), { tone: 'neutral', text: EN['web.header.notConnected'] });
    assert.deepEqual(m.connectionLabel(status({ ConnectionState: 'TokenRejected' }), EN), { tone: 'attention', text: EN['web.header.attention'] });
    assert.deepEqual(m.connectionLabel(status({ ConnectionState: 'Connecting' }), EN), { tone: 'progress', text: EN['web.header.connecting'] });
    assert.deepEqual(m.connectionLabel(null, EN, true), { tone: 'attention', text: EN['web.header.unknown'] });
    for (const key of ['web.header.connected', 'web.header.notConnected', 'web.header.attention', 'web.header.connecting', 'web.header.unknown']) {
        assert.doesNotMatch(EN[key], /[●✓✔✅⚠❌✗🟢🔴]/u, key);
    }
});

test('the header and the sidebar counts follow the data', async () => {
    let current = status();
    const { view, el, timers } = mount({ 'GET TgJelly/Status': () => current, 'GET TgJelly/Templates': templates() });
    await show(view);
    assert.equal(el('tgj-conn').innerHTML, ui.label('Connected · @jf_bot', 'good'));
    assert.equal(el('tgj-nav-count-destinations').innerHTML, '1');
    assert.equal(el('tgj-nav-count-messages').innerHTML, '1 edited');

    current = status({ ConnectionState: 'Unreachable' });
    timers.tick();
    await flush();
    assert.equal(el('tgj-conn').innerHTML, ui.label(EN['web.header.attention'], 'attention'));
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
    assert.equal(m.sectionFromHash('#/configurationpage?name=TgJelly&tgj=templates'), 'messages', 'an old Templates link opens Messages');
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

    const bare = mount({}, { hash: '#tgj=destinations' });
    await show(bare.view);
    assert.equal(shownSection(bare.el), 'destinations');
});

test('notConnected_opensOverview', async () => {
    const { view, el, timers } = mount({ 'GET TgJelly/Status': noToken() }, { hash: '#/configurationpage?name=TgJelly&tgj=messages' });
    await show(view);
    assert.equal(shownSection(el), 'overview', 'Overview leads with Connect');
    assert.equal(el('tgj-conn').innerHTML, ui.label(EN['web.header.notConnected'], 'neutral'));

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
    current = settings({ PublicUrl: 'https://a.example', Revision: 6, Message: messageDto({ ...FLAT_MESSAGE, ShowPoster: false }) });
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

    current = settings({ Revision: 6, UpdateCheck: { Enabled: true, Fixes: true, NewReleases: true, NewMajor: true } });
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

const overviewHtml = el => ['tgj-status', 'tgj-connect', 'tgj-bot'].map(id => el(id).innerHTML).join('\n');

test('overview_cards_statusBotSummary', async () => {
    const { view, el } = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest(), dest({ Id: 'd2', Health: { Status: 'NeedsPermissions', PhotosBlocked: false } })] }),
        'GET TgJelly/Templates': templates(),
    });
    await show(view);

    const statusHtml = el('tgj-status').innerHTML;
    assert.ok(statusHtml.includes('class="tgj-card"') && statusHtml.includes('>Status</h2>'));
    assert.ok(statusHtml.includes(ui.label('Connected to Telegram', 'good')), 'health is a state label');
    assert.ok(statusHtml.includes('class="tgj-row'));

    const botHtml = el('tgj-bot').innerHTML;
    assert.ok(botHtml.includes('class="tgj-card"') && botHtml.includes('>Bot</h2>'));
    assert.ok(botHtml.includes('@jf_bot'));
    for (const act of ['changeToken', 'disconnect']) {
        assert.match(botHtml, new RegExp(`<button type="button" class="tgj-btn[^"]*" data-tgj-act="${act}"`));
    }
    assert.match(botHtml, /<a [^>]*class="tgj-btn"[^>]*href="https:\/\/t\.me\/jf_bot\?start=setup"[^>]*>Open in Telegram<\/a>/);
    assert.equal(el('tgj-connect').innerHTML, '', 'no connect form while a bot is connected');
});

test('overview_noBot_leadsWithConnectCard_noSave', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': noToken() });
    await show(view);
    assert.ok(el('tgj-connect').innerHTML.includes('class="tgj-card"'));
    const order = id => el('tgjelly-root').innerHTML.indexOf(`id="${id}"`);
    assert.ok(order('tgj-connect') > -1 && order('tgj-connect') < order('tgj-status'), 'Connect leads without a bot');
    assert.ok(!el('tgj-status').innerHTML.includes('Waiting for metadata'), 'Status is only the connection line');
    assert.equal(el('tgj-bot').innerHTML, '');
    await click(view, 'connectExisting');
    const html = el('tgj-connect').innerHTML;
    assert.ok(html.includes('data-tgj-act="saveToken"') && html.includes('>Connect</button>'));
    assert.ok(!/>Save</.test(html), 'no Save button');
    assert.match(html, /<input[^>]*type="password"[^>]*data-tgj-input="token"/);
});

test('overview_connected_statusLeads', async () => {
    const { view, el } = mount();
    await show(view);
    const order = id => el('tgjelly-root').innerHTML.indexOf(`id="${id}"`);
    assert.equal(el('tgj-connect').innerHTML, '');
    assert.ok(order('tgj-status') < order('tgj-bot'));
    assert.ok(el('tgj-status').innerHTML.includes('Waiting for metadata'));
});

test('header_showsVersionLabelNextToTitle', async () => {
    const html = m.renderShell(EN, { active: 'overview', connection: { tone: 'good', text: 'Connected' }, version: '1.0.5.0', buildTarget: '10.11' });
    const version = ui.label('v0.5', 'neutral', { title: 'TgJelly 1.0.5.0, built for Jellyfin 10.11' });
    assert.match(html, /<h1 class="tgj-header-title">TgJelly<\/h1>/);
    assert.ok(html.indexOf('</h1>') < html.indexOf(version), 'the version follows the title');
    assert.ok(html.indexOf(version) < html.indexOf(ui.label('Connected', 'good')), 'and precedes the connection');
    assert.ok(!m.renderShell(EN, { active: 'overview', connection: { tone: 'neutral', text: 'x' } }).includes('>v'), 'no version, no label');

    const { view, el } = mount({ 'GET TgJelly/Status': status({ Version: '1.0.5.0' }) });
    await show(view);
    assert.equal(el('tgj-version').innerHTML, version, 'painted from the status');
});

test('header_versionLabel_isReleaseName', async () => {
    assert.equal(m.releaseName('1.0.4.0'), 'v0.4');
    assert.equal(m.releaseName('2.1.12.0'), 'v1.12');
    assert.equal(m.releaseName('1.0.4'), 'v1.0.4', 'a malformed version shows raw');
    assert.equal(m.releaseName('1.x.4.0'), 'v1.x.4.0');
    const { view, el } = mount({ 'GET TgJelly/Status': status({ Version: '2.1.12.0' }) });
    await show(view);
    assert.ok(el('tgj-version').innerHTML.includes('>v1.12<'));
});

test('header_versionTitle_hasFullVersionAndBuildTarget', async () => {
    const { view, el } = mount({ 'GET TgJelly/Status': status({ Version: '1.0.4.0', BuildTarget: '10.11' }) });
    await show(view);
    assert.ok(el('tgj-version').innerHTML.includes('title="TgJelly 1.0.4.0, built for Jellyfin 10.11"'), el('tgj-version').innerHTML);
});

test('status_hasNoVersionLine', () => {
    const html = m.renderStatus(status(), EN);
    assert.ok(!html.includes('built for Jellyfin') && !html.includes('tgj-version'));
    assert.ok(!('web.status.version' in EN));
});

test('overview_hasNoAtAGlance', async () => {
    assert.equal(m.renderOverviewSummary, undefined);
    assert.ok(!Object.keys(EN).some(key => key.startsWith('web.summary.')));
    const { view, el } = mount();
    await show(view);
    assert.ok(!el('tgjelly-root').innerHTML.includes('At a glance'));
    assert.ok(!el('tgjelly-root').innerHTML.includes('tgj-summary'));
});

test('overview_accessIsInfoNotice', async () => {
    const { view, el } = mount();
    await show(view);
    const overview = overviewHtml(el);
    assert.ok(!el('tgj-bot').innerHTML.includes('Anyone who can open this bot'), 'not a card row any more');
    assert.ok(!/<h2[^>]*>Access<\/h2>/.test(overview), 'no card titled Access');
    assert.match(el('tgj-status').innerHTML, /<div class="tgj-notice tgj-notice--info[^"]*"[\s\S]*Anyone who can open this bot can manage notifications\. Keep it restricted in BotFather\./);
});

test('status_longConnectionMessage_staysOutsideTheLabel', () => {
    const message = 'Telegram rejected the token; paste a fresh one from BotFather';
    const html = m.renderStatus(status({ ConnectionState: 'TokenRejected', ConnectionMessage: message }), EN);
    assert.ok(html.includes(ui.label(EN['web.header.attention'], 'bad')), 'the label holds the state word');
    assert.ok(html.includes(message) && !html.includes(ui.label(message, 'bad')), 'the message is plain text');
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

test('disconnectConfirm_hasRemoveSwitch_offByDefault', async () => {
    const { view, api, el } = mount();
    await show(view);
    await click(view, 'disconnect');
    const html = el('tgj-bot').innerHTML;
    assert.equal(switchIn(html, 'disconnectRemoveDestinations'), false);
    assert.ok(html.includes('Also remove all destinations'));
    assert.ok(html.includes(m.t(EN, 'web.bot.disconnect.removeHint')), 'one line says what each choice keeps');
    await flip(view, 'disconnectRemoveDestinations');
    assert.equal(switchIn(el('tgj-bot').innerHTML, 'disconnectRemoveDestinations'), true);
    assert.equal(putBodies(api).length, 0, 'the switch is part of the question, not an auto-saved setting');
    await click(view, 'disconnectNo');
    await click(view, 'disconnect');
    assert.equal(switchIn(el('tgj-bot').innerHTML, 'disconnectRemoveDestinations'), false, 'cancelling resets it');
});

test('disconnectConfirm_switchOn_sendsFlag', async () => {
    const { view, api } = mount({ 'DELETE TgJelly/Token': { status: 204 } });
    await show(view);
    await click(view, 'disconnect');
    await click(view, 'disconnectYes');
    assert.equal(api.calls.filter(c => c.type === 'DELETE')[0].url, '/jf/TgJelly/Token?removeDestinations=false');

    await show(view);
    await click(view, 'disconnect');
    await flip(view, 'disconnectRemoveDestinations');
    await click(view, 'disconnectYes');
    assert.equal(api.calls.filter(c => c.type === 'DELETE')[1].url, '/jf/TgJelly/Token?removeDestinations=true');
});

// --- Messages and Advanced, with auto-save (plan 8, task 6) ---

const MESSAGE_KEYS = ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks', 'AnnounceUpgrades'];
const BATCHING_KEYS = ['QuietWindowSeconds', 'MaxHoldMinutes', 'ReleaseQuietSeconds', 'DigestThreshold', 'LateEditHours', 'ReadyTimeoutMinutes'];
const ADVANCED_IDS = ['tgj-proxy', 'tgj-updates', 'tgj-batching', 'tgj-manual', 'tgj-diagnostics'];

test('validatePublicUrl ports PublicUrlRules: absolute http(s), a host with a dot, no IPv6; empty is valid', () => {
    for (const ok of ['', '  ', 'https://jf.example.com', 'http://192.168.1.5:8096', 'HTTPS://JF.Example.com/jellyfin', ' https://jf.example.com ', 'https://jelly.']) {
        assert.equal(msg.validatePublicUrl(ok), true, ok);
    }
    for (const bad of ['http://localhost:8096', 'http://jellyfin:8096', 'jf.example.com', 'ftp://jf.example.com', 'https://[2001:db8::1]:8096',
        'https://[::ffff:10.0.0.1]', 'https://', 'not a url', null, undefined]) {
        assert.equal(msg.validatePublicUrl(bad), bad === null || bad === undefined, String(bad));
    }
});

test('toggle_savesImmediately_showsSavedBesideRow', async () => {
    const { view, api, el, timers, hooks } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    assert.match(el('tgj-messages').innerHTML, /role="switch"[^>]*aria-checked="false"[^>]*data-tgj-input="msgSwitch" data-tgj-id="media.movie" data-tgj-key="ShowProviderLinks"/);
    const hold = holdPuts(api);

    toggle(view, 'ShowProviderLinks');
    await flush();
    assert.equal(putSettings(api).length, 1, 'sent at once, with no Save to press');
    assert.match(el('tgj-messages').innerHTML, /aria-checked="true"[^>]*data-tgj-input="msgSwitch" data-tgj-id="media.movie" data-tgj-key="ShowProviderLinks"/, 'the switch shows the choice');
    assert.equal(hooks.pendingFields.size, 1, 'the switch is pending while its save is in flight');
    await timers.advance(299);
    assert.deepEqual(cue(view, toggleCue('ShowProviderLinks')), IDLE_CUE, 'a fast save shows no Saving…');
    await timers.advance(1);
    assert.deepEqual(cue(view, toggleCue('ShowProviderLinks')), SAVING_CUE);

    hold.release();
    await flush();
    const [body] = putSettings(api);
    assert.equal(body.ExpectedRevision, 5);
    assert.deepEqual(body.Message.PerMessage, [{ EventKey: 'media.movie', Switches: { ShowProviderLinks: true } }], 'only the switch that changed');
    assert.deepEqual(cue(view, toggleCue('ShowProviderLinks')), SAVED_CUE, 'Saved, beside the switch that changed');
    for (const key of MESSAGE_KEYS.filter(k => k !== 'ShowProviderLinks')) {
        assert.deepEqual(cue(view, toggleCue(key)), IDLE_CUE, key);
    }
    assert.equal(hooks.pendingFields.size, 0, 'settled: nothing pending');
    assert.equal(hooks.state.settings.Revision, 6);

    await timers.advance(2000);
    assert.deepEqual(cue(view, toggleCue('ShowProviderLinks')), IDLE_CUE, 'gone after 2 s, class and text, so reduced motion hides it too');
});

test('batching_savesAfter800ms_orOnBlur', async () => {
    const { view, api, timers, hooks, el } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    assert.match(el('tgj-batching').innerHTML, /type="number"[^>]*data-tgj-input="batching"[^>]*value="120"[^>]*min="5" max="3600"/);

    batchInput(view, 'QuietWindowSeconds', '3');
    batchInput(view, 'QuietWindowSeconds', '30');
    await flush();
    assert.ok(hooks.pendingFields.has('[data-tgj-input="batching"][data-tgj-key="QuietWindowSeconds"][data-tgj-field="Batching.QuietWindowSeconds"]'));
    await timers.advance(799);
    assert.equal(putSettings(api).length, 0, 'not while typing');
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), IDLE_CUE, 'a half-typed value shows no error');
    await timers.advance(1);
    assert.equal(putSettings(api).length, 1, '800 ms after the last keystroke');
    assert.equal(putSettings(api)[0].Batching.QuietWindowSeconds, 30);
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), SAVED_CUE);
    assert.equal(hooks.pendingFields.size, 0);

    batchInput(view, 'DigestThreshold', '20');
    leave(view, batchAttrs('DigestThreshold'), batchAttrs('LateEditHours'));
    await flush();
    assert.equal(putSettings(api).length, 2, 'or at once when the field loses focus');
    assert.deepEqual(putSettings(api)[1].Batching, { ...settings().Batching, QuietWindowSeconds: 30, DigestThreshold: 20 });
    await timers.advance(800);
    assert.equal(putSettings(api).length, 2, 'and only once');

    batchInput(view, 'MaxHoldMinutes', '500');
    await timers.advance(800);
    assert.equal(putSettings(api).length, 2, 'out of range: not sent');
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), errorCue(m.text(EN, 'web.settings.range', { min: 1, max: 240 })));
    batchInput(view, 'MaxHoldMinutes', '60');
    await flush();
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), IDLE_CUE, 'the error goes once the value changes');
});

test('publicUrl_savesOnBlurOnly_validatesClientSide', async () => {
    const { view, api, timers, el, hooks } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    assert.match(el('tgj-messages').innerHTML, /data-tgj-input="publicUrl"[^>]*data-tgj-save="PublicUrl"/);

    urlInput(view, 'https://jelly.');
    await timers.advance(5000);
    assert.equal(putSettings(api).length, 0, 'typing never sends, even a URL that passes the rules');

    urlInput(view, 'http://localhost:8096');
    leave(view, URL_ATTRS);
    await flush();
    assert.equal(putSettings(api).length, 0, 'an invalid URL is not sent');
    assert.deepEqual(cue(view, 'PublicUrl'), errorCue(EN['web.messages.urlInvalid']));
    await hooks.reloadSection('messages', { fresh: true });
    assert.ok(el('tgj-messages').innerHTML.includes('value="http://localhost:8096"'), 'a refresh keeps what was typed');
    assert.deepEqual(cue(view, 'PublicUrl'), errorCue(EN['web.messages.urlInvalid']), 'and the cue, painted into the new spot');

    urlInput(view, 'https://jf.example.com');
    await flush();
    assert.deepEqual(cue(view, 'PublicUrl'), IDLE_CUE);
    dispatchOn(view, 'keydown', URL_ATTRS, { tagName: 'INPUT' }, { key: 'Enter' });
    await flush();
    assert.deepEqual(putSettings(api).map(b => b.PublicUrl), ['https://jf.example.com'], 'Enter saves too');
    assert.deepEqual(cue(view, 'PublicUrl'), SAVED_CUE);

    urlInput(view, '');
    leave(view, URL_ATTRS);
    await flush();
    assert.deepEqual(putSettings(api).map(b => b.PublicUrl), ['https://jf.example.com', ''], 'an empty URL is valid: it turns the buttons off');
});

test('proxy_savesOnceWhenLeavingCard_notPerKeystroke', async () => {
    const { view, api, timers, el } = mount({
        'PUT TgJelly/Proxy': request => {
            const body = JSON.parse(request.data);
            return settings({ Revision: 6, ProxyMode: body.Mode, ProxyUrl: body.Url, HasProxyUsername: 'Username' in body });
        },
    });
    await show(view);
    for (const name of ['proxyMode', 'proxyUsername', 'proxyPassword']) {
        assert.match(proxyEl(el), new RegExp(`data-tgj-input="${name}"[^>]*data-tgj-save="proxy"`), name);
    }
    assert.equal((proxyEl(el).match(/data-tgj-cue=/g) ?? []).length, 1, 'one cue for the card: it saves as one');

    proxyMode(view, 'Socks5');
    leave(view, proxyAttrs('proxyMode'), proxyAttrs('proxyUrl'));
    for (const typed of ['p', 'pr', 'proxy:1080']) {
        proxyType(view, 'proxyUrl', typed);
    }
    leave(view, proxyAttrs('proxyUrl'), proxyAttrs('proxyUsername'));
    proxyType(view, 'proxyUsername', 'bob');
    await timers.advance(5000);
    assert.equal(putProxy(api).length, 0, 'nothing is sent while focus stays in the card');

    leave(view, proxyAttrs('proxyUsername'));
    await flush();
    assert.deepEqual(putProxy(api), [{ ExpectedRevision: 5, Mode: 'Socks5', Url: 'proxy:1080', Username: 'bob' }], 'one save, with every field');
    assert.deepEqual(cue(view, 'proxy'), SAVED_CUE);

    leave(view, proxyAttrs('proxyUrl'));
    await flush();
    assert.equal(putProxy(api).length, 1, 'leaving again sends nothing');
});

test('proxyModeWithoutAddress_notSaved', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Proxy': () => settings({ Revision: 6, ProxyMode: 'Http', ProxyUrl: 'h:1' }) });
    await show(view);
    proxyMode(view, 'Http');
    leave(view, proxyAttrs('proxyMode'));
    await flush();
    assert.equal(putProxy(api).length, 0);
    assert.deepEqual(cue(view, 'proxy'), errorCue(EN['web.proxy.needsAddress']));
    assert.equal(EN['web.proxy.needsAddress'], 'enter the proxy address');

    proxyType(view, 'proxyUrl', 'h:1');
    await flush();
    assert.deepEqual(cue(view, 'proxy'), IDLE_CUE);
    dispatchOn(view, 'keydown', proxyAttrs('proxyUrl'), { tagName: 'INPUT' }, { key: 'Enter' });
    await flush();
    assert.deepEqual(putProxy(api), [{ ExpectedRevision: 5, Mode: 'Http', Url: 'h:1' }]);
});

test('noSaveButtons_inMessagesOrAdvanced', async () => {
    const { view, el } = mount({
        ...unlocked(),
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'p:1', PublicUrl: 'http://localhost', PublicUrlValid: false, PublicUrlSuggestion: 'https://jf.example.com' }),
    });
    await show(view);
    const ids = ['tgj-messages', ...ADVANCED_IDS];
    const html = ids.map(id => el(id).innerHTML).join('\n');
    for (const id of ADVANCED_IDS) {
        assert.match(el(id).innerHTML, /^<section class="tgj-card[ "]/, `${id} is a card`);
    }
    // Messages is a layout of cards: the pickers, What it shows, the template and the preview.
    assert.match(el('tgj-messages').innerHTML, /^<div class="tgj-msg-layout"><section class="tgj-card[ "]/);
    assert.ok(!/saveSettings|saveProxy|\bSave\b/.test(html), 'no Save buttons');
    for (const key of ['web.messages.save', 'web.batching.save', 'web.updates.save', 'web.proxy.save', 'web.settings.conflict', 'web.proxy.conflict']) {
        assert.ok(!(key in EN), `${key} is gone`);
    }
    assert.ok(!/emby-|inputContainer|selectContainer|sectionTitle|verticalSection|fieldDescription/.test(html), 'only the dashboard components');
    assert.ok(!SYMBOLS.test(html));

    // Every setting has its cue spot beside it.
    const cueKeys = ['PublicUrl', ...MESSAGE_KEYS.map(toggleCue), ...BATCHING_KEYS.map(k => `Batching.${k}`), 'UpdateCheck.Enabled', 'UpdateCheck.Mode', 'proxy'];
    for (const key of cueKeys) {
        assert.ok(html.includes(`data-tgj-cue="${key}"`), key);
    }

    // Every auto-saved control names its save, and can be found again after a render (data-tgj-input). The message
    // pickers and the test send's destination choose what to show or where to send: they save nothing.
    const settingsHtml = ['tgj-messages', 'tgj-proxy', 'tgj-batching', 'tgj-updates'].map(id => el(id).innerHTML).join('');
    const controls = settingsHtml.match(/<(?:input|select)\b[^>]*>|<button[^>]*role="switch"[^>]*>/g)
        .filter(control => !/data-tgj-input="(?:msgSection|msgKey|tplTestTo)"/.test(control));
    assert.equal(controls.length, 1 + 6 + 4 + 6 + 2);
    for (const control of controls) {
        assert.match(control, /data-tgj-input="[^"]+"[\s\S]*data-tgj-save="[^"]+"/, control);
    }

    // The actions stay buttons.
    for (const act of ['copyDiagnostics', 'manualAdd', 'detectedSetUp', 'detectedDismiss', 'useSuggested']) {
        assert.ok(html.includes(`data-tgj-act="${act}"`), act);
    }
});

test('settingsConflict_retriesWithServerData', async () => {
    const theirs = settings({ Revision: 9, Message: messageDto({ ...FLAT_MESSAGE, ShowOverview: false }) });
    const answers = [{ status: 409, body: { Current: theirs } }];
    const echo = echoSettings(9, theirs.Message);
    const { view, api, el, hooks } = mount({ 'PUT TgJelly/Settings': request => answers.shift() ?? echo(request) });
    await show(view);
    toggle(view, 'ShowProviderLinks');
    await flush();

    const puts = putSettings(api);
    assert.deepEqual(puts.map(b => b.ExpectedRevision), [5, 9], 'retried once, with the server revision');
    assert.deepEqual(puts[1].Message.PerMessage, [{ EventKey: 'media.movie', Switches: { ShowProviderLinks: true } }], 'the admin change is re-applied, alone');
    assert.equal(hooks.state.settings.Message.PerMessage[0].Switches.ShowOverview, false, 'on top of the change made elsewhere');
    assert.deepEqual(cue(view, toggleCue('ShowProviderLinks')), SAVED_CUE);
    assert.equal(hooks.state.settings.Revision, 10);
    assert.match(el('tgj-messages').innerHTML, /aria-checked="false"[^>]*data-tgj-key="ShowOverview"/, 'the other change shows');

    // Changed elsewhere twice in a row: stop, and say so beside the field.
    const always = mount({ 'PUT TgJelly/Settings': { status: 409, body: { Current: theirs } } });
    await show(always.view);
    toggle(always.view, 'ShowProviderLinks');
    await flush();
    assert.equal(putSettings(always.api).length, 2);
    assert.deepEqual(cue(always.view, toggleCue('ShowProviderLinks')), errorCue('changed elsewhere, check the value'));
    assert.match(always.el('tgj-messages').innerHTML, /aria-checked="true"[^>]*data-tgj-key="ShowProviderLinks"/, 'the switch keeps the admin choice');
    assert.equal(always.hooks.state.forms.settings.local.Message.Switches.media.movie.ShowProviderLinks, true);
});

test('leaving the page saves what is still waiting', async () => {
    const { view, api } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    batchInput(view, 'DigestThreshold', '20');
    urlInput(view, 'https://jf.example.com');
    await flush();
    assert.equal(putSettings(api).length, 0);
    view.dispatchEvent(new Event('viewbeforehide'));
    await flush();
    assert.deepEqual(putSettings(api).map(b => [b.Batching.DigestThreshold, b.PublicUrl]), [[20, ''], [20, 'https://jf.example.com']]);
});

test('the manual block and diagnostics are cards: their actions stay buttons', async () => {
    Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: async () => { throw new Error('denied'); } } }, configurable: true });
    const { view, el } = mount({ ...unlocked(), 'GET TgJelly/Diagnostics': { Text: 'diag' } });
    await show(view);
    assert.match(manualEl(el), /class="tgj-field" data-tgj-input="manualInput"/);
    assert.match(manualEl(el), /<select class="tgj-field tgj-select" data-tgj-input="manualPreset"/);
    assert.match(manualEl(el), /class="tgj-btn tgj-btn--primary" data-tgj-act="manualAdd"/);
    await click(view, 'copyDiagnostics');
    assert.match(el('tgj-diagnostics').innerHTML, /<textarea id="tgj-diagnostics-text" class="tgj-field tgj-diag-text" readonly rows="12">diag<\/textarea>/);
});

// --- Task 6 fix round 1 ---

test('a save in flight does not cover a newer invalid change: its error stays, and the change stays pending', async () => {
    const { view, api, hooks } = mount({ 'PUT TgJelly/Proxy': request => settings({ Revision: 6, ProxyMode: JSON.parse(request.data).Mode }) });
    await show(view);
    const hold = holdPuts(api);
    proxyType(view, 'proxyUsername', 'bob');
    leave(view, proxyAttrs('proxyUsername'));
    await flush();
    assert.equal(putProxy(api).length, 1, 'the username save is in flight');

    proxyMode(view, 'Http');
    leave(view, proxyAttrs('proxyMode'));
    await flush();
    assert.deepEqual(cue(view, 'proxy'), errorCue(EN['web.proxy.needsAddress']));

    hold.release();
    await flush();
    assert.deepEqual(cue(view, 'proxy'), errorCue(EN['web.proxy.needsAddress']), 'the older save does not say Saved');
    assert.equal(hooks.state.forms.proxy.local.Mode, 'Http', 'the Http choice is kept');
    assert.ok(hooks.pendingFields.has('[data-tgj-input="proxyMode"]'), 'and stays pending, so a refresh keeps it');

    view.dispatchEvent(new Event('viewbeforehide'));
    await flush();
    assert.equal(putProxy(api).length, 1, 'still not sent without an address');
    assert.deepEqual(cue(view, 'proxy'), errorCue(EN['web.proxy.needsAddress']));
});

test('Enter, then the change a browser fires, then leaving the field: one save', async () => {
    const { view, api } = mount({
        'PUT TgJelly/Settings': echoSettings(),
        'PUT TgJelly/Proxy': request => settings({ Revision: 6, ProxyMode: 'Http', ProxyUrl: JSON.parse(request.data).Url }),
        'GET TgJelly/Settings': settings({ ProxyMode: 'Http', ProxyUrl: 'old:1' }),
    });
    await show(view);
    const enterThenLeave = async (attrs, value) => {
        dispatchOn(view, 'input', attrs, { value });
        dispatchOn(view, 'keydown', attrs, { tagName: 'INPUT', value }, { key: 'Enter' });
        await flush();
        dispatchOn(view, 'change', attrs, { value });
        leave(view, attrs);
        await flush();
    };

    await enterThenLeave(proxyAttrs('proxyUrl'), 'new:2');
    assert.deepEqual(putProxy(api).map(b => b.Url), ['new:2'], 'one bot restart, not two');
    await enterThenLeave(URL_ATTRS, 'https://jf.example.com');
    assert.deepEqual(putSettings(api).map(b => b.PublicUrl), ['https://jf.example.com']);
    await enterThenLeave(batchAttrs('DigestThreshold'), '20');
    assert.equal(putSettings(api).length, 2);
});

test('a save queued behind a settings save sends the revision that save moved to: no 409 from the page itself', async () => {
    const server = oneRevisionServer();
    const { view, api, hooks } = mount(server.routes);
    await show(view);
    assert.equal(hooks.pageRevision(), 5);
    const results = [];
    toggle(view, 'ShowPoster');
    await Promise.resolve();
    // A destination save (Task 7's kind) queued right behind it, built when it is sent.
    hooks.queueSave({
        key: 'dest:d1',
        mode: 'now',
        send: async () => {
            const result = await hooks.api.put('Destinations/d1', { ExpectedRevision: hooks.pageRevision(), Enabled: false });
            results.push(result.status);
            return result.status === 200 ? { status: 'saved' } : { status: 'failed', message: String(result.status) };
        },
    });
    await flush();

    assert.deepEqual(api.calls.filter(c => c.type === 'PUT').map(c => JSON.parse(c.data).ExpectedRevision), [5, 6]);
    assert.deepEqual(results, [200]);
    assert.equal(server.revision, 7);
    assert.equal(hooks.pageRevision(), 6, 'this raw send does not take its reply in; the destination save does (Task 7)');
});

test('validatePublicUrl matches System.Uri on hosts URL alone would accept', () => {
    for (const bad of ['http://.', 'http://..', 'http://.a', 'http://a..b', 'http://.example.com', 'http://*.b', 'http://a%2eb']) {
        assert.equal(msg.validatePublicUrl(bad), false, bad);
    }
    for (const ok of ['http://a.b.', 'http://a_b.c', 'http://-a.b', 'http://user:pw@jf.example.com', 'http://1.2']) {
        assert.equal(msg.validatePublicUrl(ok), true, ok);
    }
});

test('batching uses the server rules and messages, the longest hold against the quiet window too', async () => {
    assert.equal(EN['web.settings.range'], 'Must be between {min} and {max}.');
    assert.equal(EN['web.settings.maxHoldShort'], 'Must be at least as long as the quiet window.');
    const { view, api, timers } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);

    batchInput(view, 'MaxHoldMinutes', '300');
    await timers.advance(800);
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), errorCue('Must be between 1 and 240.'));

    // Saved quiet window: 120 s. A 1 minute hold is shorter.
    batchInput(view, 'MaxHoldMinutes', '1');
    await timers.advance(800);
    assert.deepEqual(cue(view, 'Batching.MaxHoldMinutes'), errorCue('Must be at least as long as the quiet window.'));
    // Saved longest hold: 30 min. A 3600 s quiet window is longer.
    batchInput(view, 'QuietWindowSeconds', '3600');
    await timers.advance(800);
    assert.deepEqual(cue(view, 'Batching.QuietWindowSeconds'), errorCue('Must be at least as long as the quiet window.'));
    assert.equal(putSettings(api).length, 0, 'an invalid pair is not sent');

    batchInput(view, 'MaxHoldMinutes', '60');
    await timers.advance(800);
    assert.equal(putSettings(api).length, 1);
    assert.equal(putSettings(api)[0].Batching.MaxHoldMinutes, 60);
    assert.equal(putSettings(api)[0].Batching.QuietWindowSeconds, 120, 'the quiet window edit is not sent with it');
});

// --- no decorative symbols (Plan 8 Task 9) ---

const NO_SYMBOLS = /[●✓✔✅⚠❌✗🟢🔴]/u;

test('renderedDashboard_hasNoDecorativeSymbols', async () => {
    const unhealthy = dest({ Id: 'd2', Title: 'Broken', Health: { Status: 'Lost', PhotosBlocked: true, LastError: 'gone', At: '2026-10-20T10:00:00Z' } });
    const failing = mount({
        'GET TgJelly/Destinations': destinations({ Items: [dest(), unhealthy], DetectedChats: [{ ChatId: -5, ThreadId: 0, Title: 'New chat', Kind: 'Group' }] }),
        'GET TgJelly/Diagnostics': { Text: 'TgJelly 1.0' },
        'POST TgJelly/Destinations/d1/Test': { Sent: false, Error: 'bot kicked', Health: { Status: 'Lost' } },
    });
    await show(failing.view);
    await destAct(failing.view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(failing.view, 'testDest', { 'data-tgj-id': 'd1' });
    const everything = view => [...IDS.map(id => view.elements.get(id)?.innerHTML ?? ''), view.elements.get('tgjelly-root').innerHTML].join('\n');
    assert.ok(everything(failing.view).includes('bot kicked'), 'the failed test outcome is on the page');
    assert.ok(!NO_SYMBOLS.test(everything(failing.view)), 'failed test outcome');

    const passing = mount({ 'POST TgJelly/Destinations/d1/Test': { Sent: true, Error: null, Health: { Status: 'Ok' } } });
    await show(passing.view);
    await destAct(passing.view, 'edit', { 'data-tgj-id': 'd1' });
    await destAct(passing.view, 'testDest', { 'data-tgj-id': 'd1' });
    assert.ok(!NO_SYMBOLS.test(everything(passing.view)), 'sent test outcome');

    const unconnected = mount({ 'GET TgJelly/Status': status({ ConnectionState: 'NotConfigured', HasToken: false, BotUsername: null }) });
    await show(unconnected.view);
    assert.ok(!NO_SYMBOLS.test(everything(unconnected.view)), 'not connected');
});

// --- a section reload that lands after a newer save (final review F1) ---

/** Holds the reply to the next GET of this path (the reply is read from the routes when it is sent); release() lets it land. */
function holdNextGet(api, path) {
    const fetch = api.fetch;
    let held = null;
    api.fetch = request => {
        const answer = fetch(request);
        if (request.type !== 'GET' || request.url !== `/jf/TgJelly/${path}` || held) {
            return answer;
        }

        answer.catch(() => {});
        return new Promise(resolve => {
            held = () => resolve(answer);
        });
    };
    return { sent: () => held !== null, release: () => held() };
}

test('a settings reload sent before a settings save and answered after it is ignored: the next save keeps the first change', async () => {
    let server = settings();
    const { view, api, el, hooks } = mount({
        'GET TgJelly/Settings': () => server,
        'PUT TgJelly/Destinations/d1': () => {
            server = { ...server, Revision: server.Revision + 1 };
            return { ...dest({ Enabled: false }), Revision: server.Revision };
        },
        'PUT TgJelly/Settings': request => {
            const body = JSON.parse(request.data);
            if (body.ExpectedRevision !== server.Revision) {
                return { status: 409, body: { Current: server } };
            }

            server = { ...server, Revision: server.Revision + 1, Message: mergeMessage(server.Message, body.Message), Batching: body.Batching };
            return server;
        },
    });
    await show(view);
    const reload = holdNextGet(api, 'Settings');
    // A destination save: once it settles, the page reloads the settings sections at the Revision it moved to (6).
    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });
    assert.ok(reload.sent(), 'a GET Settings is on its way, read at Revision 6');
    toggle(view, 'ShowPoster');
    await flush();
    assert.equal(server.Message.PerMessage[0].Switches.ShowPoster, false);
    assert.equal(server.Revision, 7);

    reload.release();
    await flush();
    assert.equal(hooks.state.settings.Revision, 7, 'the older reply is not taken');
    assert.equal(hooks.state.forms.settings.local.Message.Switches.media.movie.ShowPoster, false);
    assert.equal(switchIn(el('tgj-messages').innerHTML, 'msgSwitch', { 'data-tgj-id': 'media.movie', 'data-tgj-key': 'ShowPoster' }), false);

    toggle(view, 'ShowOverview');
    await flush();
    assert.deepEqual(putSettings(api).map(p => [p.ExpectedRevision, p.Message.PerMessage[0].Switches]), [[6, { ShowPoster: false }], [7, { ShowOverview: false }]]);
    assert.equal(server.Message.PerMessage[0].Switches.ShowPoster, false, 'the first change is not reverted');
    assert.equal(server.Message.PerMessage[0].Switches.ShowOverview, false);
    assert.deepEqual(cue(view, toggleCue('ShowOverview')), SAVED_CUE);
});

test('a templates reload sent before a template save and answered after it is ignored: the textarea keeps the saved text', async () => {
    let revision = 5;
    let text = '<b>{{ title }}</b>';
    const current = () => {
        const dto = templates({ Revision: revision });
        dto.Items[0] = { ...dto.Items[0], Text: text, Edited: text !== dto.Items[0].BuiltIn };
        return dto;
    };
    const { view, api, el, hooks, timers } = mount({
        'GET TgJelly/Templates': current,
        'PUT TgJelly/Destinations/d1': () => {
            revision++;
            return { ...dest({ Enabled: false }), Revision: revision };
        },
        'PUT TgJelly/Templates/media.movie': request => {
            const body = JSON.parse(request.data);
            if (body.ExpectedRevision !== revision) {
                return { status: 409, body: { Current: current() } };
            }

            revision++;
            text = body.Text;
            return current();
        },
    });
    await show(view);
    const reload = holdNextGet(api, 'Templates');
    await flip(view, 'destEnabled', { 'data-tgj-id': 'd1', 'data-tgj-save': 'dest:d1:Enabled' });
    assert.ok(reload.sent(), 'a GET Templates is on its way, read at Revision 6');
    tplType(view, 'New {{ title }}');
    await timers.advance(1000);
    assert.deepEqual(tplPuts(api), [['Templates/media.movie', { ExpectedRevision: 6, Text: 'New {{ title }}' }]]);
    assert.equal(revision, 7);

    reload.release();
    await flush();
    assert.equal(hooks.state.templates.Revision, 7, 'the older reply is not taken');
    assert.equal(hooks.state.forms['tpl:media.movie'].local.Text, 'New {{ title }}');
    assert.ok(el('tgj-messages').innerHTML.includes('>New {{ title }}</textarea>'), 'the textarea shows the saved text');
    assert.equal(text, 'New {{ title }}');
});

// --- actions wait for the auto-save queue (final review F3) ---

test('remove, add and set up started while a settings save is in flight send the Revision it moved to: no false conflict', async () => {
    const conflict = m.t(EN, 'web.dest.conflictPlain');
    const cases = [
        {
            act: 'removeYes', attrs: { 'data-tgj-id': 'd1' },
            sent: api => Number(/expectedRevision=(\d+)/.exec(api.calls.find(c => c.type === 'DELETE').url)[1]),
            section: el => destEl(el),
        },
        {
            act: 'manualAdd', attrs: {}, before: view => typeManual(view, '-100123'),
            sent: api => JSON.parse(posts(api, 'Destinations')[0].data).ExpectedRevision,
            section: el => manualEl(el),
        },
        {
            act: 'detectedSetUp', attrs: { 'data-tgj-id': '-100777' },
            sent: api => JSON.parse(posts(api, 'DetectedChats/-100777')[0].data).ExpectedRevision,
            section: el => manualEl(el),
        },
    ];
    for (const { act, attrs, before, sent, section } of cases) {
        let revision = 5;
        const list = () => destinations({ Revision: revision, ManualUnlocked: true, DetectedChats: DETECTED });
        const checked = (expected, ok) => (expected === revision ? (revision++, ok()) : { status: 409, body: { Current: list() } });
        const { view, api, el } = mount({
            'GET TgJelly/Settings': () => settings({ Revision: revision }),
            'GET TgJelly/Destinations': list,
            'PUT TgJelly/Settings': r => checked(JSON.parse(r.data).ExpectedRevision, () => settings({ Revision: revision })),
            'DELETE TgJelly/Destinations/d1': r => checked(Number(/expectedRevision=(\d+)/.exec(r.url)[1]), () => ({ status: 204 })),
            'POST TgJelly/Destinations': r => checked(JSON.parse(r.data).ExpectedRevision, () => added()),
            'POST TgJelly/DetectedChats/-100777': r => checked(JSON.parse(r.data).ExpectedRevision, () => added()),
        });
        await show(view);
        await before?.(view);
        const puts = holdPuts(api);
        toggle(view, 'ShowPoster');
        await flush();
        assert.equal(putSettings(api).length, 1, `${act}: the settings save is in flight`);
        clickOn(view, { 'data-tgj-act': act, ...attrs });
        await flush();
        puts.release();
        await flush();

        assert.equal(sent(api), 6, `${act}: it sends the Revision the settings save moved to`);
        assert.equal(revision, 7, `${act}: the server took it`);
        assert.ok(!section(el).includes(conflict), `${act}: no changed-elsewhere message`);
    }
});
