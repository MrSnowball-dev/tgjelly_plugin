// TgJelly dashboard: Destinations — the list, the editor, topics and adding a chat by hand.

import {
    actionButton, card, emptyState, esc, inlineConfirm, label, notice, row, selectField, switchControl, t, text,
    textField,
} from './configurationpage?name=tgjelly-ui.js';
import { sameValue } from './configurationpage?name=tgjelly-api.js';
import { telegramLink } from './configurationpage?name=tgjelly-overview.js';

/** A destination's topic routes as sorted "slot=thread" strings, so the form compares and stores them as one value. */
const routeList = routes => (routes ?? []).map(r => `${r.Slot}=${r.ThreadId}`).sort();

/** The routes of a routeList as a map from slot to thread id. */
export const routeMap = list => new Map((list ?? []).map(entry => {
    const at = entry.lastIndexOf('=');
    return [entry.slice(0, at), Number(entry.slice(at + 1))];
}));

/** The editor's data for one destination from the DestinationDto: the fields its auto-saves can send. */
export function destinationFormData(dest) {
    return {
        Enabled: Boolean(dest.Enabled),
        Routes: routeList(dest.TopicRoutes),
        Separate: Boolean(dest.SeparateMediaTopics),
        Events: [...(dest.Events ?? [])],
        LibraryIds: [...(dest.LibraryIds ?? [])],
        SilentGroups: [...(dest.SilentGroups ?? [])],
    };
}

/**
 * The body of a destination's PUT: only the fields that differ from base, plus ExpectedRevision (spec §7.2, §10.4).
 * @param {object} base the form data the edit started from
 * @param {object} local the form's current data
 * @param {number} revision
 * @returns {object | null} null when nothing changed
 */
export function destinationChanges(base, local, revision) {
    const body = { ExpectedRevision: revision };
    const differs = key => !sameValue(local[key], base[key]);
    for (const key of ['Events', 'LibraryIds', 'SilentGroups', 'Enabled']) {
        if (differs(key)) {
            body[key] = local[key];
        }
    }

    if (differs('Separate')) {
        body.SeparateMediaTopics = local.Separate;
    }

    // Only the slots whose topic changed; null sends a slot back to the destination's own topic.
    if (differs('Routes')) {
        const before = routeMap(base.Routes);
        const after = routeMap(local.Routes);
        body.TopicRoutes = [...new Set([...before.keys(), ...after.keys()])]
            .filter(slot => before.get(slot) !== after.get(slot))
            .map(slot => ({ Slot: slot, ThreadId: after.has(slot) ? after.get(slot) : null }));
    }

    return Object.keys(body).length > 1 ? body : null;
}

const libraryKey = id => String(id).replaceAll('-', '').toLowerCase();
export const sameLibrary = (a, b) => libraryKey(a) === libraryKey(b);

/**
 * The dashboard's health words and colours (design §7). The bot's dest.health.* labels carry emoji by design, so the
 * dashboard has its own web.dest.health.* copies and leaves the bot's strings alone.
 */
const HEALTH = {
    Ok: { key: 'web.dest.health.Ok', tone: 'good' },
    PhotosBlocked: { key: 'web.dest.health.PhotosBlocked', tone: 'attention' },
    NeedsPermissions: { key: 'web.dest.health.NeedsPermissions', tone: 'bad' },
    TopicMissing: { key: 'web.dest.health.TopicMissing', tone: 'bad' },
    Lost: { key: 'web.dest.health.Lost', tone: 'bad' },
};

/** A health status as a label's { tone, text }; a status this page doesn't know shows as its name, neutral. */
const healthState = (status, S) => {
    const known = HEALTH[status || 'Ok'];
    return known ? { tone: known.tone, text: text(S, known.key) } : { tone: 'neutral', text: String(status) };
};

/** A health status as escaped HTML text, for an outcome line ("Check again", a test send). */
export const healthText = (status, S) => esc(healthState(status, S).text);

/** The health as labels, only when something is wrong (a healthy destination shows none): the status, plus "Photos blocked" when photos are blocked too. */
function healthLabels(health, S) {
    const status = health && health.Status ? health.Status : 'Ok';
    const asLabel = ({ tone, text: words }) => label(words, tone);
    return (status === 'Ok' ? '' : asLabel(healthState(status, S)))
        + (health && health.PhotosBlocked && status !== 'PhotosBlocked' ? asLabel(healthState('PhotosBlocked', S)) : '');
}

/** The save key (and cue key) of one control in a destination's editor: 'dest:<id>:<what>'. */
export const destSaveKey = (id, what) => `dest:${id}:${what}`;

/**
 * The editor card for one destination (spec §7.2, §7.3; design §6.1): the event switches by group,
 * the libraries and the silent groups. Every control saves itself at once (data-tgj-save, with its cue beside it);
 * Send test, Check again and Remove stay actions, and Remove asks inline first.
 * @param {object} dest the DestinationDto
 * @param {{ EventKeys: string[], Groups: string[] }} catalog
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object }, confirmRemove?: boolean, leave?: boolean, result?: string }} ui
 *   form defaults to the destination's own values; result is HTML shown under the buttons
 * @returns {string} HTML
 */
export function renderEditor(dest, catalog, libraries, S, ui) {
    const local = (ui.form && ui.form.local) || destinationFormData(dest);
    const id = dest.Id;
    const switchFor = (input, what, checked, label, attrs) => switchControl({ input, checked, label, attrs: { 'data-tgj-id': id, ...attrs, 'data-tgj-save': destSaveKey(id, what) } });
    const toggle = (input, what, checked, label, attrs) => row({ label, control: switchFor(input, what, checked, label, attrs), cueKey: destSaveKey(id, what) });
    const parts = [];

    // A forum: where the destination posts, and a topic choice beside each media event and each other group.
    const forum = Boolean(dest.IsForum);
    const routes = routeMap(local.Routes);
    const separate = Boolean(local.Separate);
    const topic = slot => (forum ? topicChoice(dest, slot, routes, S, ui, separate) : '');
    if (forum) {
        parts.push(row({ label: text(S, 'web.topics.postsTo'), control: `<span class="tgj-topic-name">${esc(topicName(dest, dest.ThreadId, S))}</span>` }));
    }

    const events = new Set(local.Events);
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.events.title')}</h3>`);
    for (const group of catalog.Groups) {
        const keys = catalog.EventKeys.filter(k => k.startsWith(group + '.'));
        // Each group has one topic; Media can instead have a topic for each event ("Separate topics for each event"),
        // and only then do the events show their own choice (owner, 2026-10-25).
        const groupTopic = topic(group);
        parts.push(`<div class="tgj-subcard" data-tgj-group-card="${esc(group)}">`
            + `<div class="tgj-subcard-head"><h3 class="tgj-subcard-title">${t(S, `web.event.group.${group}`)}</h3>${groupTopic}</div>`
            + keys.map(k => (group === 'media' && forum && separate
                ? row({ label: text(S, `web.event.${k}`), control: switchFor('destEvent', `event:${k}`, events.has(k), text(S, `web.event.${k}`), { 'data-tgj-key': k }) + topic(k), cueKey: destSaveKey(id, `event:${k}`) })
                : toggle('destEvent', `event:${k}`, events.has(k), text(S, `web.event.${k}`), { 'data-tgj-key': k }))).join('')
            + '</div>');
    }

    // Removing a leftover library has no switch of its own to show the cue beside, so the Libraries title carries it.
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.libraries.title')} `
        + `<span class="tgj-cue" data-tgj-cue="${esc(destSaveKey(id, 'libraries'))}" aria-live="polite"></span></h3>`);
    if (dest.LibraryFilterBroken) {
        parts.push(notice({ kind: 'error', html: t(S, 'web.dest.librariesRemoved') }));
    }

    const chosen = new Set(local.LibraryIds.map(libraryKey));
    parts.push(libraries.map(l => toggle('destLibrary', `lib:${l.Id}`, chosen.has(libraryKey(l.Id)), l.Name, { 'data-tgj-lib': l.Id })).join(''));
    for (const gone of (dest.RemovedLibraryIds ?? []).filter(x => local.LibraryIds.some(y => sameLibrary(x, y)))) {
        parts.push(row({
            label: text(S, 'dest.libraries.removed', { id: gone }),
            control: actionButton({ label: text(S, 'web.dest.removeLibrary'), act: 'removeLibrary', id, attrs: { 'data-tgj-lib': gone } }),
        }));
    }

    parts.push(`<p class="tgj-row-hint">${t(S, chosen.size === 0 ? 'dest.libraries.all' : 'dest.libraries.hint')}</p>`);

    const silent = new Set(local.SilentGroups);
    parts.push(`<h3 class="tgj-subtitle">${t(S, 'dest.silent.title')}</h3>`
        + catalog.Groups.map(g => toggle('destSilent', `silent:${g}`, silent.has(g), text(S, `groups.${g}`), { 'data-tgj-group': g })).join('')
        + `<p class="tgj-row-hint">${t(S, 'dest.silent.hint')}</p>`);

    parts.push(destinationIds(dest, routes, S, Boolean(ui.idsOpen), separate));

    parts.push('<div class="tgj-actions">'
        + actionButton({ label: text(S, 'dest.btn.test'), act: 'testDest', id })
        + actionButton({ label: text(S, 'dest.btn.check'), act: 'checkDest', id })
        + (ui.confirmRemove ? '' : actionButton({ label: text(S, 'dest.btn.remove'), act: 'remove', kind: 'danger', id }))
        + '</div>');
    if (ui.confirmRemove) {
        // "Bot leaves the chat" is part of the question, not a setting: it saves nothing.
        parts.push(row({
            label: text(S, 'web.dest.leave'),
            control: switchControl({ input: 'destLeave', checked: Boolean(ui.leave), label: text(S, 'web.dest.leave'), attrs: { 'data-tgj-id': id } }),
        }));
        parts.push(inlineConfirm({
            question: text(S, 'web.dest.confirmRemove', { title: dest.Title }),
            confirmLabel: text(S, 'dest.btn.remove'),
            confirmAct: 'removeYes',
            cancelAct: 'removeNo',
            cancelLabel: text(S, 'dest.btn.cancel'),
            id,
        }));
    }

    if (ui.result) {
        parts.push(`<p class="tgj-row-hint tgj-dest-result">${ui.result}</p>`);
    }

    return card({ title: dest.Title, description: destinationMeta(dest, S), body: parts.join(''), extraClass: 'tgj-dest-editor' });
}

/** A topic's name for the editor: the name TgJelly knows, else General or "Topic #123". */
export function topicName(dest, thread, S) {
    const known = (dest.KnownTopics ?? []).find(k => k.ThreadId === thread);
    if (known && known.Name) {
        return known.Name;
    }

    return thread === 0 ? text(S, 'web.topics.general') : text(S, 'web.topics.number', { id: thread });
}

/** A slot's label: a media event's name, or the group's. */
const slotLabel = (slot, S) => text(S, slot.includes('.') ? `web.event.${slot}` : `web.event.group.${slot}`);

/**
 * The topic choice beside a media event or a group's title (forums only): the destination's own topic, General, the
 * topics TgJelly knows, Create a topic…, or Use a topic link…; a cue beside it, "Topic gone" when Telegram says so, and
 * the link field while a link is being entered.
 */
function topicChoice(dest, slot, routes, S, ui, separate = false) {
    const id = dest.Id;
    const key = destSaveKey(id, `topic:${slot}`);
    const value = slot === 'media' && separate ? 'separate' : routes.has(slot) ? String(routes.get(slot)) : '';
    const threads = [0, ...(dest.KnownTopics ?? []).map(k => k.ThreadId)].filter((t, i, all) => t !== dest.ThreadId && all.indexOf(t) === i);
    if (routes.has(slot) && !threads.includes(routes.get(slot)) && routes.get(slot) !== dest.ThreadId) {
        threads.push(routes.get(slot));
    }

    const options = [
        { value: '', label: text(S, 'web.topics.default', { topic: topicName(dest, dest.ThreadId, S) }) },
        ...threads.map(t => ({ value: String(t), label: topicName(dest, t, S) })),
        ...(slot === 'media' ? [{ value: 'separate', label: text(S, 'web.topics.separate') }] : []),
        { value: 'create', label: text(S, 'web.topics.create') },
        { value: 'link', label: text(S, 'web.topics.link') },
    ];
    const gone = routes.has(slot) && (dest.MissingTopics ?? []).includes(routes.get(slot)) ? label(text(S, 'web.topics.gone'), 'attention') : '';
    const select = selectField({
        input: 'destTopic',
        value,
        options,
        extraClass: 'tgj-topic-select',
        attrs: { 'data-tgj-id': id, 'data-tgj-key': slot, 'data-tgj-save': key, 'aria-label': text(S, 'web.topics.choose', { what: slotLabel(slot, S) }) },
    });
    const asking = ui.topicLink && ui.topicLink.id === id && ui.topicLink.slot === slot ? ui.topicLink : null;
    // Creating a topic is asked first: a keyboard user moving through the list passes Create a topic… on the way.
    const creating = asking && asking.create
        ? `<span class="tgj-topic-link"><span class="tgj-row-hint">${t(S, 'web.topics.createAsk', { name: slotLabel(slot, S) })}</span>`
            + actionButton({ label: text(S, 'web.topics.createYes'), act: 'topicCreateYes', kind: 'primary', id, attrs: { 'data-tgj-key': slot } })
            + actionButton({ label: text(S, 'web.topics.linkCancel'), act: 'topicLinkCancel', id, attrs: { 'data-tgj-instant': '' } })
            + '</span>'
        : '';
    const linking = asking && !asking.create
        ? `<span class="tgj-topic-link">${textField({ input: 'destTopicLink', value: ui.topicLink.text ?? '', attrs: { 'data-tgj-id': id, 'data-tgj-key': slot, autocomplete: 'off', spellcheck: 'false', 'aria-label': text(S, 'web.topics.linkLabel') } })}`
            + actionButton({ label: text(S, 'web.topics.linkUse'), act: 'topicLinkUse', kind: 'primary', id })
            + actionButton({ label: text(S, 'web.topics.linkCancel'), act: 'topicLinkCancel', id, attrs: { 'data-tgj-instant': '' } })
            + '</span>'
        : '';
    return `<span class="tgj-topic">${select}${gone}<span class="tgj-cue" data-tgj-cue="${esc(key)}" aria-live="polite"></span>${creating}${linking}</span>`;
}

/** A destination's IDs, folded away until opened: the chat, its topic, and each routed slot's topic. */
function destinationIds(dest, routes, S, open, separate) {
    const toggleButton = `<button type="button" class="tgj-ref-toggle" data-tgj-act="destIds" data-tgj-id="${esc(dest.Id)}" data-tgj-instant aria-expanded="${open}">${t(S, 'web.dest.ids')}</button>`;
    if (!open) {
        return `<div class="tgj-dest-ids">${toggleButton}</div>`;
    }

    const lines = [[text(S, 'web.dest.ids.chat'), dest.ChatId]];
    if (dest.IsForum || dest.ThreadId) {
        lines.push([text(S, 'web.dest.ids.topic'), dest.ThreadId]);
    }

    // Only the routes in use: the media events' own with separate topics, the Media topic otherwise.
    for (const [slot, thread] of routes) {
        if (slot.startsWith('media.') !== separate && (slot === 'media' || slot.startsWith('media.'))) {
            continue;
        }

        lines.push([text(S, 'web.dest.ids.slot', { what: slotLabel(slot, S) }), thread]);
    }

    return `<div class="tgj-dest-ids">${toggleButton}<dl class="tgj-dest-ids-list">`
        + lines.map(([name, value]) => `<dt>${esc(name)}</dt><dd><code>${esc(value)}</code></dd>`).join('')
        + '</dl></div>';
}

/** A destination's kind as plain text: "Channel", or "Forum" for a supergroup with topics. */
const destinationMeta = (dest, S) => text(S, dest.IsForum ? 'dest.kind.Forum' : `dest.kind.${dest.Kind}`);

/**
 * The Destinations section (spec §6.6 item 3; design §5.2): a card with one row per destination (its title, the kind
 * muted, the health as coloured words, the Sending switch and Edit), then the open destination's editor card.
 * Add destination, and "Set up manually" once the manual block is unlocked (spec §6.5), close the list, or make its
 * empty state.
 * @param {object | null} dto the DestinationsDto
 * @param {{ Id: string, Name: string }[]} libraries
 * @param {{ StartAdd?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ editing?: string | null, forms?: Record<string, object>, confirmRemove?: string | null, leave?: boolean,
 *   results?: Record<string, string> }} ui forms is keyed 'dest:<id>' like the controller's state.forms
 * @returns {string} HTML
 */
export function renderDestinations(dto, libraries, links, S, ui) {
    if (!dto) {
        return '';
    }

    const forms = ui.forms ?? {};
    const add = telegramLink(links && links.StartAdd, t(S, 'web.dest.add'), { primary: true })
        + (dto.ManualUnlocked
            ? `<span class="tgj-row-hint">${t(S, 'web.dest.manualHint')}</span>${actionButton({ label: text(S, 'web.dest.manualLink'), act: 'open-manual' })}`
            : '');
    const rows = dto.Items.map(dest => {
        const form = forms[`dest:${dest.Id}`];
        const key = destSaveKey(dest.Id, 'Enabled');
        return row({
            label: dest.Title,
            hint: destinationMeta(dest, S),
            extraClass: 'tgj-dest-row',
            control: healthLabels(dest.Health, S)
                + `<span class="tgj-switch-pair"><span class="tgj-switch-label">${t(S, 'web.dest.enabled')}</span>`
                + switchControl({
                    input: 'destEnabled',
                    checked: form ? Boolean(form.local.Enabled) : Boolean(dest.Enabled),
                    label: text(S, 'web.dest.enabled'),
                    attrs: { 'data-tgj-id': dest.Id, 'data-tgj-save': key },
                })
                + '</span>'
                + actionButton({ label: text(S, ui.editing === dest.Id ? 'web.dest.close' : 'web.dest.edit'), act: 'edit', id: dest.Id }),
            cueKey: key,
        });
    });
    const body = rows.length === 0
        ? emptyState({ text: text(S, 'web.dest.empty'), actionHtml: add })
        : rows.join('') + `<div class="tgj-actions">${add}</div>`;
    const cards = [card({ title: text(S, 'web.dest.title'), body })];

    const editing = dto.Items.find(d => d.Id === ui.editing);
    if (editing) {
        cards.push(renderEditor(editing, dto.Catalog, libraries ?? [], S, {
            form: forms[`dest:${editing.Id}`],
            confirmRemove: ui.confirmRemove === editing.Id,
            leave: ui.leave,
            result: (ui.results ?? {})[editing.Id],
            topicLink: ui.topicLink ?? null,
            idsOpen: Boolean((ui.idsOpen ?? {})[editing.Id]),
        }));
    }

    return `<div class="tgj-stack">${cards.join('')}</div>`;
}


/**
 * The manual card under Advanced (spec §6.5): "Add chat by ID" and the detected chats. Only when the gate is unlocked.
 * These are actions (Add, Set up, Dismiss), not settings: nothing here auto-saves.
 * @param {object | null} dto the DestinationsDto
 * @param {Record<string, string>} S
 * @param {{ input?: string, result?: string, busy?: boolean }} ui
 *   result is HTML from the last add
 * @returns {string} HTML, or '' while locked
 */
export function renderManual(dto, S, ui) {
    if (!dto || !dto.ManualUnlocked) {
        return '';
    }

    const parts = [
        row({
            label: text(S, 'web.manual.label'),
            extraClass: 'tgj-row--stack',
            control: textField({ input: 'manualInput', value: ui.input ?? '', attrs: { autocomplete: 'off', 'aria-label': text(S, 'web.manual.label') } }),
        }),
        `<div class="tgj-actions">${actionButton({ label: text(S, 'web.manual.add'), act: 'manualAdd', kind: 'primary' })}</div>`,
    ];
    if (ui.result) {
        parts.push(`<div class="tgj-manual-result">${ui.result}</div>`);
    }

    const detected = dto.DetectedChats ?? [];
    if (detected.length > 0) {
        parts.push(`<h3 class="tgj-subtitle">${t(S, 'detected.title')}</h3>`);
        for (const chat of detected) {
            const id = String(chat.ChatId);
            parts.push(row({
                label: text(S, 'detected.line', { title: chat.Title }),
                extraClass: 'tgj-detected',
                control: actionButton({ label: text(S, 'web.manual.setUp'), act: 'detectedSetUp', kind: 'primary', id })
                    + actionButton({ label: text(S, 'web.manual.dismiss'), act: 'detectedDismiss', id }),
            }));
        }
    }

    return card({ title: text(S, 'web.manual.title'), body: parts.join('') });
}
