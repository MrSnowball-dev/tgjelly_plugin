// The Messages section (plan 9 Task 8, design §6.1-6.2): one page per message, with its switches, its template and the
// preview. Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import * as msg from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-messages.js';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import { makeView, makeApiClient, fakeTimers, flush, dispatchOn, liveFields } from './fake-view.mjs';

const EN = JSON.parse(readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Strings/en.json', import.meta.url), 'utf8'));

const ALL = ['ShowPoster', 'ShowOverview', 'ShowRatingGenres', 'ShowOpenInJellyfin', 'ShowProviderLinks'];
// What MessageSwitches.Applicable gives for these keys (the server sends it with each template item).
const APPLICABLE = {
    'media.movie': ALL,
    'media.episodes': ['ShowPoster', 'ShowOpenInJellyfin', 'ShowProviderLinks'],
    'security.lockout': ['ShowPoster'],
    'security.login_failed': ['ShowPoster'],
    'server.started': ['ShowPoster'],
    'media.digest': [],
};
const KEYS = Object.keys(APPLICABLE);
const groupOf = key => key.split('.')[0];

function item(EventKey, overrides = {}) {
    return {
        EventKey, Label: `Label of ${EventKey}`, Media: groupOf(EventKey) === 'media' && EventKey !== 'media.digest',
        Text: `Text of ${EventKey}`, BuiltIn: `Text of ${EventKey}`, Edited: false,
        Fields: [], SampleFull: {}, SampleSparse: {}, Flag: null, ApplicableSwitches: APPLICABLE[EventKey], ...overrides,
    };
}

function templates(overrides = {}) {
    return { Revision: 5, Items: KEYS.map(key => item(key)), ...overrides };
}

const off = Object.fromEntries(ALL.map(name => [name, false]));
const on = Object.fromEntries(ALL.map(name => [name, true]));

function settings(overrides = {}) {
    return {
        Revision: 5,
        PublicUrl: 'https://jf.example.com',
        PublicUrlValid: true,
        PublicUrlSuggestion: '',
        Message: { PerMessage: KEYS.map(EventKey => ({ EventKey, Switches: groupOf(EventKey) === 'media' ? { ...on } : { ...off } })), AnnounceUpgrades: false },
        Batching: { QuietWindowSeconds: 120, MaxHoldMinutes: 30, ReleaseQuietSeconds: 20, DigestThreshold: 10, LateEditHours: 6, ReadyTimeoutMinutes: 15 },
        UpdateCheck: { Enabled: true, Fixes: true, NewReleases: true, NewMajor: false },
        ProxyMode: 'System',
        ProxyUrl: '',
        HasProxyUsername: false,
        HasProxyPassword: false,
        ...overrides,
    };
}

/** Settings with one message's switches changed. */
function withSwitches(key, switches, overrides = {}) {
    const base = settings(overrides);
    base.Message.PerMessage = base.Message.PerMessage.map(e => (e.EventKey === key ? { EventKey: key, Switches: { ...e.Switches, ...switches } } : e));
    return base;
}

/** PUT Settings that merges the Message change by event key, switch by switch, as the server does. */
function echoSettings() {
    let current = settings();
    return request => {
        const body = JSON.parse(request.data);
        const sent = body.Message ?? {};
        current = {
            ...current,
            Revision: current.Revision + 1,
            PublicUrl: body.PublicUrl,
            Message: {
                PerMessage: current.Message.PerMessage.map(e => {
                    const change = (sent.PerMessage ?? []).find(c => c.EventKey === e.EventKey);
                    return change ? { EventKey: e.EventKey, Switches: { ...e.Switches, ...change.Switches } } : e;
                }),
                AnnounceUpgrades: sent.AnnounceUpgrades ?? current.Message.AnnounceUpgrades,
            },
        };
        return current;
    };
}

const STATUS = {
    Version: '1.0.4.0', BuildTarget: '10.11', Compatible: true, InstanceOwner: true, Revision: 5, LoadWarnings: [], LastErrors: [],
    ConnectionState: 'Connected', ConnectionMessage: 'Connected to Telegram', BotUsername: 'jf_bot', HasToken: true,
    Counters: { DroppedIntake: 0, DroppedOutbox: 0, Evictions: 0 }, OutboxCount: 0, UnhealthyDestinations: 0, PendingMedia: 0,
    UpdateCheckFailing: null, RemovedLibraryFilters: 0, Notices: [],
};
const LINKS = { OpenBot: 'https://t.me/jf_bot?start=setup', StartAdd: 'https://t.me/jf_bot?start=add', StartGroup: 'https://t.me/jf_bot?startgroup=add', StartChannel: 'https://t.me/jf_bot?startchannel' };
const DESTINATIONS = {
    Revision: 5, Items: [], DetectedChats: [], ManualUnlocked: false,
    Catalog: { EventKeys: KEYS.filter(k => k !== 'media.digest'), Groups: ['media', 'security', 'server'], Presets: { Everything: [], NewMedia: [], AdminAlerts: [] } },
};
const VALIDATION = { Errors: [], Warnings: [], Preview: '', Length: 10, Limit: 1024, Html: 'ok', Caption: true, Poster: false, Buttons: [] };

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

/** The page on a fake view, opened on the Messages section (and the message the hash names, if any). */
function mount(routes = {}, { hash = '#/configurationpage?name=TgJelly&tgj=messages' } = {}) {
    const timers = fakeTimers();
    const api = makeApiClient({
        'GET TgJelly/Strings': EN,
        'GET TgJelly/Status': STATUS,
        'GET TgJelly/Links': LINKS,
        'GET TgJelly/Settings': settings(),
        'GET TgJelly/Destinations': DESTINATIONS,
        'GET TgJelly/Libraries': [],
        'GET TgJelly/Templates': templates(),
        'POST TgJelly/Templates/Validate': VALIDATION,
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

    const view = makeView(['tgjelly-root']);
    const hooks = page(view, {});
    return { view, api, timers, doc, hooks, win: globalThis.window, el: id => view.elements.get(id) };
}

async function show(view) {
    view.dispatchEvent(new Event('viewshow'));
    await flush();
}


test('messages_templatesLoadFailure_carriesTheServerMessage', async t => {
    t.mock.method(console, 'error', () => {});
    const { view, el } = mount({ 'GET TgJelly/Templates': { status: 500, body: { Message: 'The templates file is locked' } } });
    await show(view);

    const html = el('tgj-messages').innerHTML;
    assert.ok(html.includes('HTTP 500') && html.includes('The templates file is locked'), html);
    assert.ok(/Copy diagnostics/.test(html), 'says what to do if it keeps failing');
});

/** The Messages section as the controller renders it: the state's data, the selected key and no validation yet. */
function render(key, { tpl = templates(), set = settings() } = {}) {
    const forms = { settings: { base: m.settingsFormData(set), local: m.settingsFormData(set), dirty: false, changed: [] } };
    return msg.renderMessagesSection({ templates: tpl, settings: set, forms }, EN, { key, destinations: [], lang: 'en' });
}

const selectHtml = (html, input) => (new RegExp(`<select[^>]*data-tgj-input="${input}"[^>]*>[\\s\\S]*?</select>`).exec(html) ?? [''])[0];
const options = select => [...select.matchAll(/<option value="([^"]*)"( selected)?>([^<]*)<\/option>/g)].map(([, value, selected, label]) => ({ value, selected: Boolean(selected), label }));
/** The switches in markup: [eventKey, switch name] of each msgSwitch button, in order. */
const switchesIn = html => [...html.matchAll(/<button[^>]*role="switch"[^>]*data-tgj-input="msgSwitch"[^>]*>/g)]
    .map(([tag]) => [/data-tgj-id="([^"]+)"/.exec(tag)[1], /data-tgj-key="([^"]+)"/.exec(tag)[1]]);
const putSettings = api => api.calls.filter(c => c.type === 'PUT' && c.url === '/jf/TgJelly/Settings').map(c => JSON.parse(c.data));
const cue = (view, key) => ({ className: 'tgj-cue', innerHTML: '', ...view.cues.get(key) });
const SAVED_CUE = { className: 'tgj-cue tgj-cue--saved', innerHTML: ui.label(EN['web.autosave.saved'], 'good') };
const IDLE_CUE = { className: 'tgj-cue', innerHTML: '' };
const errorCue = reason => ({
    className: 'tgj-cue tgj-cue--error',
    innerHTML: `${ui.label(EN['web.autosave.notSaved'], 'bad')} <span class="tgj-cue-reason">${ui.esc(reason)}</span>`,
});
const switchAttrs = (key, name) => ({
    'data-tgj-input': 'msgSwitch', 'data-tgj-id': key, 'data-tgj-key': name,
    'data-tgj-field': `Message.Switches.${key}.${name}`, 'data-tgj-save': `msg:${key}:${name}`, role: 'switch',
});
const URL_ATTRS = { 'data-tgj-input': 'publicUrl', 'data-tgj-field': 'PublicUrl', 'data-tgj-save': 'PublicUrl' };
const leave = (view, attrs) => dispatchOn(view, 'focusout', attrs, {}, { relatedTarget: null });
const messagesEl = el => el('tgj-messages').innerHTML;

test('nav_hasNoTemplatesItem', () => {
    assert.deepEqual(m.SECTIONS, ['overview', 'destinations', 'messages', 'advanced', 'log']);
    const html = m.renderShell(EN, { active: 'messages', connection: { tone: 'good', text: 'Connected' } });
    assert.ok(!html.includes('data-tgj-nav="templates"'), 'no Templates item in the sidebar');
    assert.ok(!html.includes('id="tgj-templates"'), 'no templates host');
    assert.ok(html.includes('data-tgj-nav="messages"'));
    assert.ok(!('web.nav.templates' in EN));
    // An old link to the Templates section opens Messages, where the templates are now.
    assert.equal(m.sectionFromHash('#/configurationpage?name=TgJelly&tgj=templates'), 'messages');
});

test('messages_pickers_sectionThenMessage', () => {
    const html = render('security.lockout');
    const section = selectHtml(html, 'msgSection');
    const message = selectHtml(html, 'msgKey');
    assert.ok(section && message, html);
    assert.ok(html.indexOf(section) < html.indexOf(message), 'the Section picker comes first');
    assert.deepEqual(options(section), [
        { value: 'media', selected: false, label: EN['web.event.group.media'] },
        { value: 'security', selected: true, label: EN['web.event.group.security'] },
        { value: 'server', selected: false, label: EN['web.event.group.server'] },
    ]);
    assert.deepEqual(options(message).map(o => [o.value, o.selected, o.label]), [
        ['security.lockout', true, EN['web.event.security.lockout']],
        ['security.login_failed', false, EN['web.event.security.login_failed']],
    ], "only that section's messages, by their dashboard names");

    // Under Media, the Digest is one of the messages.
    const media = options(selectHtml(render('media.episodes'), 'msgKey'));
    assert.deepEqual(media.map(o => o.value), ['media.movie', 'media.episodes', 'media.digest']);
    assert.equal(media.find(o => o.value === 'media.digest').label, 'Digest');
    assert.ok(media.find(o => o.value === 'media.episodes').selected);

    // The cards follow the pickers: What it shows, the template, then the preview.
    const body = render('media.movie');
    const order = ['data-tgj-input="msgKey"', 'data-tgj-input="msgSwitch"', '<textarea', 'id="tgj-tpl-preview"'].map(s => body.indexOf(s));
    assert.ok(order.every((at, i) => at >= 0 && (i === 0 || order[i - 1] < at)), JSON.stringify(order));
});

test('messages_pickerOptions_haveNoEditedSuffix', () => {
    const tpl = templates();
    tpl.Items = tpl.Items.map(i => (i.EventKey === 'media.episodes' ? { ...i, Edited: true, Text: 'mine' } : i));
    const html = render('media.episodes', { tpl });
    const labels = options(selectHtml(html, 'msgKey')).map(o => o.label);
    assert.deepEqual(labels, [EN['web.event.media.movie'], EN['web.event.media.episodes'], 'Digest']);
    assert.ok(labels.every(l => !l.includes(EN['web.templates.edited'])), 'no (Edited) in any option');
    assert.ok(!('web.templates.eventEdited' in EN));

    // The state is a label beside the pickers instead.
    const pickers = html.slice(0, html.indexOf('data-tgj-input="msgSwitch"'));
    assert.ok(pickers.includes(ui.label(EN['web.templates.edited'], 'accent')), pickers);
    const builtIn = render('media.movie', { tpl });
    assert.ok(builtIn.slice(0, builtIn.indexOf('data-tgj-input="msgSwitch"')).includes(ui.label(EN['web.templates.builtIn'], 'neutral')));
});

test('messages_selectionPersistsInHash', async () => {
    assert.equal(msg.messageFromHash('#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=server.started'), 'server.started');
    assert.equal(msg.messageFromHash('#tgjmsg=media.digest'), 'media.digest');
    assert.equal(msg.messageFromHash('#/configurationpage?name=TgJelly&tgj=messages'), null);
    assert.equal(msg.messageFromHash('#tgjmsg=%E0%A4%A'), null, 'a malformed escape');
    assert.equal(msg.messageFromHash(undefined), null);
    assert.equal(msg.hashWithMessage('#/configurationpage?name=TgJelly&tgj=messages', 'media.movie'), '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=media.movie');
    assert.equal(msg.hashWithMessage('#/configurationpage?name=TgJelly&tgjmsg=media.movie&tgj=messages', 'server.started'), '#/configurationpage?name=TgJelly&tgjmsg=server.started&tgj=messages');
    assert.equal(msg.hashWithMessage('', 'media.movie'), '#tgjmsg=media.movie');
    assert.equal(m.sectionFromHash('#/configurationpage?name=TgJelly&tgjmsg=x&tgj=messages'), 'messages', 'the two parameters stay apart');

    // Opened from a link: that message is shown.
    const { view, el, win, api } = mount({}, { hash: '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=security.lockout' });
    await show(view);
    assert.ok(options(selectHtml(messagesEl(el), 'msgKey')).find(o => o.value === 'security.lockout').selected);
    assert.ok(messagesEl(el).includes('>Text of security.lockout</textarea>'));

    // Picking another message records it in the hash, beside the section, without a history entry.
    dispatchOn(view, 'change', { 'data-tgj-input': 'msgKey' }, { value: 'security.login_failed' });
    await flush();
    assert.equal(win.location.hash, '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=security.login_failed');
    assert.deepEqual(win.history.state, { idx: 3 });
    assert.ok(messagesEl(el).includes('>Text of security.login_failed</textarea>'));

    // Picking a section shows its first message.
    dispatchOn(view, 'change', { 'data-tgj-input': 'msgSection' }, { value: 'server' });
    await flush();
    assert.equal(win.location.hash, '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=server.started');
    assert.ok(messagesEl(el).includes('>Text of server.started</textarea>'));
    const validated = api.calls.filter(c => c.url.endsWith('/Templates/Validate')).map(c => JSON.parse(c.data).EventKey);
    assert.deepEqual(validated, ['security.lockout', 'security.login_failed', 'server.started'], 'each pick shows its preview at once');

    // A key the server doesn't list falls back to the first media message.
    const unknown = mount({}, { hash: '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=media.nope' });
    await show(unknown.view);
    assert.ok(options(selectHtml(messagesEl(unknown.el), 'msgKey')).find(o => o.value === 'media.movie').selected);
});

test('messages_switches_areApplicableOnly', () => {
    const security = render('security.lockout');
    assert.deepEqual(switchesIn(security), [['security.lockout', 'ShowPoster']], 'Security shows Poster only');
    assert.ok(!security.includes('data-tgj-input="msgAnnounce"'), 'no Announce upgrades for an alert');
    assert.ok(security.includes(m.t(EN, 'web.messages.logoHint')), 'its poster is the TgJelly logo');
    assert.ok(security.includes(`<h2 class="tgj-card-title">${m.t(EN, 'web.messages.shows')}</h2>`));

    assert.deepEqual(switchesIn(render('media.episodes')), APPLICABLE['media.episodes'].map(name => ['media.episodes', name]));
    const movie = render('media.movie');
    assert.deepEqual(switchesIn(movie), ALL.map(name => ['media.movie', name]));
    assert.ok(movie.includes('data-tgj-input="msgAnnounce"'), 'Announce upgrades for a media message');
    assert.ok(movie.includes(m.t(EN, 'web.messages.announceHint')));
    assert.match(EN['web.messages.announceHint'], /applies to all media messages/i);
    for (const name of ALL) {
        assert.ok(movie.includes(`data-tgj-cue="msg:media.movie:${name}"`), `${name} has its cue beside it`);
    }

    // A message no switch changes (the digest) has no What it shows card at all.
    const digest = render('media.digest');
    assert.deepEqual(switchesIn(digest), []);
    assert.ok(!digest.includes(m.t(EN, 'web.messages.shows')));
    assert.ok(!digest.includes('data-tgj-input="msgAnnounce"'));
    assert.ok(digest.includes('<textarea'), 'its template is still there');
});

test('messages_switchSave_sendsSingleEntry', async () => {
    const { view, api, el, hooks } = mount({ 'PUT TgJelly/Settings': echoSettings() }, { hash: '#/configurationpage?name=TgJelly&tgj=messages&tgjmsg=media.episodes' });
    await show(view);
    dispatchOn(view, 'click', switchAttrs('media.episodes', 'ShowPoster'));
    await flush();
    const [first] = putSettings(api);
    assert.deepEqual(first.Message, { PerMessage: [{ EventKey: 'media.episodes', Switches: { ShowPoster: false } }] });
    assert.equal(first.ExpectedRevision, 5);
    assert.deepEqual(cue(view, 'msg:media.episodes:ShowPoster'), SAVED_CUE, 'Saved beside the switch');
    assert.equal(hooks.state.settings.Message.PerMessage.find(e => e.EventKey === 'media.movie').Switches.ShowPoster, true, 'the movie keeps its poster');
    assert.match(messagesEl(el), /aria-checked="false"[^>]*data-tgj-input="msgSwitch" data-tgj-id="media.episodes" data-tgj-key="ShowPoster"/);

    // Announce upgrades is one setting for every media message: it saves alone, with no message entry.
    dispatchOn(view, 'click', { 'data-tgj-input': 'msgAnnounce', 'data-tgj-field': 'Message.AnnounceUpgrades', 'data-tgj-save': 'Message.AnnounceUpgrades', role: 'switch' });
    await flush();
    assert.deepEqual(putSettings(api)[1].Message, { PerMessage: [], AnnounceUpgrades: true });
    assert.equal(putSettings(api)[1].ExpectedRevision, 6);

    // A switch the selected message doesn't have is ignored.
    dispatchOn(view, 'click', switchAttrs('media.episodes', 'ShowOverview'));
    await flush();
    assert.equal(putSettings(api).length, 2);
});

test('publicUrl_onlyWhileOpenInJellyfinOn', async () => {
    const shown = render('media.movie');
    assert.ok(shown.includes('data-tgj-input="publicUrl"'));
    const open = shown.indexOf('data-tgj-key="ShowOpenInJellyfin"');
    const url = shown.indexOf('data-tgj-input="publicUrl"');
    const providers = shown.indexOf('data-tgj-key="ShowProviderLinks"');
    assert.ok(open < url && url < providers, 'nested under Open in Jellyfin');
    assert.ok(shown.includes(m.t(EN, 'web.messages.urlHint')));
    assert.match(EN['web.messages.urlHint'], /every message/i, 'the hint says it is one URL for every message');

    const hidden = render('media.movie', { set: withSwitches('media.movie', { ShowOpenInJellyfin: false }) });
    assert.ok(!hidden.includes('data-tgj-input="publicUrl"'));
    assert.ok(!render('security.lockout').includes('data-tgj-input="publicUrl"'), 'not for a message without the button');

    // Turning the switch off hides the field at once.
    const { view, el } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    assert.ok(messagesEl(el).includes('data-tgj-input="publicUrl"'));
    dispatchOn(view, 'click', switchAttrs('media.movie', 'ShowOpenInJellyfin'));
    await flush();
    assert.ok(!messagesEl(el).includes('data-tgj-input="publicUrl"'));
});

test('publicUrl_stillSavesOnBlurOnly', async () => {
    const { view, api, timers } = mount({ 'PUT TgJelly/Settings': echoSettings() });
    await show(view);
    dispatchOn(view, 'input', URL_ATTRS, { value: 'https://new.example.com' });
    await timers.advance(5000);
    assert.equal(putSettings(api).length, 0, 'typing never sends');

    dispatchOn(view, 'input', URL_ATTRS, { value: 'http://localhost:8096' });
    leave(view, URL_ATTRS);
    await flush();
    assert.equal(putSettings(api).length, 0, 'an invalid URL is not sent');
    assert.deepEqual(cue(view, 'PublicUrl'), errorCue(EN['web.messages.urlInvalid']));

    dispatchOn(view, 'input', URL_ATTRS, { value: 'https://new.example.com' });
    await flush();
    assert.deepEqual(cue(view, 'PublicUrl'), IDLE_CUE);
    dispatchOn(view, 'keydown', URL_ATTRS, { tagName: 'INPUT' }, { key: 'Enter' });
    await flush();
    const [body] = putSettings(api);
    assert.equal(body.PublicUrl, 'https://new.example.com', 'Enter saves');
    assert.deepEqual(body.Message, { PerMessage: [] }, 'and no message switch goes with it');
    assert.deepEqual(cue(view, 'PublicUrl'), SAVED_CUE);
});

test('messages_noBatchingCard', async () => {
    const { view, el } = mount();
    await show(view);
    const html = messagesEl(el);
    assert.ok(html.includes('data-tgj-input="msgKey"'), 'the section rendered');
    assert.ok(!html.includes('data-tgj-input="batching"') && !html.includes(m.t(EN, 'web.batching.title')));
    const section = el('tgjelly-root').innerHTML.match(/<section[^>]*data-tgj-section="messages"[^>]*>([\s\S]*?)<\/section>/)[1];
    assert.deepEqual([...section.matchAll(/id="([^"]+)"/g)].map(x => x[1]), ['tgj-messages'], 'one host: the Messages section');
});

test('advanced_hasBatchingCard', async () => {
    const { view, el } = mount();
    await show(view);
    const advanced = el('tgjelly-root').innerHTML.match(/<section[^>]*data-tgj-section="advanced"[^>]*>([\s\S]*?)<\/section>/)[1];
    const hosts = [...advanced.matchAll(/id="([^"]+)"/g)].map(x => x[1]);
    assert.ok(hosts.includes('tgj-batching'));
    assert.ok(hosts.indexOf('tgj-updates') < hosts.indexOf('tgj-batching'), 'Batching comes after Update checks (design §7)');
    assert.ok(el('tgj-batching').innerHTML.includes(`<h2 class="tgj-card-title">${m.t(EN, 'web.batching.title')}</h2>`));
    assert.ok(el('tgj-batching').innerHTML.includes('data-tgj-input="batching"'));
});

test('the Messages layout keeps the preview beside the cards on wide screens and under them on narrow ones', () => {
    const css = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');
    assert.doesNotMatch(css, /#tgjelly-root \.tgj-msg-side \{[^}]*position: sticky;/, 'Jellyfin breaks sticky; a script places it');
    assert.match(css, /@container tgjelly \(max-width: [\d.]+rem\) \{[^@]*#tgjelly-root \.tgj-msg-layout \{[^}]*grid-template-columns: minmax\(0, 1fr\);/);
    const html = render('media.movie');
    assert.ok(html.startsWith('<div class="tgj-msg-layout">'), html.slice(0, 80));
    assert.ok(html.indexOf('<div id="tgj-msg-cards" class="tgj-msg-cards">') < html.indexOf('<div id="tgj-msg-side" class="tgj-msg-side">'));
    assert.ok(!/[●✓✔✅⚠❌✗🟢🔴]/u.test(html));
});

test('previewOffset keeps the preview under the header and within the cards', () => {
    const base = { headerBottom: 60, cardsHeight: 2000, sideHeight: 500, gap: 16 };
    assert.equal(msg.previewOffset({ ...base, cardsTop: 300 }), 0, 'not scrolled past yet');
    assert.equal(msg.previewOffset({ ...base, cardsTop: -400 }), 476, 'follows the scroll: 60 + 16 + 400');
    assert.equal(msg.previewOffset({ ...base, cardsTop: -5000 }), 1500, 'stops at the end of the cards');
    assert.equal(msg.previewOffset({ ...base, cardsTop: -400, sideHeight: 3000 }), 0, 'a preview taller than the cards stays');
});

// --- the template's errors and length sit under the textarea (Task 8 fix round 1, design §6.1) ---

const FAILED = { ...VALIDATION, Errors: [{ Message: 'Unexpected <end>', Line: 2, Column: 7 }], Warnings: ['careful & slow'], Html: '', Length: 0 };
const GOOD = { ...VALIDATION, Html: '<b>last good</b>', Length: 9, Limit: 1024 };
/** The Messages section with this validation (and the last good one) for the movie. */
const renderWith = (result, good = GOOD) => msg.renderMessagesSection(
    { templates: templates(), settings: null, forms: {} }, EN, { key: 'media.movie', result, good, destinations: [{ Id: 'd1', Title: 'Family' }], lang: 'en' });
/** The inner HTML of the first element with this id in the markup (no nested element of the same tag). */

test('templateCard_showsErrorsAndLength_underTextarea', () => {
    const html = renderWith(FAILED);
    const card = html.slice(html.indexOf('<section class="tgj-card tgj-tpl"'), html.indexOf('</section>', html.indexOf('<section class="tgj-card tgj-tpl"')));
    const textarea = card.indexOf('</textarea>');
    const error = card.indexOf('line 2, column 7: Unexpected &lt;end&gt;');
    const warning = card.indexOf('careful &amp; slow');
    const actions = card.indexOf('data-tgj-act="tplTest"');
    assert.ok(textarea >= 0 && textarea < error && error < actions, card);
    assert.ok(textarea < warning && warning < actions);
    assert.ok(card.includes('id="tgj-tpl-check"'), 'one element, updated in place');

    const ok = renderWith({ ...GOOD, Length: 312 });
    const okCard = ok.slice(ok.indexOf('<section class="tgj-card tgj-tpl"'));
    const length = okCard.indexOf(m.t(EN, 'web.templates.length', { length: '312', limit: '1,024' }));
    assert.ok(okCard.indexOf('</textarea>') < length && length < okCard.indexOf('data-tgj-act="tplTest"'), okCard);
});

test('previewPane_hasOnlyTheBubble', async () => {
    const html = renderWith(FAILED);
    const pane = html.slice(html.indexOf('<div id="tgj-tpl-preview"'));
    assert.ok(/class="tgj-tg-bubble[^"]*tgj-tg-stale/.test(pane), 'the last good message, dimmed');
    assert.ok(pane.includes('<b>last good</b>'));
    assert.ok(!pane.includes('tgj-error') && !pane.includes('careful') && !pane.includes('tgj-tpl-length'), pane);
    const okPane = renderWith(GOOD).slice(html.indexOf('<div id="tgj-tpl-preview"'));
    assert.ok(!okPane.includes(m.t(EN, 'web.templates.length', { length: '9', limit: '1,024' })), 'the length is not in the pane');

    // The controller fills each in place: the bubble in the pane, the errors and length under the template.
    const { view, el, timers } = mount({ 'POST TgJelly/Templates/Validate': request => (JSON.parse(request.data).Text.includes('oops') ? FAILED : GOOD) });
    await show(view);
    dispatchOn(view, 'input', { 'data-tgj-input': 'tplText', 'data-tgj-key': 'media.movie', 'data-tgj-save': 'template:media.movie' }, { value: '{{ oops' });
    await timers.advance(500);
    assert.ok(el('tgj-tpl-check').innerHTML.includes('line 2, column 7: Unexpected &lt;end&gt;'));
    assert.ok(el('tgj-tpl-preview').innerHTML.includes('<b>last good</b>'));
    assert.ok(!el('tgj-tpl-preview').innerHTML.includes('tgj-error'));
});

test('templateErrors_updateWithoutTouchingTextarea', async () => {
    const { view, el, doc, timers } = mount({ 'POST TgJelly/Templates/Validate': request => (JSON.parse(request.data).Text.includes('oops') ? FAILED : GOOD) });
    await show(view);
    const textarea = liveFields(view, doc, 'tgj-messages', [{ tag: 'textarea', attributes: { 'data-tgj-input': 'tplText', 'data-tgj-key': 'media.movie' } }]);
    const typing = textarea();
    typing.focus();
    typing.setSelectionRange(4, 6, 'forward');
    const writes = el('tgj-messages').writes;
    const checkWrites = el('tgj-tpl-check').writes;

    dispatchOn(view, 'input', { 'data-tgj-input': 'tplText', 'data-tgj-key': 'media.movie', 'data-tgj-save': 'template:media.movie' }, { value: 'Hi {{ oops' });
    await timers.advance(500);
    assert.ok(el('tgj-tpl-check').innerHTML.includes('Unexpected &lt;end&gt;'), 'the error shows');
    assert.ok(el('tgj-tpl-check').writes > checkWrites);
    assert.equal(el('tgj-messages').writes, writes, 'the section, and so the textarea, is not written again');
    assert.equal(textarea(), typing, 'the same textarea');
    assert.equal(doc.activeElement, typing, 'it keeps the focus');
    assert.deepEqual([typing.selectionStart, typing.selectionEnd, typing.selectionDirection], [4, 6, 'forward'], 'and the caret');
});

// --- the Fields and Formatting tables, click to insert (Task 9, design §6.3) ---

const TPL_ATTRS = { 'data-tgj-input': 'tplText', 'data-tgj-key': 'media.movie', 'data-tgj-save': 'template:media.movie' };
const fieldItem = () => item('media.movie', {
    Fields: [
        { Name: 'title', Type: 'string', Description: 'The <title>.', Optional: false },
        { Name: 'year', Type: 'int', Description: 'The year.', Optional: true },
        { Name: 'library', Type: 'string', Description: 'The library.', Optional: true },
    ],
    SampleFull: { title: 'Dune', year: 2021, library: '' },
});

/** Runs fn with globalThis.localStorage set to storage (or removed), then puts the old one back. */
async function withStorage(storage, fn) {
    const before = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', { value: storage, configurable: true, writable: true });
    try {
        return await fn();
    } finally {
        if (before) {
            Object.defineProperty(globalThis, 'localStorage', before);
        } else {
            delete globalThis.localStorage;
        }
    }
}

const memoryStorage = (initial = {}) => {
    const data = { ...initial };
    return { data, getItem: k => (k in data ? data[k] : null), setItem: (k, v) => { data[k] = String(v); } };
};
const throwingStorage = () => ({ getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); } });

/** A fake template textarea inside the Template card, with the editing calls the insert uses. */
function wireTextarea(view, doc, el, { value, start = value.length, end = start, execCommand = true }) {
    const ta = {
        value, selectionStart: start, selectionEnd: end, inputs: 0,
        focus() { doc.activeElement = this; },
        setSelectionRange(s, e) { this.selectionStart = s; this.selectionEnd = e; },
        setRangeText(text, s, e, mode) {
            this.value = this.value.slice(0, s) + text + this.value.slice(e);
            if (mode === 'end') {
                this.selectionStart = this.selectionEnd = s + text.length;
            }
        },
        dispatchEvent() { this.inputs++; dispatchOn(view, 'input', TPL_ATTRS, { value: this.value }); },
    };
    doc.execCommand = execCommand
        ? (command, _ui, text) => { assert.equal(command, 'insertText'); ta.setRangeText(text, ta.selectionStart, ta.selectionEnd, 'end'); ta.dispatchEvent(); return true; }
        : undefined;
    el('tgj-tpl-edit').querySelector = selector => (selector === 'textarea' ? ta : null);
    return ta;
}

const clickInsert = (view, kind, id) => dispatchOn(view, 'click', { 'data-tgj-act': 'tplInsert', 'data-tgj-insert': kind, 'data-tgj-id': id, 'data-tgj-instant': '' });
const formatIndex = snippet => msg.FORMATTING.findIndex(f => f.snippet === snippet);

test('fieldsTable_rowsWithDescriptionAndExample', () => {
    const html = msg.renderFieldsTable(fieldItem(), EN);
    for (const head of ['web.tables.field', 'web.tables.what', 'web.tables.example']) {
        assert.ok(html.includes(`<th scope="col">${ui.esc(EN[head])}</th>`), head);
    }

    const rows = [...html.matchAll(/<tr>([\s\S]*?)<\/tr>/g)].map(r => r[1]).filter(r => r.includes('data-tgj-insert'));
    assert.equal(rows.length, 3);
    assert.ok(rows[0].includes('data-tgj-insert="field"') && rows[0].includes('data-tgj-id="title"') && rows[0].includes('{{ title }}'), rows[0]);
    assert.ok(rows[0].includes('The &lt;title&gt;.') && rows[0].includes('Dune'));
    assert.ok(!rows[0].includes(ui.label(EN['web.templates.optional'], 'neutral')), 'title is not optional');
    assert.ok(rows[1].includes(ui.label(EN['web.templates.optional'], 'neutral')) && rows[1].includes('2021'));
    assert.ok(rows[2].includes('tgj-muted'), 'an empty example is a muted dash');
});

test('fieldClick_insertsAtCaret_andSavesOnce', async () => {
    const { view, api, el, doc, timers } = mount({ 'PUT TgJelly/Templates/media.movie': templates({ Revision: 6 }) });
    await show(view);
    const ta = wireTextarea(view, doc, el, { value: 'Hi  there', start: 3 });
    const writes = el('tgj-messages').writes;
    const { event } = clickInsert(view, 'field', 'title');
    await flush();
    assert.equal(ta.value, 'Hi {{ title }} there');
    assert.equal(ta.selectionStart, 'Hi {{ title }}'.length);
    assert.equal(ta.selectionEnd, ta.selectionStart, 'the caret lands after the insert');
    assert.equal(doc.activeElement, ta, 'the focus stays in the textarea');
    assert.equal(el('tgj-messages').writes, writes, 'nothing is re-rendered');
    assert.ok(event.defaultPrevented);
    await timers.advance(1000);
    await flush();
    const puts = api.calls.filter(c => c.type === 'PUT' && c.url === '/jf/TgJelly/Templates/media.movie');
    assert.equal(puts.length, 1, 'exactly one save follows');
    assert.equal(JSON.parse(puts[0].data).Text, 'Hi {{ title }} there');
});

test('fieldClick_withoutExecCommand_usesSetRangeText_andFiresInput', async () => {
    const { view, el, doc } = mount();
    await show(view);
    const ta = wireTextarea(view, doc, el, { value: 'ab', start: 1, execCommand: false });
    clickInsert(view, 'field', 'year');
    await flush();
    assert.equal(ta.value, 'a{{ year }}b');
    assert.equal(ta.inputs, 1);
    assert.equal(doc.activeElement, ta);
});

test('formatClick_wrapsSelection', async () => {
    const { view, el, doc } = mount();
    await show(view);
    const ta = wireTextarea(view, doc, el, { value: 'Hello world', start: 6, end: 11 });
    clickInsert(view, 'format', String(formatIndex('<b>text</b>')));
    await flush();
    assert.equal(ta.value, 'Hello <b>world</b>');
    assert.deepEqual([ta.selectionStart, ta.selectionEnd], [18, 18]);
    assert.equal(doc.activeElement, ta);
});

test('formatClick_noSelection_insertsAndSelectsPlaceholder', async () => {
    const { view, el, doc } = mount();
    await show(view);
    const ta = wireTextarea(view, doc, el, { value: 'Hello', start: 5 });
    clickInsert(view, 'format', String(formatIndex('<b>text</b>')));
    await flush();
    assert.equal(ta.value, 'Hello<b>text</b>');
    assert.equal(ta.value.slice(ta.selectionStart, ta.selectionEnd), 'text');
    assert.equal(doc.activeElement, ta);
});

test('formatting_listsEveryConstruct', () => {
    assert.deepEqual(msg.FORMATTING.map(f => f.snippet), [
        '<b>text</b>', '<i>text</i>', '<u>text</u>', '<s>text</s>', '<tg-spoiler>text</tg-spoiler>', '<a href="https://example.com">text</a>',
        '<code>text</code>', '<pre>text</pre>', '<blockquote>text</blockquote>', '<blockquote expandable>text</blockquote>',
        '{{ if field }}text{{ end }}', '{{ plural n "one" "many" }}', '{{ raw value }}', '{{- field -}}',
    ]);
    // A placeholder sits inside its snippet, and each format has a description.
    for (const f of msg.FORMATTING) {
        assert.ok(f.snippet.includes(f.placeholder), f.snippet);
        assert.ok(EN[f.descriptionKey], f.descriptionKey);
    }
    assert.ok(msg.renderFormattingTable(EN).includes('tgj-tg-'), 'Looks like uses the preview classes');
});

test('tables_collapsedByDefault_rememberState', async () => {
    await withStorage(memoryStorage(), async () => {
        const closed = msg.renderTemplateTables(fieldItem(), EN);
        assert.equal((closed.match(/aria-expanded="false"/g) ?? []).length, 2);
        assert.ok(!closed.includes('aria-expanded="true"'));
        assert.equal((closed.match(/\shidden\b/g) ?? []).length, 2, 'both panels hidden');

        const { view, el } = mount();
        await show(view);
        const attrs = new Map();
        const panel = el('tgj-tbl-fields');
        dispatchOn(view, 'click', { 'data-tgj-act': 'tplTable', 'data-tgj-id': 'fields', 'data-tgj-instant': '' },
            { setAttribute: (k, v) => attrs.set(k, v), getAttribute: k => (k === 'data-tgj-act' ? 'tplTable' : k === 'data-tgj-id' ? 'fields' : k === 'data-tgj-instant' ? '' : attrs.get(k) ?? null) });
        await flush();
        assert.equal(attrs.get('aria-expanded'), 'true');
        assert.equal(panel.hidden, false);
        assert.equal(globalThis.localStorage.getItem('tgj.tables.fields'), '1');
        const reopened = msg.renderTemplateTables(fieldItem(), EN);
        assert.equal((reopened.match(/aria-expanded="true"/g) ?? []).length, 1, 'only Fields is open');
        assert.ok(globalThis.localStorage.getItem('tgj.tables.formatting') === null);
    });
});

test('tables_storageThrows_stillWork', async () => {
    await withStorage(throwingStorage(), async () => {
        assert.equal((msg.renderTemplateTables(fieldItem(), EN).match(/aria-expanded="false"/g) ?? []).length, 2);
        const { view, el, doc } = mount();
        await show(view);
        const attrs = new Map();
        dispatchOn(view, 'click', { 'data-tgj-act': 'tplTable', 'data-tgj-id': 'formatting', 'data-tgj-instant': '' },
            { setAttribute: (k, v) => attrs.set(k, v), getAttribute: k => (k === 'data-tgj-act' ? 'tplTable' : k === 'data-tgj-id' ? 'formatting' : k === 'data-tgj-instant' ? '' : attrs.get(k) ?? null) });
        await flush();
        assert.equal(attrs.get('aria-expanded'), 'true', 'the panel still opens');
        assert.equal(el('tgj-tbl-formatting').hidden, false);
        const ta = wireTextarea(view, doc, el, { value: '', start: 0 });
        clickInsert(view, 'field', 'title');
        await flush();
        assert.equal(ta.value, '{{ title }}');
    });
    await withStorage(undefined, async () => {
        assert.ok(msg.renderTemplateTables(fieldItem(), EN).includes('aria-expanded="false"'));
    });
});

test('insertCell_isKeyboardActivatable', async () => {
    const html = msg.renderTemplateTables(fieldItem(), EN);
    const cells = [...html.matchAll(/<button\b[^>]*data-tgj-insert[^>]*>/g)].map(m2 => m2[0]);
    assert.equal(cells.length, 3 + msg.FORMATTING.length);
    for (const cell of cells) {
        assert.ok(cell.includes('type="button"') && cell.includes('data-tgj-act="tplInsert"'), cell);
        assert.ok(!cell.includes('tabindex') && !cell.includes('disabled'), cell);
    }
    // Each table row starts with its insert cell.
    assert.equal([...html.matchAll(/<tr>\s*<td[^>]*><button\b[^>]*data-tgj-insert/g)].length, cells.length);
    // A press on a cell must not take the focus (and the caret) from the textarea.
    const { view } = mount();
    await show(view);
    const { event } = dispatchOn(view, 'mousedown', { 'data-tgj-insert': 'field' });
    assert.ok(event.defaultPrevented);
});

// --- the hover link between template and preview (Task 10, design §6.5) ---

const HOVER_TEXT = '<b>{{ title }}</b> ({{ year }})';
// The pieces Templates/Validate gives for HOVER_TEXT: <b>, {{ title }}, </b> (, {{ year }} and ).
const HOVER_PIECES = [[0, 3], [3, 11], [14, 6], [20, 10], [30, 1]].map(([Start, Length], Index) => ({ Index, Start, Length }));
const marked = (index, html) => `\uE000${index}\uE001${html}\uE002`;
const hoverReply = text => ({
    ...VALIDATION, Html: '<b>Dune</b> (2021)', ForText: text, Pieces: text === HOVER_TEXT ? HOVER_PIECES : [],
    MarkedHtml: marked(0, '<b>') + marked(1, 'Dune') + marked(2, '</b> (') + marked(3, '2021') + marked(4, ')'),
});

/** A rendered element's stand-in with a classList and the attributes given. */
const fakeNode = (attributes, rects = []) => {
    const classes = new Set();
    return {
        classes,
        getAttribute: name => attributes[name] ?? null,
        classList: { add: c => classes.add(c), remove: c => classes.delete(c), contains: c => classes.has(c) },
        getClientRects: () => rects,
    };
};

/** The Messages page on the movie, its template HOVER_TEXT, with the preview's piece spans and the mirror's spans faked. */
async function mountHover({ typed = HOVER_TEXT } = {}) {
    const items = KEYS.map(key => item(key, key === 'media.movie' ? { Text: HOVER_TEXT } : {}));
    const mounted = mount({
        'GET TgJelly/Templates': templates({ Items: items }),
        'POST TgJelly/Templates/Validate': request => hoverReply(JSON.parse(request.data).Text),
    });
    await show(mounted.view);
    const { el, doc, view } = mounted;
    const ta = wireTextarea(view, doc, el, { value: typed, start: 4, end: 6 });
    let selections = 0;
    const setSelectionRange = ta.setSelectionRange;
    ta.setSelectionRange = (...args) => { selections++; setSelectionRange.apply(ta, args); };
    ta.focus();
    const focusBefore = ta.focus;
    let focuses = 0;
    ta.focus = () => { focuses++; focusBefore.call(ta); };
    const spans = HOVER_PIECES.map(p => fakeNode({ 'data-tgj-piece': String(p.Index) }));
    el('tgj-tpl-preview').querySelectorAll = selector => {
        const match = /^\[data-tgj-piece="(\d+)"\]$/.exec(selector);
        return match ? spans.filter(s => s.getAttribute('data-tgj-piece') === match[1]) : selector === '.tgj-piece-hl' ? spans.filter(s => s.classes.has('tgj-piece-hl')) : [];
    };
    // In the mirror, each piece is one line of 20px: piece k from y = 20k to 20k + 20.
    const mirror = el('tgj-tpl-mirror');
    const sources = HOVER_PIECES.map(p => fakeNode({ 'data-tgj-src': String(p.Index) }, [{ left: 0, right: 200, top: 20 * p.Index, bottom: 20 * p.Index + 20 }]));
    mirror.querySelectorAll = selector => (selector === '[data-tgj-src]' ? sources : []);
    return { ...mounted, ta, spans, mirror, counts: () => ({ focuses, selections }) };
}

const lit = spans => spans.filter(s => s.classes.has('tgj-piece-hl')).map(s => s.getAttribute('data-tgj-piece'));
const litSource = mirror => [...mirror.innerHTML.matchAll(/<span data-tgj-src="(\d+)" class="tgj-piece-hl">([^<]*)<\/span>/g)].map(m => [m[1], m[2]]);

test('hover_previewPiece_highlightsSourceRange', async () => {
    const { view, spans, mirror } = await mountHover();

    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '3' });
    assert.deepEqual(lit(spans), ['3'], 'the preview piece');
    assert.deepEqual(litSource(mirror), [['3', '{{ year }}']], 'and its source range, on the same line as {{ title }}');
    assert.ok(mirror.innerHTML.startsWith('<span data-tgj-src="0">&lt;b&gt;</span><span data-tgj-src="1">{{ title }}</span>'), mirror.innerHTML);

    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '1' });
    assert.deepEqual(lit(spans), ['1']);
    assert.deepEqual(litSource(mirror), [['1', '{{ title }}']]);

    // From the template side: the pointer over {{ year }}'s text in the textarea lights its output.
    dispatchOn(view, 'mousemove', { 'data-tgj-input': 'tplText' }, {}, { clientX: 30, clientY: 65 });
    assert.deepEqual(lit(spans), ['3']);
    assert.deepEqual(litSource(mirror), [['3', '{{ year }}']]);

    // Moving away clears both sides.
    dispatchOn(view, 'mouseover', {});
    assert.deepEqual(lit(spans), []);
    assert.deepEqual(litSource(mirror), []);
    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '4' });
    dispatchOn(view, 'mouseout', { 'data-tgj-piece': '4' }, {}, { relatedTarget: null });
    assert.deepEqual(lit(spans), [], 'leaving the page clears it too');
});

test('hover_ignoresPiecesForOlderText', async () => {
    // The textarea already holds newer text than the preview's pieces were computed for.
    const { view, spans, mirror, ta, timers } = await mountHover({ typed: `${HOVER_TEXT} more` });
    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '3' });
    dispatchOn(view, 'mousemove', { 'data-tgj-input': 'tplText' }, {}, { clientX: 30, clientY: 65 });
    assert.deepEqual(lit(spans), []);
    assert.deepEqual(litSource(mirror), []);

    // Back to the text the pieces are for: hover works; typing drops the highlight at once.
    ta.value = HOVER_TEXT;
    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '3' });
    assert.deepEqual(lit(spans), ['3']);
    ta.value = `${HOVER_TEXT}!`;
    dispatchOn(view, 'input', TPL_ATTRS, { value: ta.value });
    await flush();
    assert.deepEqual(lit(spans), []);
    assert.deepEqual(litSource(mirror), []);
    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '3' });
    assert.deepEqual(lit(spans), [], 'still the old pieces until the next preview');
    await timers.advance(500);
    assert.equal(mirror.innerHTML.includes('tgj-piece-hl'), false);
});

test('hover_neverMovesCaretOrFocus', async () => {
    const { view, el, doc, ta, api, counts } = await mountHover();
    const writes = el('tgj-messages').writes;
    const calls = api.calls.length;

    dispatchOn(view, 'mouseover', { 'data-tgj-piece': '1' });
    dispatchOn(view, 'mousemove', { 'data-tgj-input': 'tplText' }, {}, { clientX: 30, clientY: 65 });
    dispatchOn(view, 'scroll', { 'data-tgj-input': 'tplText' });
    dispatchOn(view, 'mouseout', { 'data-tgj-input': 'tplText' }, {}, { relatedTarget: null });
    await flush();

    assert.deepEqual(counts(), { focuses: 0, selections: 0 }, 'no focus() and no setSelectionRange()');
    assert.equal(doc.activeElement, ta, 'the textarea keeps the focus');
    assert.deepEqual([ta.selectionStart, ta.selectionEnd, ta.value], [4, 6, HOVER_TEXT], 'and its caret and text');
    assert.equal(el('tgj-messages').writes, writes, 'the section is not written again');
    assert.equal(api.calls.length, calls, 'nothing is saved or validated');
});

test('the mirror sits behind the textarea and never takes the focus', () => {
    const html = render('media.movie');
    const field = /<div class="tgj-tpl-field">([\s\S]*?)<\/textarea>/.exec(html);
    assert.ok(field, html);
    assert.ok(field[1].startsWith('<div class="tgj-tpl-mirror" id="tgj-tpl-mirror" aria-hidden="true"></div><textarea'), field[1]);
    const css = readFileSync(new URL('../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css', import.meta.url), 'utf8');
    assert.match(css, /#tgjelly-root \.tgj-tpl-mirror \{[^}]*pointer-events: none;[^}]*color: transparent;/);
    assert.match(css, /#tgjelly-root \.tgj-tpl-field \.tgj-template-text \{[^}]*background: transparent;/);
    assert.match(css, /#tgjelly-root \.tgj-piece-hl \{[^}]*background: var\(--tgj-accent-soft\);[^}]*outline: 1px solid var\(--tgj-accent-ring\);/);
});
