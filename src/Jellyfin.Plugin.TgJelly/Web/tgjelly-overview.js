// TgJelly dashboard: Overview — status, the bot and its token, the proxy form.

import {
    actionButton, card, esc, formatNumber, formatTime, inlineConfirm, label, notice, row, selectField, switchControl, t,
    text, textField,
} from './configurationpage?name=tgjelly-ui.js';
import { PROBLEM_STATES } from './configurationpage?name=tgjelly-api.js';
import { connectionLabel } from './configurationpage?name=tgjelly-shell.js';

/**
 * Renders the Status section from GET Status.
 * @param {object} status the StatusDto
 * @param {Record<string, string>} S
 * @param {string} [lang]
 * @returns {string} HTML
 */
export function renderStatus(status, S, lang = 'en') {
    const counters = status.Counters ?? {};
    const dropped = (counters.DroppedIntake ?? 0) + (counters.DroppedOutbox ?? 0) + (counters.Evictions ?? 0);
    const parts = [];

    // Connecting and the two waits are in progress; no token is neutral.
    const tone = PROBLEM_STATES.has(status.ConnectionState) ? 'bad'
        : status.ConnectionState === 'Connected' ? 'good'
            : status.ConnectionState === 'NoToken' ? 'neutral' : 'progress';
    const takeOver = status.ConnectionState === 'WebhookConflict'
        ? actionButton({ label: text(S, 'web.status.takeOver'), act: 'takeover', kind: 'primary' })
        : '';
    // The label holds the state word; the server's message (long when something is wrong) is plain text beside it.
    // Not connected and Connected say it all in the label ("No bot connected" and "Connected as @bot" would only
    // repeat it; the Bot card names the bot), so they carry no message.
    const word = status.ConnectionState === 'Connected' ? text(S, 'web.status.connected') : connectionLabel(status, S).text;
    const repeats = status.ConnectionState === 'NoToken' || status.ConnectionState === 'Connected';
    const message = status.ConnectionMessage && status.ConnectionMessage !== word && !repeats
        ? `<span class="tgj-conn-message">${esc(status.ConnectionMessage)}</span>` : '';
    parts.push(row({
        label: text(S, 'web.status.connection'),
        control: label(word, tone) + message + takeOver,
    }));

    if (!status.HasToken) {
        // Before a bot is connected the card is just the connection line, above the Connect card.
        return card({ title: text(S, 'web.status.title'), body: parts.join('') });
    }

    parts.push(notice({ kind: 'info', html: text(S, 'web.bot.access') }));
    parts.push(row({
        label: text(S, 'web.status.activity'),
        extraClass: 'tgj-row--stack',
        control: `<span class="tgj-row-hint">${t(S, 'web.status.counts', {
            pending: formatNumber(status.PendingMedia ?? 0, lang),
            outbox: formatNumber(status.OutboxCount ?? 0, lang),
            dropped: formatNumber(dropped, lang),
        })}</span>`,
    }));

    for (const entry of status.Notices ?? []) {
        parts.push(notice({
            kind: 'info',
            html: `<span class="tgj-notice-text">${esc(entry.Text)}</span>`
                + actionButton({ label: text(S, 'web.status.dismiss'), act: 'dismiss', id: entry.Id }),
        }));
    }

    const errors = status.LastErrors ?? [];
    if (errors.length > 0) {
        const items = [...errors].reverse().map(e => `<li>${t(S, 'web.status.error', {
            time: formatTime(e.At, lang),
            source: e.Source,
            message: e.Message,
        })}</li>`);
        parts.push(row({
            label: text(S, 'web.status.errors'),
            extraClass: 'tgj-row--stack',
            control: `<ul class="tgj-errors tgj-row-hint">${items.join('')}</ul>`,
        }));
    }

    return card({ title: text(S, 'web.status.title'), body: parts.join('') });
}

/** A link to Telegram, or '' when the URL isn't one the server should have sent. */
export function telegramLink(url, label, { primary = false } = {}) {
    return typeof url === 'string' && url.startsWith('https://t.me/')
        ? `<a class="tgj-btn${primary ? ' tgj-btn--primary' : ''}" href="${esc(url)}" target="_blank" rel="noopener noreferrer">${label}</a>`
        : '';
}

/**
 * The proxy card: under Advanced, and inside the "Can't reach Telegram?" panel.
 * Its fields auto-save together, as one save keyed 'proxy', when the focus leaves the card (every proxy save restarts
 * the bot session); so they all carry data-tgj-save="proxy", and the card has one cue, beside the mode.
 * @param {object | null} settings the SettingsDto (null until it loads)
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is the editor state: local.Mode and local.Url. The proxy username and
 *   password are not part of it.
 * @param {{ nested?: boolean }} [options] nested: inside another card (the connect panel), so no card of its own
 * @returns {string} HTML
 */
export function renderProxyForm(settings, S, ui, { nested = false } = {}) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local;
    const modes = ['System', 'None', 'Http', 'Socks5'].map(value => ({ value, label: text(S, PROXY_MODE_LABELS[value]) }));
    const save = { 'data-tgj-save': 'proxy', autocomplete: 'off' };
    // Username and password are never written into the HTML: the controller puts typed text back into the
    // inputs after a render.
    const field = (input, labelKey, type, value, placeholder = '') => row({
        label: text(S, labelKey),
        control: textField({ input, value, type, placeholder, attrs: { ...save, 'aria-label': text(S, labelKey) } }),
    });
    const rows = [row({
        label: text(S, 'web.proxy.mode'),
        cueKey: 'proxy',
        control: selectField({ input: 'proxyMode', value: local.Mode, options: modes, attrs: { 'data-tgj-save': 'proxy', 'aria-label': text(S, 'web.proxy.mode') } }),
    })];
    if (local.Mode === 'Http' || local.Mode === 'Socks5') {
        rows.push(field('proxyUrl', 'web.proxy.url', 'text', local.Url ?? ''));
    }

    // Hiding the credentials changes nothing: what was typed or saved stays.
    if (local.Mode === 'Http' || local.Mode === 'Socks5') {
        rows.push(field('proxyUsername', 'web.proxy.username', 'text', '', settings.HasProxyUsername ? text(S, 'web.proxy.saved') : ''));
        rows.push(field('proxyPassword', 'web.proxy.password', 'password', '', settings.HasProxyPassword ? text(S, 'web.proxy.saved') : ''));
    }

    rows.push(`<p class="tgj-row-hint">${t(S, 'web.proxy.typesHint')}</p>`);
    return nested
        ? `<div class="tgj-proxy-form"><h3 class="tgj-subtitle">${t(S, 'web.proxy.title')}</h3>${rows.join('')}</div>`
        : card({ title: text(S, 'web.proxy.title'), body: rows.join(''), extraClass: 'tgj-proxy-form' });
}

const PROXY_MODE_LABELS = { System: 'web.proxy.modeSystem', None: 'web.proxy.modeNone', Http: 'web.proxy.modeHttp', Socks5: 'web.proxy.modeSocks5' };

/**
 * The token form: a password input that is never pre-filled, the two notes for an existing bot, and
 * Connect. Connecting is an explicit action: nothing here auto-saves.
 */
function renderTokenForm(ui, S) {
    const busy = Boolean(ui.saving);
    const notes = ui.mode === 'existing'
        ? `<p class="tgj-row-hint">${t(S, 'web.connect.noteRestrict')}</p><p class="tgj-row-hint">${t(S, 'web.connect.noteOther')}</p>`
        : '';
    return '<div class="tgj-token-form">'
        + row({
            label: text(S, 'web.connect.tokenLabel'),
            extraClass: 'tgj-row--stack',
            control: textField({ input: 'token', value: '', type: 'password', attrs: { autocomplete: 'off', 'aria-label': text(S, 'web.connect.tokenLabel'), disabled: busy } }),
        })
        + notes
        + `<div class="tgj-actions">${actionButton({ label: text(S, 'web.connect.save'), act: 'saveToken', kind: 'primary', disabled: busy })}`
        + `${ui.hasToken ? actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelToken', disabled: busy }) : ''}</div></div>`;
}

/**
 * The connect card: shown without a token, or while changing it, and the outcome of the last save.
 * @param {{ hasToken: boolean, changingToken?: boolean, mode?: string | null, saving?: boolean,
 *   result?: object | null, unreachable?: boolean, proxy?: object }} ui hasToken comes from GET Status; result is
 *   { kind: 'Connected' | 'NeedsConfirm' | 'Error', message, username, webhookDeleted }; proxy is the proxy form's ui
 * @param {Record<string, string>} S
 * @param {{ OpenBot?: string } | null} links
 * @param {object | null} [settings] for the proxy panel
 * @param {{ bare?: boolean }} [options] bare: the body only, for the Bot card while the token is being changed
 * @returns {string} HTML
 */
export function renderConnect(ui, S, links, settings = null, { bare = false } = {}) {
    const result = ui.result;
    const busy = Boolean(ui.saving);
    if (result && result.kind === 'Connected') {
        return result.webhookDeleted ? notice({ kind: 'info', html: t(S, 'web.connect.webhookRemoved') }) : '';
    }

    if (ui.hasToken && !ui.changingToken) {
        return '';
    }

    const title = text(S, 'web.connect.title');
    const wrap = body => (bare ? body : card({ title, body }));
    if (result && result.kind === 'NeedsConfirm') {
        return wrap(`<p>${esc(result.message)}</p><div class="tgj-actions">`
            + actionButton({ label: text(S, 'web.connect.switch'), act: 'switchBot', kind: 'primary', disabled: busy })
            + actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelSwitch', disabled: busy })
            + '</div>');
    }

    const parts = [];
    if (!ui.mode) {
        parts.push('<div class="tgj-actions">'
            + actionButton({ label: text(S, 'web.connect.createNew'), act: 'connectNew', kind: 'primary', disabled: busy })
            + actionButton({ label: text(S, 'web.connect.haveBot'), act: 'connectExisting', disabled: busy })
            + `${ui.hasToken ? actionButton({ label: text(S, 'web.connect.cancel'), act: 'cancelToken', disabled: busy }) : ''}</div>`);
    } else {
        if (ui.mode === 'new') {
            parts.push('<ol class="tgj-steps">'
                + `<li>${t(S, 'web.connect.step1')} ${telegramLink('https://t.me/BotFather', t(S, 'web.connect.openBotFather'))}</li>`
                + `<li>${t(S, 'web.connect.step2')}</li><li>${t(S, 'web.connect.step3')}</li></ol>`);
        }

        parts.push(renderTokenForm(ui, S));
    }

    if (result && result.kind === 'Error') {
        parts.push(notice({ kind: 'error', html: esc(result.message) }));
    }

    if (ui.unreachable) {
        parts.push(`<div class="tgj-unreachable"><h3>${t(S, 'web.connect.unreachableTitle')}</h3>`
            + `<p class="tgj-row-hint">${t(S, 'web.connect.unreachableHint')}</p>${renderProxyForm(settings, S, ui.proxy ?? {}, { nested: true })}</div>`);
    }

    return wrap(parts.join(''));
}

/**
 * The Bot card: the username, the access reminder, Open in Telegram, Change token and
 * Disconnect bot with its inline confirmation.
 * @param {object} status the StatusDto
 * @param {{ OpenBot?: string } | null} links
 * @param {Record<string, string>} S
 * @param {{ confirmDisconnect?: boolean, removeDestinations?: boolean, saving?: boolean }} ui
 * @param {string} [changeHtml] the change-token form, shown in place of the card's actions while the token is changed
 * @returns {string} HTML
 */
export function renderBot(status, links, S, ui, changeHtml = '') {
    if (!status || !status.HasToken) {
        return '';
    }

    const busy = Boolean(ui.saving);
    // The switch is part of the question, not a setting: it saves nothing (like "Bot leaves the chat" on a destination).
    const disconnect = ui.confirmDisconnect
        ? row({
            label: text(S, 'web.bot.disconnect.removeDestinations'),
            control: switchControl({ input: 'disconnectRemoveDestinations', checked: Boolean(ui.removeDestinations), label: text(S, 'web.bot.disconnect.removeDestinations') }),
        }) + `<p class="tgj-row-hint">${t(S, 'web.bot.disconnect.removeHint')}</p>` + inlineConfirm({
            question: text(S, 'web.bot.confirmDisconnect'),
            confirmLabel: text(S, 'web.bot.confirmYes'),
            confirmAct: 'disconnectYes',
            cancelLabel: text(S, 'web.connect.cancel'),
            cancelAct: 'disconnectNo',
        })
        : actionButton({ label: text(S, 'web.bot.disconnect'), act: 'disconnect', kind: 'danger', disabled: busy });
    return card({
        title: text(S, 'web.bot.title'),
        body: row({ label: text(S, 'web.bot.name'), control: `<b>@${esc(status.BotUsername)}</b>` })
            + (changeHtml
                ? `<div class="tgj-bot-change">${changeHtml}</div>`
                : `<div class="tgj-actions">${telegramLink(links && links.OpenBot, t(S, 'web.bot.open'))}`
                    + `${actionButton({ label: text(S, 'web.bot.change'), act: 'changeToken', disabled: busy })}${disconnect}</div>`),
    });
}

/** The proxy editor's data from a SettingsDto. Username and Password are absent until the user types in them. */
export function proxyFormData(settings) {
    return { Mode: settings.ProxyMode, Url: settings.ProxyUrl };
}
