// The Log section (plan 9 Task 13, design §8.1): every event, per destination, with its result and a plain reason.
// Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import * as log from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-log.js';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import { makeView, makeApiClient, fakeTimers, flush, clickOn, dispatchOn } from './fake-view.mjs';

const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));
const SYMBOLS = /[●✓✔✅⚠❌✗🟢🔴]/u;

// A fixed "now" for the pure renders: 2026-10-24 15:00 local time.
const NOW = new Date(2026, 9, 24, 15, 0, 0).getTime();
const hoursAgo = hours => new Date(NOW - hours * 3600_000).toISOString();

function row(overrides = {}) {
    return {
        Id: 'media.movie|d1|1', At: hoursAgo(1), UpdatedAt: hoursAgo(1), EventKey: 'media.movie', Title: 'Dune: Part Two',
        DestinationId: 'd1', DestinationTitle: 'Media channel', Result: 'Sent', Details: [], Telegram: null, Tries: 1,
        RetryAt: null, Advice: null, ...overrides,
    };
}

const FAILED = row({
    Id: 'alert-7', EventKey: 'security.login_failed', Title: 'admin', DestinationId: 'd2', DestinationTitle: 'Admin alerts',
    Result: 'Failed', Details: [EN['web.log.why.failed.Lost']], Telegram: '403 Forbidden: bot was kicked from the supergroup chat',
    Tries: 1, Advice: 'Add the bot back to Admin alerts, then press Check again on the destination.',
});

function dto(overrides = {}) {
    return { Items: [row(), FAILED], LastSentAt: hoursAgo(1), LastEventAt: hoursAgo(1), StartedEmpty: false, Next: null, ...overrides };
}

const FILTERS = { problemsOnly: false, destinationId: '', eventKey: '' };
const CATALOG = {
    EventKeys: ['media.movie', 'media.episodes', 'security.login_failed', 'server.started'],
    Groups: ['media', 'security', 'server'],
};
const DESTS = [{ Id: 'd1', Title: 'Media channel' }, { Id: 'd2', Title: 'Admin alerts' }];
const render = (page, filters = FILTERS, view = {}) => log.renderLog(page, filters, EN, 'en', { now: NOW, destinations: DESTS, catalog: CATALOG, ...view });

/** The table's rows (not the expansions) by data-tgj-row: id → the row's cells' inner HTML. */
function rows(html) {
    return new Map([...html.matchAll(/<tr[^>]*data-tgj-row="([^"]*)"[^>]*>([\s\S]*?)<\/tr>/g)]
        .map(([, id, body]) => [id, [...body.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/g)].map(c => c[1])]));
}

const COLUMNS = ['When', 'Event', 'Destination', 'Result', 'Why'];

// --- the controller on a fake view ---

const STATUS = {
    Version: '1.0.4.0', BuildTarget: '10.11', Compatible: true, InstanceOwner: true, Revision: 5, LoadWarnings: [], LastErrors: [],
    ConnectionState: 'Connected', ConnectionMessage: 'Connected to Telegram', BotUsername: 'jf_bot', HasToken: true,
    Counters: { DroppedIntake: 0, DroppedOutbox: 0, Evictions: 0 }, OutboxCount: 0, UnhealthyDestinations: 0, PendingMedia: 0,
    UpdateCheckFailing: null, RemovedLibraryFilters: 0, Notices: [],
};
const SETTINGS = {
    Revision: 5, PublicUrl: '', PublicUrlValid: true, PublicUrlSuggestion: '',
    Message: { PerMessage: [], AnnounceUpgrades: false },
    Batching: { QuietWindowSeconds: 120, MaxHoldMinutes: 30, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 },
    UpdateCheck: { Enabled: true, Fixes: true, NewReleases: true, NewMajor: false },
    ProxyMode: 'System', ProxyUrl: '', HasProxyUsername: false, HasProxyPassword: false,
};
const DESTINATIONS = {
    Revision: 5, Items: [], DetectedChats: [], ManualUnlocked: false,
    Catalog: { ...CATALOG, Presets: { Everything: [], NewMedia: [], AdminAlerts: [] } },
};

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

function mount(routes = {}, { hash = '#/configurationpage?name=TgJelly&tgj=log' } = {}) {
    const timers = fakeTimers();
    const api = makeApiClient({
        'GET TgJelly/Strings': EN,
        'GET TgJelly/Status': STATUS,
        'GET TgJelly/Links': {},
        'GET TgJelly/Settings': SETTINGS,
        'GET TgJelly/Destinations': DESTINATIONS,
        'GET TgJelly/Libraries': [],
        'GET TgJelly/Templates': { Revision: 5, Items: [] },
        'GET TgJelly/Log': dto(),
        ...routes,
    });
    const doc = { hidden: false, documentElement: { lang: 'en' } };
    const location = { hash };
    const history = {
        state: null,
        replaceState(state, title, url) {
            location.hash = String(url);
        },
    };
    globalThis.window = { ApiClient: api, location, history };
    globalThis.document = doc;
    globalThis.setInterval = timers.setInterval;
    globalThis.clearInterval = timers.clearInterval;
    globalThis.setTimeout = timers.setTimeout;
    globalThis.clearTimeout = timers.clearTimeout;

    const view = makeView(['tgjelly-root']);
    page(view, {});
    return { view, api, timers, el: id => view.elements.get(id) };
}

async function show(view) {
    view.dispatchEvent(new Event('viewshow'));
    await flush();
}

const logCalls = api => api.calls.filter(c => c.type === 'GET' && c.url.startsWith('/jf/TgJelly/Log'));
const query = call => new URLSearchParams(call.url.split('?')[1] ?? '');

// --- tests ---

test('log_navAfterAdvanced', () => {
    assert.deepEqual(m.SECTIONS, ['overview', 'destinations', 'messages', 'advanced', 'log']);
    assert.equal(EN['web.nav.log'], 'Log');
    const html = m.renderShell(EN, { active: 'log', connection: { tone: 'good', text: 'Connected' } });
    const nav = [...html.matchAll(/data-tgj-nav="(\w+)"/g)].map(x => x[1]);
    assert.deepEqual(nav, m.SECTIONS, 'Log is the last sidebar item (the last tab on a phone)');
    assert.ok(html.includes('<span class="tgj-nav-label">Log</span>'));
    assert.match(html, /<section id="tgj-sec-log" data-tgj-section="log" aria-labelledby="tgj-nav-log"><div id="tgj-log"><\/div><\/section>/);
    assert.equal(m.sectionFromHash('#/configurationpage?name=TgJelly&tgj=log'), 'log');
});

test('log_sentRow_hasEmptyWhy', () => {
    const skipped = row({ Id: 'skip-1', Result: 'Skipped', DestinationId: null, DestinationTitle: null, Details: ['No destination has Movies switched on'] });
    const html = render(dto({ Items: [row(), skipped] }));
    const cells = rows(html);
    assert.equal(cells.get('media.movie|d1|1')[4], '', 'Sent has an empty Why');
    assert.ok(cells.get('media.movie|d1|1')[3].includes(ui.label(EN['web.log.result.Sent'], 'good')));
    assert.ok(!cells.get('media.movie|d1|1')[3].includes('data-tgj-act'), 'a plain Sent row has nothing to expand');
    assert.equal(cells.get('skip-1')[4], 'No destination has Movies switched on');

    // The tones of design §8.1, as labels: a word in an outline, no symbol.
    const tones = { Sent: 'good', Waiting: 'progress', Skipped: 'neutral', SentLate: 'attention', SentWithBuiltIn: 'attention', SentWithoutPhoto: 'attention', Failed: 'bad' };
    const words = { Sent: 'Sent', Waiting: 'Waiting', Skipped: 'Skipped', SentLate: 'Sent late', SentWithBuiltIn: 'Sent with built-in', SentWithoutPhoto: 'Sent without photo', Failed: 'Failed' };
    for (const [result, tone] of Object.entries(tones)) {
        assert.equal(EN[`web.log.result.${result}`], words[result]);
        const one = render(dto({ Items: [row({ Id: 'x', Result: result, Details: result === 'Sent' ? [] : ['why'] })] }));
        assert.ok(one.includes(ui.label(words[result], tone)), result);
    }

    // Several details are joined, most serious first, as the server ordered them.
    const two = render(dto({ Items: [row({ Id: 'x', Result: 'SentWithBuiltIn', Details: ['Your edited template failed', 'Sent on try 3'] })] }));
    assert.equal(rows(two).get('x')[4], 'Your edited template failed; Sent on try 3');
});

test('log_columnIsDestination', () => {
    const none = row({ Id: 'none', Result: 'Skipped', DestinationId: null, DestinationTitle: null, Details: ['No destination wants it'] });
    const html = render(dto({ Items: [row(), none] }));
    const heads = [...html.matchAll(/<th[^>]*>([^<]*)<\/th>/g)].map(x => x[1]);
    assert.deepEqual(heads, COLUMNS);
    assert.deepEqual(COLUMNS.map((_, i) => EN[`web.log.col.${['when', 'event', 'destination', 'result', 'why'][i]}`]), COLUMNS);
    assert.ok(!/>Chat</.test(html), 'the column is Destination, not Chat');

    const cells = rows(html);
    assert.ok(cells.get('media.movie|d1|1')[2].includes('Media channel'));
    assert.match(cells.get('none')[2], /—/, 'a dash when no destination was involved');

    // The Event cell: the title, and under it the message's name, never a raw key.
    assert.ok(cells.get('media.movie|d1|1')[1].includes('Dune: Part Two'));
    assert.ok(cells.get('media.movie|d1|1')[1].includes(EN['web.event.media.movie']));
    for (const key of ['server.summary', 'media', 'media.digest', 'security.lockout', 'server.task_failed']) {
        const cell = rows(render(dto({ Items: [row({ Id: 'k', EventKey: key })] }))).get('k')[1];
        const name = /<span class="tgj-log-kind">([^<]*)<\/span>/.exec(cell)[1];
        assert.ok(name && !name.includes(key) && !name.includes('web.'), `${key} → ${name}`);
    }
});

test('log_timeTitle_isFullDateTime', () => {
    const today = row({ Id: 'today', At: new Date(2026, 9, 24, 13, 47, 9).toISOString() });
    const older = row({ Id: 'older', At: new Date(2026, 9, 22, 9, 5, 30).toISOString() });
    const cells = rows(render(dto({ Items: [today, older] })));
    const when = cell => {
        const match = /<time datetime="([^"]+)" title="([^"]+)">([^<]+)<\/time>/.exec(cell);
        assert.ok(match, cell);
        return { datetime: match[1], title: match[2], shown: match[3] };
    };

    const a = when(cells.get('today')[0]);
    assert.equal(a.datetime, today.At);
    assert.match(a.shown, /^1:47\sPM$|^13:47$/, 'today: the time alone');
    assert.match(a.title, /2026/, 'the full date');
    assert.match(a.title, /October/);
    assert.match(a.title, /1:47:09|13:47:09/, 'the time with seconds');

    const b = when(cells.get('older')[0]);
    assert.match(b.shown, /Oct 22/, 'another day: the date too');
    assert.match(b.shown, /9:05|09:05/);
    assert.match(b.title, /October 22, 2026/);
    assert.match(b.title, /9:05:30|09:05:30/);
});

test('log_failedRow_expandsWithTelegramWords', async () => {
    const { view, el } = mount();
    await show(view);
    const before = el('tgj-log').innerHTML;
    assert.ok(before.includes('Dune: Part Two'), before);
    assert.ok(!before.includes('bot was kicked'), 'collapsed at first');
    assert.match(before, /<button type="button" class="tgj-log-toggle"[^>]*data-tgj-act="logToggle"[^>]*data-tgj-id="alert-7"[^>]*aria-expanded="false"/);

    clickOn(view, { 'data-tgj-act': 'logToggle', 'data-tgj-id': 'alert-7', 'data-tgj-instant': '' });
    await flush();
    const html = el('tgj-log').innerHTML;
    assert.match(html, /data-tgj-id="alert-7"[^>]*aria-expanded="true"/);
    assert.ok(html.includes('<code>403 Forbidden: bot was kicked from the supergroup chat</code>'), 'Telegram\'s words, verbatim');
    assert.ok(html.includes(m.text(EN, 'web.log.detail.tries.one', { count: 1 })), 'the tries');
    assert.ok(html.includes(EN['web.log.detail.noRetry']), 'a Failed row is not tried again');
    assert.ok(html.includes('Add the bot back to Admin alerts, then press Check again on the destination.'), 'what to do');

    // A retrying row says when it tries next.
    const retry = render(dto({ Items: [row({ Id: 'r', Result: 'Waiting', Details: ['Couldn\'t reach Telegram, trying again'], Telegram: 'connection timed out', Tries: 2, RetryAt: new Date(2026, 9, 24, 15, 2, 0).toISOString(), Advice: 'Check the proxy.' })] }), FILTERS, { expanded: new Set(['r']) });
    assert.ok(retry.includes(m.text(EN, 'web.log.detail.tries.other', { count: 2 })), retry);
    assert.match(retry, /Next try at (3:02\sPM|15:02)/);

    // Clicking again closes it.
    clickOn(view, { 'data-tgj-act': 'logToggle', 'data-tgj-id': 'alert-7', 'data-tgj-instant': '' });
    await flush();
    assert.ok(!el('tgj-log').innerHTML.includes('bot was kicked'));
});

test('log_problemsOnly_requestsFilter', async () => {
    const { view, api, el } = mount();
    await show(view);
    assert.equal(logCalls(api).length, 1);
    const first = query(logCalls(api)[0]);
    assert.equal(first.get('problemsOnly'), 'false');
    assert.ok(!first.has('before'));

    const html = el('tgj-log').innerHTML;
    assert.match(html, /<input type="radio"[^>]*data-tgj-input="logShow"[^>]*value="all"[^>]*checked/);
    assert.match(html, /<input type="radio"[^>]*data-tgj-input="logShow"[^>]*value="problems"/);
    assert.ok(html.includes(`>${EN['web.log.filter.all']}<`) && html.includes(`>${EN['web.log.filter.problems']}<`));

    dispatchOn(view, 'change', { 'data-tgj-input': 'logShow' }, { value: 'problems' });
    await flush();
    const second = query(logCalls(api).at(-1));
    assert.equal(second.get('problemsOnly'), 'true');
    assert.match(el('tgj-log').innerHTML, /data-tgj-input="logShow"[^>]*value="problems"[^>]*checked/);

    dispatchOn(view, 'change', { 'data-tgj-input': 'logDestination' }, { value: 'd2' });
    await flush();
    const third = query(logCalls(api).at(-1));
    assert.equal(third.get('problemsOnly'), 'true');
    assert.equal(third.get('destinationId'), 'd2');

    dispatchOn(view, 'change', { 'data-tgj-input': 'logEvent' }, { value: 'security.login_failed' });
    await flush();
    assert.equal(query(logCalls(api).at(-1)).get('eventKey'), 'security.login_failed');

    // The selects list the destinations and the events by their names; a destination removed since (none here are
    // configured) keeps the name its rows carry.
    const now = el('tgj-log').innerHTML;
    assert.match(now, /<option value="d2" selected>Admin alerts<\/option>/);
    const listed = render(dto(), { ...FILTERS, destinationId: 'd1' });
    assert.match(listed, /<option value="">All destinations<\/option><option value="d1" selected>Media channel<\/option><option value="d2">Admin alerts<\/option><\/select>/);
    assert.match(now, /<option value="security.login_failed" selected>Failed logins<\/option>/);
    assert.ok(now.includes(`<option value="">${EN['web.log.destination.all']}</option>`));
});

test('log_refresh_keepsExpandedRows', async () => {
    let current = dto();
    const { view, api, el, timers } = mount({ 'GET TgJelly/Log': () => current });
    await show(view);
    clickOn(view, { 'data-tgj-act': 'logToggle', 'data-tgj-id': 'alert-7', 'data-tgj-instant': '' });
    await flush();
    const calls = logCalls(api).length;

    current = dto({ Items: [row({ Id: 'new', Title: 'Severance, S02E10' }), row(), FAILED] });
    timers.tick();
    await flush();
    assert.equal(logCalls(api).length, calls + 1, 'the status poll reloads the Log while it is shown');
    const html = el('tgj-log').innerHTML;
    assert.ok(html.includes('Severance, S02E10'), 'the new row');
    assert.ok(html.includes('bot was kicked'), 'the expanded row stays open');

    // Unchanged data writes nothing: a button the admin is about to press stays where it is.
    const writes = el('tgj-log').writes;
    timers.tick();
    await flush();
    assert.equal(el('tgj-log').writes, writes);

    // Another section: the poll leaves the Log alone.
    clickOn(view, { 'data-tgj-nav': 'overview' });
    const hidden = logCalls(api).length;
    timers.tick();
    await flush();
    assert.equal(logCalls(api).length, hidden, 'no Log request while the Log is not shown');

    // Back on the Log: it loads at once.
    clickOn(view, { 'data-tgj-nav': 'log' });
    await flush();
    assert.equal(logCalls(api).length, hidden + 1);
});

test('log_refresh_onlyLoadsWhenShown', async () => {
    const { view, api } = mount({}, { hash: '#/configurationpage?name=TgJelly&tgj=overview' });
    await show(view);
    assert.equal(logCalls(api).length, 0, 'opening the page on another section does not load the Log');
});

test('log_quietLine_whenNoEventsFor24h', () => {
    const busy = render(dto());
    assert.match(busy, new RegExp(m.text(EN, 'web.log.lastSent', { time: 'XX' }).replace('XX', '<time[^>]*>[^<]+</time>')));
    assert.ok(!busy.includes('No events from Jellyfin'), 'no quiet line while events arrive');
    assert.ok(!busy.includes(EN['web.log.startedEmpty']));

    const quiet = render(dto({ LastEventAt: hoursAgo(25), LastSentAt: hoursAgo(25) }));
    assert.match(quiet, /No events from Jellyfin since <time[^>]*>[^<]+<\/time>/);

    const empty = render(dto({ Items: [], LastEventAt: null, LastSentAt: null, StartedEmpty: true }));
    assert.ok(empty.includes(EN['web.log.lastSentNone']), empty);
    assert.ok(empty.includes(EN['web.log.quietSinceStart']));
    assert.ok(empty.includes(EN['web.log.startedEmpty']), 'says the log started empty');
    assert.ok(empty.includes(EN['web.log.empty']));

    const week = render(dto({ Items: [], LastEventAt: null, LastSentAt: null }));
    assert.ok(week.includes(EN['web.log.quietWeek']));

    // The lead states the retention.
    assert.equal(EN['web.log.lead'], 'Every event TgJelly received, and what happened to it, for the last 7 days.');
    assert.ok(busy.includes(EN['web.log.lead']));
});

test('log_showOlder_pages', async () => {
    const older = row({ Id: 'old-1', Title: 'Blade Runner 2049', At: hoursAgo(30) });
    const { view, api, el, timers } = mount({
        'GET TgJelly/Log': request => (new URLSearchParams(request.url.split('?')[1]).has('before')
            ? dto({ Items: [older], Next: null })
            : dto({ Next: '638650000000000000:alert-7' })),
    });
    await show(view);
    const html = el('tgj-log').innerHTML;
    assert.ok(html.includes(ui.actionButton({ label: EN['web.log.older'], act: 'logOlder' })), html);

    clickOn(view, { 'data-tgj-act': 'logOlder' });
    await flush();
    const call = logCalls(api).at(-1);
    assert.equal(query(call).get('before'), '638650000000000000:alert-7', 'the server\'s Next, passed back unchanged');
    const after = el('tgj-log').innerHTML;
    assert.deepEqual([...rows(after).keys()], ['media.movie|d1|1', 'alert-7', 'old-1'], 'older rows go under the others');
    assert.ok(!after.includes('data-tgj-act="logOlder"'), 'no Show older when there is nothing older');

    // A refresh keeps the older rows loaded.
    timers.tick();
    await flush();
    const refresh = logCalls(api).at(-1);
    assert.ok(!query(refresh).has('before'));
    assert.deepEqual([...rows(el('tgj-log').innerHTML).keys()], ['media.movie|d1|1', 'alert-7', 'old-1']);
});

test('log_mergeKeepsOlderPages', () => {
    const a = row({ Id: 'a' });
    const b = row({ Id: 'b' });
    const c = row({ Id: 'c' });
    const d = row({ Id: 'd' });
    const held = { Items: [a, b, c, d], Next: 'n2' };
    // Two rows arrived: the newest page (as long as what was held) ends at c, so d stays, with the held cursor.
    const fresh = { Items: [row({ Id: 'y' }), row({ Id: 'x' }), a, b], Next: 'n1' };
    assert.deepEqual(log.mergeLog(held, fresh).Items.map(r => r.Id), ['y', 'x', 'a', 'b', 'c', 'd']);
    assert.equal(log.mergeLog(held, fresh).Next, 'n2');
    // Everything fits in one page: the page is the whole log.
    assert.deepEqual(log.mergeLog(held, { Items: [a, b], Next: null }).Items.map(r => r.Id), ['a', 'b']);
    assert.deepEqual(log.mergeLog(null, fresh), fresh);
});

test('log_noSummaryCounts', () => {
    const html = render(dto());
    assert.ok(!/\d+\s+(sent|skipped|waiting|failed)/i.test(html), 'no counts of results');
    assert.ok(!/Last 24 h/i.test(html));
    assert.ok(!Object.keys(EN).some(k => /^web\.log\.(summary|count)/.test(k)));
    assert.doesNotMatch(html, SYMBOLS);
    for (const key of Object.keys(EN).filter(k => k.startsWith('web.log.') || k === 'web.nav.log')) {
        assert.doesNotMatch(EN[key], SYMBOLS, key);
    }
});

test('log_escapesServerText', () => {
    const html = render(dto({ Items: [row({ Id: '<x>', Title: '<b>t</b>', DestinationTitle: '<i>d</i>', Result: 'Failed', Details: ['<s>'], Telegram: '<u>', Advice: '<a>' })] }), FILTERS, { expanded: new Set(['<x>']) });
    for (const raw of ['<b>t</b>', '<i>d</i>', '<s>', '<u>', '<a>', 'data-tgj-row="<x>"']) {
        assert.ok(!html.includes(raw), raw);
    }
});

test('log_loadFailure_saysWhatHappened', async () => {
    const { view, el } = mount({ 'GET TgJelly/Log': { status: 500, body: { Message: 'The log file is locked' } } });
    const errors = [];
    const original = console.error;
    console.error = (...args) => errors.push(args);
    try {
        await show(view);
    } finally {
        console.error = original;
    }

    const html = el('tgj-log').innerHTML;
    assert.ok(html.includes('The log file is locked') && html.includes('500'), html);
    assert.equal(errors.length, 1);
});
