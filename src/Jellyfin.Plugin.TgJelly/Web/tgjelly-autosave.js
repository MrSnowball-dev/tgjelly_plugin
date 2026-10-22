// The page-wide auto-save queue and its per-field cues (plan 8, design §6). Pure: no DOM, no string lookups. The controller
// owns the clock choice, the text of the cues and the state updates; this module owns ordering, timing and conflict retry.
//
// createAutosave({ clock, onCue, onSettled }) -> { change(job), flush(key?), cancel(key) }
//   clock    { now(), setTimeout(fn, ms), clearTimeout(handle) }
//   job      { key, mode: 'now' | 'debounce' | 'blur', delay?, validate?: () => string | null, send: () => Promise<SaveResult> }
//   SaveResult  { status: 'saved' } | { status: 'invalid', message } | { status: 'failed', message }
//             | { status: 'conflict', retry: () => Promise<SaveResult> }
//   onCue(key, { state: 'saving' | 'saved' | 'error' | 'idle', text? })   text only on 'error': the reason, or for a second
//                                                                          conflict the string key 'web.autosave.conflict'
//   onSettled(key, result)   the final result of a job (after any conflict retry)
//   A job whose key changed again while it was in flight is outdated: its result still reaches onSettled, but it shows no
//   cue, so the newer change's cue (say, an error from its validate) stays.

export const SAVING_AFTER_MS = 300;
export const SAVED_FOR_MS = 2000;
export const CONFLICT_KEY = 'web.autosave.conflict';

export function createAutosave({ clock, onCue, onSettled }) {
    let seq = 0;
    const waiting = new Map();   // key -> { seq, job, timer }: debounce and blur jobs not yet due
    let ready = [];              // { key, job }: due, in arrival order, not yet sent
    let running = false;
    const idleTimers = new Map();   // key -> handle of the pending 'idle' cue
    const cueState = new Map();     // key -> last state emitted
    const changes = new Map();      // key -> how many changes it has had: a sent job compares it to tell if it is outdated
    let drained = [];               // resolvers waiting for an empty queue

    function cue(key, state, text) {
        cueState.set(key, state);
        onCue(key, text === undefined ? { state } : { state, text });
    }

    function clearIdle(key) {
        if (idleTimers.has(key)) { clock.clearTimeout(idleTimers.get(key)); idleTimers.delete(key); }
    }

    function showSaved(key) {
        clearIdle(key);
        cue(key, 'saved');
        // This idle cue is what removes the cue under prefers-reduced-motion, so it always fires.
        idleTimers.set(key, clock.setTimeout(() => {
            idleTimers.delete(key);
            // A newer cue (saving, error) that arrived meanwhile must not be replaced by a stale idle.
            if (cueState.get(key) === 'saved') cue(key, 'idle');
        }, SAVED_FOR_MS));
    }

    function showError(key, text) {
        clearIdle(key);
        cue(key, 'error', text);
    }

    function check(job) {
        if (!job.validate) return null;
        try { return job.validate() || null; } catch (error) { return (error && error.message) || String(error); }
    }

    function dropQueued(key) {
        ready = ready.filter((e) => e.key !== key);
    }

    function dropWaiting(key) {
        const w = waiting.get(key);
        if (w) { if (w.timer !== undefined) clock.clearTimeout(w.timer); waiting.delete(key); }
    }

    function enqueue(key, job) {
        const at = ready.findIndex((e) => e.key === key);
        if (at >= 0) ready[at] = { key, job };   // a newer change replaces a queued, unsent one in place
        else ready.push({ key, job });
        pump();
    }

    function becomeDue(key) {
        const w = waiting.get(key);
        if (!w) return;
        waiting.delete(key);
        if (w.timer !== undefined) clock.clearTimeout(w.timer);
        const error = check(w.job);
        if (error) { onSettled(key, { status: 'invalid', message: error }); showError(key, error); return; }
        enqueue(key, w.job);
    }

    function change(job) {
        const { key } = job;
        changes.set(key, (changes.get(key) ?? 0) + 1);
        dropWaiting(key);
        if (cueState.get(key) === 'error') { clearIdle(key); cue(key, 'idle'); }
        if (job.mode === 'blur') {
            waiting.set(key, { seq: ++seq, job });
            return;
        }
        const error = check(job);
        if (error) {
            dropQueued(key);
            onSettled(key, { status: 'invalid', message: error });
            showError(key, error);
            return;
        }
        if (job.mode === 'debounce') {
            const w = { seq: ++seq, job, timer: undefined };
            w.timer = clock.setTimeout(() => becomeDue(key), job.delay ?? 0);
            waiting.set(key, w);
            return;
        }
        enqueue(key, job);
    }

    function flush(key) {
        const due = [...waiting.entries()].filter(([k]) => key === undefined || k === key).sort((a, b) => a[1].seq - b[1].seq);
        for (const [k] of due) becomeDue(k);
        return new Promise((resolve) => {
            if (!running && ready.length === 0) resolve();
            else drained.push(resolve);
        });
    }

    function cancel(key) {
        dropWaiting(key);
        dropQueued(key);
        clearIdle(key);
        cue(key, 'idle');
    }

    async function attempt(send) {
        try { return await send(); } catch (error) { return { status: 'failed', message: (error && error.message) || String(error) }; }
    }

    async function run({ key, job }) {
        // A change to the same key made while this job is in flight is newer: its cue (an error, or none while it waits)
        // must not be replaced by this job's outcome, which is about a value the admin has already changed.
        const taken = changes.get(key);
        const outdated = () => changes.get(key) !== taken;
        let savingTimer = clock.setTimeout(() => { savingTimer = undefined; if (!outdated()) cue(key, 'saving'); }, SAVING_AFTER_MS);
        let result = await attempt(job.send);
        if (result.status === 'conflict') {
            // The caller took the newer server data and re-applies the pending change inside retry(); once only.
            result = typeof result.retry === 'function' ? await attempt(result.retry) : { status: 'failed', message: CONFLICT_KEY };
            if (result.status === 'conflict') result = { status: 'failed', message: CONFLICT_KEY };
        }
        if (savingTimer !== undefined) clock.clearTimeout(savingTimer);
        onSettled(key, result);
        if (outdated()) {
            if (cueState.get(key) === 'saving') cue(key, 'idle');
        } else if (result.status === 'saved') showSaved(key);
        else showError(key, result.message ?? '');
    }

    async function pump() {
        if (running) return;
        running = true;
        try {
            while (ready.length > 0) await run(ready.shift());
        } finally {
            running = false;
            const done = drained;
            drained = [];
            for (const resolve of done) resolve();
        }
    }

    return { change, flush, cancel };
}
