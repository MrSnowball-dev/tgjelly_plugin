// Test doubles for the dashboard page (tests/web). No dependencies: Node 22's built-ins only.

/**
 * A fake page element that records what the controller writes to it.
 * @param {string} id
 */
export function makeElement(id) {
    return { id, innerHTML: '', textContent: '', hidden: false };
}

/**
 * A fake Jellyfin view: an EventTarget with isConnected, classList and querySelector('#id').
 * @param {string[]} ids the element IDs the page shell has
 */
export function makeView(ids) {
    const view = new EventTarget();
    const classes = new Set();
    const elements = new Map(ids.map(id => [id, makeElement(id)]));

    view.isConnected = true;
    view.classList = {
        contains: name => classes.has(name),
        add: name => classes.add(name),
        remove: name => classes.delete(name),
    };
    view.querySelector = selector => (selector.startsWith('#') ? elements.get(selector.slice(1)) ?? null : null);
    view.elements = elements;
    return view;
}

/**
 * A Response-like value, as jellyfin-apiclient's fetch resolves (2xx) or rejects with (non-2xx).
 * @param {number} status
 * @param {unknown} body JSON body, or undefined for none
 */
export function makeResponse(status, body) {
    const text = body === undefined ? '' : JSON.stringify(body);
    return {
        status,
        ok: status >= 200 && status < 300,
        json: async () => JSON.parse(text),
        text: async () => text,
    };
}

/**
 * A fake ApiClient. getUrl prefixes /jf/; fetch records every call and answers from
 * routes['<TYPE> <path without query>'], which is a body (200), a { status, body } pair, or a function of the request.
 * A route missing from the table answers 404; routes['NETWORK'] = true makes every call fail without a status.
 * @param {Record<string, unknown>} routes
 */
export function makeApiClient(routes) {
    const calls = [];
    return {
        calls,
        getUrl: path => '/jf/' + path,
        fetch: async request => {
            calls.push(request);
            if (routes.NETWORK) {
                throw new TypeError('Failed to fetch');
            }

            const path = request.url.replace(/^\/jf\//, '').replace(/\?.*$/, '');
            const key = `${request.type} ${path}`;
            let answer = key in routes ? routes[key] : { status: 404, body: { Message: 'not found' } };
            if (typeof answer === 'function') {
                answer = answer(request);
            }

            const pair = answer && typeof answer === 'object' && 'status' in answer ? answer : { status: 200, body: answer };
            const response = makeResponse(pair.status, pair.body);
            if (!response.ok) {
                throw response;
            }

            return response;
        },
    };
}

/** Fake setInterval/clearInterval that count the live intervals and run them on tick(). */
export function fakeTimers() {
    const live = new Map();
    let next = 1;
    return {
        setInterval: (fn, ms) => {
            const id = next++;
            live.set(id, { fn, ms });
            return id;
        },
        clearInterval: id => {
            live.delete(id);
        },
        active: () => live.size,
        intervals: () => [...live.values()].map(v => v.ms),
        tick: () => {
            for (const { fn } of [...live.values()]) {
                fn();
            }
        },
    };
}

/** Lets every pending promise chain settle. */
export async function flush() {
    for (let i = 0; i < 5; i++) {
        await new Promise(resolve => setImmediate(resolve));
    }
}

/**
 * Dispatches a click on the view whose target is a fake element carrying the given attributes,
 * the way a click on a rendered [data-tgj-act] button reaches the delegated listener.
 * @param {EventTarget} view
 * @param {Record<string, string>} attributes
 */
export function clickOn(view, attributes) {
    const target = {
        disabled: false,
        getAttribute: name => attributes[name] ?? null,
        closest: selector => (selector === '[data-tgj-act]' && 'data-tgj-act' in attributes ? target : null),
    };
    const event = new Event('click', { cancelable: true });
    Object.defineProperty(event, 'target', { value: target });
    view.dispatchEvent(event);
    return { event, target };
}
