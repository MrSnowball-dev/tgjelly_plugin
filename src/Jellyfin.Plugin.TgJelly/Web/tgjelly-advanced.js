// TgJelly dashboard: Advanced — batching, update checks, the token and diagnostics.

import {
    actionButton, card, esc, label, notice, numberField, row, switchControl, t, text,
} from './configurationpage?name=tgjelly-ui.js';
import { isPlainObject } from './configurationpage?name=tgjelly-api.js';
import { messageFormData, messageRequest } from './configurationpage?name=tgjelly-messages.js';

/** The three update kinds: the settings path's last part, the switch's input name and its strings. */
export const UPDATE_KINDS = [
    { key: 'Fixes', input: 'updFixes', label: 'web.updates.fixes', hint: 'web.updates.fixesHint' },
    { key: 'NewReleases', input: 'updNewReleases', label: 'web.updates.newReleases', hint: 'web.updates.newReleasesHint' },
    { key: 'NewMajor', input: 'updNewMajor', label: 'web.updates.newMajor', hint: 'web.updates.newMajorHint' },
];

/** The batching fields with their allowed ranges and the strings that label and describe them. */
export const BATCHING_FIELDS = [
    { key: 'QuietWindowSeconds', min: 5, max: 3600, label: 'web.batching.quiet.label', hint: 'web.batching.quiet.hint' },
    { key: 'MaxHoldMinutes', min: 1, max: 240, label: 'web.batching.maxHold.label', hint: 'web.batching.maxHold.hint' },
    { key: 'ReleaseQuietSeconds', min: 5, max: 600, label: 'web.batching.releaseQuiet.label', hint: 'web.batching.releaseQuiet.hint' },
    { key: 'DigestThreshold', min: 2, max: 100, label: 'web.batching.digest.label', hint: 'web.batching.digest.hint' },
    { key: 'LateEditHours', min: 0, max: 24, label: 'web.batching.lateEdit.label', hint: 'web.batching.lateEdit.hint' },
    { key: 'ReadyTimeoutMinutes', min: 1, max: 120, label: 'web.batching.readyTimeout.label', hint: 'web.batching.readyTimeout.hint' },
];

/**
 * The settings editor's data from a SettingsDto: the fields PUT Settings takes. Mode may arrive as a name or a number.
 * @param {object} settings
 */
export function settingsFormData(settings) {
    const check = settings.UpdateCheck ?? {};
    return {
        PublicUrl: settings.PublicUrl ?? '',
        Message: messageFormData(settings.Message),
        Batching: { ...(settings.Batching ?? {}) },
        UpdateCheck: {
            Enabled: Boolean(check.Enabled),
            Fixes: check.Fixes !== false,
            NewReleases: check.NewReleases !== false,
            NewMajor: Boolean(check.NewMajor),
        },
    };
}

/** The UpdateCheck a PUT sends. */
function updateCheckRequest(check) {
    return { Enabled: check.Enabled, Fixes: check.Fixes, NewReleases: check.NewReleases, NewMajor: check.NewMajor };
}

/** The value at a dotted path ('UpdateCheck.Enabled') of a nested object; undefined when a part is missing. */
export function pathValue(obj, path) {
    return path.split('.').reduce((node, part) => (isPlainObject(node) ? node[part] : undefined), obj);
}

/** A copy of target with the value at each dotted path taken from source (both settings form data). */
export function overlayPaths(target, source, paths) {
    const out = structuredClone(target);
    for (const path of paths) {
        const parts = path.split('.');
        let node = out;
        for (const part of parts.slice(0, -1)) {
            node = node[part] = isPlainObject(node[part]) ? node[part] : {};
        }

        node[parts.at(-1)] = structuredClone(pathValue(source, path));
    }

    return out;
}

/**
 * The PUT Settings body for an auto-save: the form's values at the given dotted paths (the field that changed) laid
 * over the saved ones (the form's base), so edits still waiting in other fields are not sent with it. Also the
 * client-side errors that stop it (a batching value that isn't a whole number); see batchingError for the ranges.
 * @param {{ base: object, local: object }} form
 * @param {string[]} paths e.g. ['Message.Switches.media.movie.ShowPoster']
 * @param {number} revision the Revision the page last saw (ExpectedRevision)
 * @param {Record<string, string>} S
 * @returns {{ body: object | null, errors: { Field: string, Message: string }[] }} Message is plain text
 */
export function settingsRequest(form, paths, revision, S) {
    const data = overlayPaths(form.base, form.local, paths);
    const errors = BATCHING_FIELDS
        .filter(f => !Number.isInteger(data.Batching[f.key]))
        .map(f => ({ Field: `Batching.${f.key}`, Message: text(S, 'web.settings.notNumber') }));
    return errors.length > 0
        ? { body: null, errors }
        : {
            body: {
                ExpectedRevision: revision,
                PublicUrl: String(data.PublicUrl ?? '').trim(),
                Message: messageRequest(data.Message, paths),
                Batching: data.Batching,
                UpdateCheck: updateCheckRequest(data.UpdateCheck),
            },
            errors: [],
        };
}

/**
 * Why a batching value can't be saved (plain text), or null: the server's rules (SettingsValidator) and its messages,
 * checked before the save. batching is the Batching object as it would be sent: this field's value with the others.
 * @param {{ key: string, min: number, max: number }} field one of BATCHING_FIELDS
 * @param {Record<string, unknown>} batching numbers, or the text typed where it isn't a whole number
 * @param {Record<string, string>} S
 * @returns {string | null}
 */
export function batchingError(field, batching, S) {
    const value = batching[field.key];
    if (!Number.isInteger(value)) {
        return text(S, 'web.settings.notNumber');
    }

    if (value < field.min || value > field.max) {
        return text(S, 'web.settings.range', { min: field.min, max: field.max });
    }

    const quiet = batching.QuietWindowSeconds;
    const hold = batching.MaxHoldMinutes;
    const pair = field.key === 'QuietWindowSeconds' || field.key === 'MaxHoldMinutes';
    return pair && Number.isInteger(quiet) && Number.isInteger(hold) && hold * 60 < quiet ? text(S, 'web.settings.maxHoldShort') : null;
}

/**
 * The batching card under Advanced: six whole-number fields, each with its range in the explanation.
 * Each saves 800 ms after the last keystroke, or when the field is left.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is state.forms.settings
 * @returns {string} HTML
 */
export function renderBatching(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const rows = BATCHING_FIELDS.map(f => {
        const path = `Batching.${f.key}`;
        return row({
            label: text(S, f.label),
            hint: text(S, f.hint, { min: f.min, max: f.max }),
            cueKey: path,
            control: numberField({
                input: 'batching',
                value: ui.form.local.Batching[f.key],
                min: f.min,
                max: f.max,
                attrs: { 'data-tgj-key': f.key, 'data-tgj-field': path, 'data-tgj-save': path, 'aria-label': text(S, f.label) },
            }),
        });
    });
    return card({ title: text(S, 'web.batching.title'), body: rows.join('') });
}

/**
 * The update-check card under Advanced: the Check switch and the three kinds, each saved at once.
 * @param {object | null} settings
 * @param {Record<string, string>} S
 * @param {{ form?: { local: object } }} ui form is state.forms.settings
 * @returns {string} HTML
 */
export function renderUpdates(settings, S, ui) {
    if (!settings || !ui.form) {
        return '';
    }

    const local = ui.form.local.UpdateCheck;
    const enabled = text(S, 'settings.updates.enabled');
    const body = row({
        label: enabled,
        cueKey: 'UpdateCheck.Enabled',
        control: switchControl({
            input: 'updEnabled',
            checked: Boolean(local.Enabled),
            label: enabled,
            attrs: { 'data-tgj-field': 'UpdateCheck.Enabled', 'data-tgj-save': 'UpdateCheck.Enabled' },
        }),
    }) + UPDATE_KINDS.map(kind => {
        const path = `UpdateCheck.${kind.key}`;
        const name = text(S, kind.label);
        return row({
            label: name,
            hint: text(S, kind.hint),
            cueKey: path,
            control: switchControl({
                input: kind.input,
                checked: Boolean(local[kind.key]),
                label: name,
                attrs: { 'data-tgj-field': path, 'data-tgj-save': path },
            }),
        });
    }).join('');
    return card({ title: text(S, 'web.updates.title'), description: text(S, 'settings.updates.hint'), body });
}

/**
 * The Bot token card under Advanced: the token masked (TokenHint), and in full after Show until Hide or leaving the
 * page. The full token is only in the page's memory, never in the state the page keeps or in a log.
 * @param {object | null} status the StatusDto
 * @param {Record<string, string>} S
 * @param {{ token?: string }} ui token is the revealed token, or '' while masked
 * @returns {string} HTML, or '' without a bot
 */
export function renderToken(status, S, ui) {
    if (!status || !status.HasToken) {
        return '';
    }

    const shown = typeof ui.token === 'string' && ui.token !== '';
    const value = shown ? ui.token : (status.TokenHint ?? '');
    const button = shown
        ? actionButton({ label: text(S, 'web.token.hide'), act: 'hideToken' })
        : actionButton({ label: text(S, 'web.token.show'), act: 'showToken' });
    return card({
        title: text(S, 'web.token.title'),
        body: `<p class="tgj-row-hint">${t(S, 'web.token.hint')}</p>`
            + row({
                label: text(S, 'web.token.title'),
                extraClass: 'tgj-row--stack',
                control: `<code class="tgj-token" aria-label="${esc(text(S, 'web.token.title'))}">${esc(value)}</code>${button}`,
            }),
    });
}

/**
 * The diagnostics block under Advanced: Copy diagnostics, and the text itself when the clipboard refused it.
 * @param {Record<string, string>} S
 * @param {{ text?: string, copied?: boolean }} ui
 * @returns {string} HTML
 */
export function renderDiagnostics(S, ui) {
    const parts = [`<div class="tgj-actions">${actionButton({ label: text(S, 'web.diag.copy'), act: 'copyDiagnostics' })}</div>`];
    if (ui.copied) {
        parts.push(`<p class="tgj-row-hint">${t(S, 'web.diag.copied')}</p>`);
    }

    if (ui.text) {
        parts.push(`<p class="tgj-row-hint">${t(S, 'web.diag.manual')}</p>`
            + `<textarea id="tgj-diagnostics-text" class="tgj-field tgj-diag-text" readonly rows="12">${esc(ui.text)}</textarea>`);
    }

    const info = notice({ kind: 'info', html: `<p>${t(S, 'web.diag.contains')}</p><p>${t(S, 'web.diag.never')}</p><p>${t(S, 'web.diag.why')}</p>` });
    return card({ title: text(S, 'web.diag.title'), body: info + parts.join('') });
}
