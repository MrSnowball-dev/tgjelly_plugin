// Auto-save queue tests. Pure module, driven by a fake clock.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAutosave } from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly-autosave.js';

function fakeClock() {
    let t = 0, id = 0;
    const timers = new Map();
    return {
        now: () => t,
        setTimeout: (fn, ms) => { timers.set(++id, { at: t + ms, fn, id }); return id; },
        clearTimeout: (h) => { timers.delete(h); },
        // Advance time, firing due timers in order, letting promise continuations run between them.
        async tick(ms) {
            const end = t + ms;
            for (;;) {
                await flushMicrotasks();
                const due = [...timers.values()].filter((x) => x.at <= end).sort((a, b) => a.at - b.at || a.id - b.id)[0];
                if (!due) break;
                timers.delete(due.id);
                t = Math.max(t, due.at);
                due.fn();
            }
            t = end;
            await flushMicrotasks();
        },
    };
}
const flushMicrotasks = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };

function setup() {
    const clock = fakeClock();
    const cues = [], settled = [];
    const q = createAutosave({
        clock,
        onCue: (key, cue) => cues.push([key, cue.state, cue.text]),
        onSettled: (key, result) => settled.push([key, result.status]),
    });
    return { clock, q, cues, settled };
}
const states = (cues, key) => cues.filter((c) => c[0] === key).map((c) => c[1]);
const deferred = () => { let resolve; const promise = new Promise((r) => { resolve = r; }); return { promise, resolve }; };

test('now_sendsImmediately_cueSavedThenIdle', async () => {
    const { clock, q, cues, settled } = setup();
    let sent = 0;
    q.change({ key: 'a', mode: 'now', send: async () => { sent++; return { status: 'saved' }; } });
    await clock.tick(0);
    assert.equal(sent, 1);
    assert.deepEqual(states(cues, 'a'), ['saved']);
    await clock.tick(1999);
    assert.deepEqual(states(cues, 'a'), ['saved']);
    await clock.tick(1);
    assert.deepEqual(states(cues, 'a'), ['saved', 'idle']);
    assert.deepEqual(settled, [['a', 'saved']]);
});

test('debounce_sendsOnceAfterLastChange', async () => {
    const { clock, q } = setup();
    const sent = [];
    const job = (v) => ({ key: 'a', mode: 'debounce', delay: 800, send: async () => { sent.push(v); return { status: 'saved' }; } });
    q.change(job(1));
    await clock.tick(500);
    q.change(job(2));
    await clock.tick(500);
    assert.deepEqual(sent, []);
    await clock.tick(300);
    assert.deepEqual(sent, [2]);
});

test('blur_waitsForFlush', async () => {
    const { clock, q } = setup();
    let sent = 0;
    q.change({ key: 'a', mode: 'blur', send: async () => { sent++; return { status: 'saved' }; } });
    await clock.tick(60000);
    assert.equal(sent, 0);
    await q.flush('a');
    assert.equal(sent, 1);
});

test('validateError_neverSends_cueError', async () => {
    const { clock, q, cues } = setup();
    let sent = 0;
    q.change({ key: 'a', mode: 'now', validate: () => 'must be a number', send: async () => { sent++; return { status: 'saved' }; } });
    await clock.tick(5000);
    assert.equal(sent, 0);
    assert.deepEqual(cues.filter((c) => c[0] === 'a'), [['a', 'error', 'must be a number']]);
});

test('queue_serializesSavesAcrossSections', async () => {
    const { clock, q } = setup();
    const first = deferred();
    const log = [];
    q.change({ key: 'settings.x', mode: 'now', send: async () => { log.push('s1'); await first.promise; log.push('e1'); return { status: 'saved' }; } });
    q.change({ key: 'template:y', mode: 'now', send: async () => { log.push('s2'); return { status: 'saved' }; } });
    await clock.tick(0);
    assert.deepEqual(log, ['s1']);
    first.resolve();
    await clock.tick(0);
    assert.deepEqual(log, ['s1', 'e1', 's2']);
});

test('newerChange_replacesQueued', async () => {
    const { clock, q } = setup();
    const first = deferred();
    const sent = [];
    q.change({ key: 'a', mode: 'now', send: async () => { await first.promise; return { status: 'saved' }; } });
    q.change({ key: 'b', mode: 'now', send: async () => { sent.push('b1'); return { status: 'saved' }; } });
    q.change({ key: 'b', mode: 'now', send: async () => { sent.push('b2'); return { status: 'saved' }; } });
    await clock.tick(0);
    first.resolve();
    await clock.tick(0);
    assert.deepEqual(sent, ['b2']);
});

test('saving_cueOnlyAfter300ms', async () => {
    const { clock, q, cues } = setup();
    const slow = deferred(), fast = deferred();
    q.change({ key: 'fast', mode: 'now', send: () => fast.promise });
    await clock.tick(299);
    assert.deepEqual(states(cues, 'fast'), []);
    fast.resolve({ status: 'saved' });
    await clock.tick(0);
    assert.deepEqual(states(cues, 'fast'), ['saved']);
    await clock.tick(1000);
    assert.deepEqual(states(cues, 'fast'), ['saved']);
    await clock.tick(1000);
    assert.deepEqual(states(cues, 'fast'), ['saved', 'idle']);
    q.change({ key: 'slow', mode: 'now', send: () => slow.promise });
    await clock.tick(299);
    assert.deepEqual(states(cues, 'slow'), []);
    await clock.tick(1);
    assert.deepEqual(states(cues, 'slow'), ['saving']);
    slow.resolve({ status: 'saved' });
    await clock.tick(0);
    assert.deepEqual(states(cues, 'slow'), ['saving', 'saved']);
});

test('conflict_reappliesPendingChangeOnce', async () => {
    const { clock, q, cues, settled } = setup();
    let retried = 0;
    q.change({ key: 'a', mode: 'now', send: async () => ({ status: 'conflict', retry: async () => { retried++; return { status: 'saved' }; } }) });
    await clock.tick(0);
    assert.equal(retried, 1);
    assert.deepEqual(states(cues, 'a'), ['saved']);
    assert.deepEqual(settled, [['a', 'saved']]);
});

test('secondConflict_cueError', async () => {
    const { clock, q, cues } = setup();
    let retried = 0;
    const again = async () => { retried++; return { status: 'conflict', retry: async () => { retried++; return { status: 'saved' }; } }; };
    q.change({ key: 'a', mode: 'now', send: async () => ({ status: 'conflict', retry: again }) });
    await clock.tick(0);
    assert.equal(retried, 1);
    assert.deepEqual(cues.filter((c) => c[0] === 'a'), [['a', 'error', 'web.autosave.conflict']]);
});

test('failed_cueErrorStaysUntilNextChange', async () => {
    const { clock, q, cues } = setup();
    q.change({ key: 'a', mode: 'now', send: async () => ({ status: 'failed', message: 'boom' }) });
    await clock.tick(60000);
    assert.deepEqual(cues.filter((c) => c[0] === 'a'), [['a', 'error', 'boom']]);
    q.change({ key: 'a', mode: 'debounce', delay: 800, send: async () => ({ status: 'saved' }) });
    assert.deepEqual(states(cues, 'a'), ['error', 'idle']);
    await clock.tick(800);
    assert.deepEqual(states(cues, 'a'), ['error', 'idle', 'saved']);
});

test('pendingSave_flushesOnHide', async () => {
    const { clock, q } = setup();
    const sent = [];
    q.change({ key: 'a', mode: 'debounce', delay: 800, send: async () => { sent.push('a'); return { status: 'saved' }; } });
    q.change({ key: 'b', mode: 'blur', send: async () => { sent.push('b'); return { status: 'saved' }; } });
    await clock.tick(100);
    await q.flush();
    assert.deepEqual(sent, ['a', 'b']);
    await clock.tick(5000);
    assert.deepEqual(sent, ['a', 'b']);
});

test('throwingSend_isFailed_andCancelDropsPending', async () => {
    const { clock, q, cues } = setup();
    q.change({ key: 'a', mode: 'now', send: async () => { throw new Error('net down'); } });
    let sent = 0;
    q.change({ key: 'b', mode: 'debounce', delay: 800, send: async () => { sent++; return { status: 'saved' }; } });
    q.cancel('b');
    await clock.tick(5000);
    assert.equal(sent, 0);
    assert.deepEqual(cues.filter((c) => c[0] === 'a'), [['a', 'error', 'net down']]);
    assert.deepEqual(states(cues, 'b'), ['idle']);
});

test('throwingSend_cueUsesTheSuppliedDescription', async () => {
    const cues = [];
    const clock = fakeClock();
    const q = createAutosave({ clock, onCue: (key, cue) => cues.push([key, cue.state, cue.text]), onSettled: () => {}, describeError: err => `cause: ${err.message}; next step` });
    q.change({ key: 'a', mode: 'now', send: async () => { throw new Error('net down'); } });
    await clock.tick(5000);
    assert.deepEqual(cues.filter((c) => c[0] === 'a'), [['a', 'error', 'cause: net down; next step']]);
});

test('staleIdle_doesNotReplaceNewerCue', async () => {
    const { clock, q, cues } = setup();
    q.change({ key: 'a', mode: 'now', send: async () => ({ status: 'saved' }) });
    await clock.tick(500);
    const slow = deferred();
    q.change({ key: 'a', mode: 'now', send: () => slow.promise });
    await clock.tick(1600);   // t=2100: past the first saved's idle at 2000, request in flight since 500
    assert.deepEqual(states(cues, 'a'), ['saved', 'saving']);
    slow.resolve({ status: 'saved' });
    await clock.tick(0);
    assert.deepEqual(states(cues, 'a'), ['saved', 'saving', 'saved']);
    await clock.tick(1999);
    assert.deepEqual(states(cues, 'a'), ['saved', 'saving', 'saved']);
    await clock.tick(1);
    assert.deepEqual(states(cues, 'a'), ['saved', 'saving', 'saved', 'idle']);
});

test('outdatedResult_keepsTheNewerChangesCue', async () => {
    const { clock, q, cues, settled } = setup();
    const first = deferred();
    q.change({ key: 'proxy', mode: 'now', send: () => first.promise });
    await clock.tick(400);
    assert.deepEqual(states(cues, 'proxy'), ['saving']);

    // A newer change for the same key, invalid, while the first is in flight.
    q.change({ key: 'proxy', mode: 'blur', validate: () => 'enter the proxy address', send: async () => ({ status: 'saved' }) });
    q.flush('proxy');   // not awaited: it resolves once the queue is empty, and the first save is still in flight
    await clock.tick(0);
    assert.deepEqual(cues.at(-1), ['proxy', 'error', 'enter the proxy address']);

    first.resolve({ status: 'saved' });
    await clock.tick(5000);
    assert.deepEqual(cues.at(-1), ['proxy', 'error', 'enter the proxy address'], 'the outdated Saved does not replace it');
    assert.ok(!states(cues, 'proxy').includes('saved'));
    assert.deepEqual(settled, [['proxy', 'invalid'], ['proxy', 'saved']], 'the result still reaches onSettled');
});

test('outdatedResult_clearsItsOwnSavingCue', async () => {
    const { clock, q, cues } = setup();
    const first = deferred();
    q.change({ key: 'a', mode: 'now', send: () => first.promise });
    await clock.tick(400);
    q.change({ key: 'a', mode: 'blur', send: async () => ({ status: 'saved' }) });
    first.resolve({ status: 'failed', message: 'x' });
    await clock.tick(0);
    assert.deepEqual(states(cues, 'a'), ['saving', 'idle'], 'no error for a value already changed; the waiting change has no cue yet');
    await q.flush('a');
    assert.deepEqual(states(cues, 'a'), ['saving', 'idle', 'saved']);
});

test('newerBlurChange_replacesAQueuedDueJob_invalidValueNeverSent', async () => {
    const { clock, q, cues } = setup();
    const busy = deferred();
    // Like the controller's sends, this one reads the field when it runs, not when it was queued.
    let value = 'https://media.example';
    const sent = [];
    const urlJob = () => ({
        key: 'url',
        mode: 'blur',
        validate: () => (value.includes('.') ? null : 'enter the full address'),
        send: async () => { sent.push(value); return { status: 'saved' }; },
    });
    q.change({ key: 'other', mode: 'now', send: () => busy.promise });
    await clock.tick(0);
    q.change(urlJob());
    q.flush('url');   // due and checked, queued behind the save in flight
    value = 'https://media';
    q.change(urlJob());   // edited again while it waits: not checked until the next blur
    busy.resolve({ status: 'saved' });
    await clock.tick(5000);
    assert.deepEqual(sent, [], 'nothing is sent for the key until the next blur');

    await q.flush('url');
    assert.deepEqual(sent, [], 'the invalid value is never sent');
    assert.deepEqual(cues.at(-1), ['url', 'error', 'enter the full address']);
    value = 'https://media.example.org';
    q.change(urlJob());
    await q.flush('url');
    assert.deepEqual(sent, ['https://media.example.org']);
});

test('newerDebounceChange_replacesAQueuedDueJob', async () => {
    const { clock, q } = setup();
    const busy = deferred();
    let value = 1;
    const sent = [];
    const job = () => ({ key: 'a', mode: 'debounce', delay: 800, send: async () => { sent.push(value); return { status: 'saved' }; } });
    q.change({ key: 'other', mode: 'now', send: () => busy.promise });
    await clock.tick(0);
    q.change(job());
    await clock.tick(800);   // due, queued behind the save in flight
    value = 2;
    q.change(job());
    busy.resolve({ status: 'saved' });
    await clock.tick(799);
    assert.deepEqual(sent, [], 'the newer change waits out its own delay');
    await clock.tick(1);
    assert.deepEqual(sent, [2], 'and is sent once');
});
