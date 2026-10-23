// The Messages section (plan 9 Task 8, design §6.1-6.2): one page per message, with its switches, its template and the
// preview. Run: node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js';
import * as msg from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-messages.js';
import * as ui from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js';
import { makeView, makeApiClient, fakeTimers, flush, dispatchOn } from './fake-view.mjs';

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
    return { view, api, timers, hooks, win: globalThis.window, el: id => view.elements.get(id) };
}

async function show(view) {
    view.dispatchEvent(new Event('viewshow'));
    await flush();
}

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
    assert.deepEqual(m.SECTIONS, ['overview', 'destinations', 'messages', 'advanced']);
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
    assert.match(css, /#tgjelly-root \.tgj-msg-side \{[^}]*position: sticky;/);
    assert.match(css, /@container tgjelly \(max-width: [\d.]+rem\) \{[^@]*#tgjelly-root \.tgj-msg-layout \{[^}]*grid-template-columns: minmax\(0, 1fr\);/);
    const html = render('media.movie');
    assert.ok(html.startsWith('<div class="tgj-msg-layout">'), html.slice(0, 80));
    assert.ok(html.indexOf('<div class="tgj-msg-cards">') < html.indexOf('<div class="tgj-msg-side">'));
    assert.ok(!/[●✓✔✅⚠❌✗🟢🔴]/u.test(html));
});
