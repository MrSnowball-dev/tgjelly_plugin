// Test doubles for the dashboard page (tests/web). No dependencies: Node 22's built-ins only.

/**
 * A fake page element that records what the controller writes to it. Like a browser, reading innerHTML gives the
 * serialized markup, which differs from what was written (&#39; comes back as '); writes counts innerHTML assignments.
 * It keeps attributes (setAttribute and friends), and has no fields inside unless liveFields gives it some.
 * @param {string} id
 * @param {(html: string) => void} [onWrite] called with each written markup (makeView registers the ids in it)
 */
export function makeElement(id, onWrite = () => {}) {
    let html = '';
    const attributes = new Map();
    return {
        id,
        textContent: '',
        hidden: false,
        writes: 0,
        attributes,
        get innerHTML() {
            return html;
        },
        set innerHTML(value) {
            this.writes++;
            html = String(value).replaceAll('&#39;', "'");
            onWrite(html);
        },
        getAttribute: name => (attributes.has(name) ? attributes.get(name) : null),
        setAttribute: (name, value) => attributes.set(name, String(value)),
        removeAttribute: name => attributes.delete(name),
        hasAttribute: name => attributes.has(name),
        contains: () => false,
        querySelector: () => null,
    };
}

/**
 * A fake Jellyfin view: an EventTarget with isConnected, classList and querySelector('#id').
 * Like a browser, markup written to any of its elements creates the elements whose id="…" it holds (an ID already
 * there keeps its element, so a test's handle on it stays good).
 * @param {string[]} ids the element IDs the page has before the controller writes anything
 */
export function makeView(ids) {
    const view = new EventTarget();
    const classes = new Set();
    const elements = new Map();
    const register = html => {
        for (const match of html.matchAll(/\sid="([^"]+)"/g)) {
            if (!elements.has(match[1])) {
                elements.set(match[1], makeElement(match[1], register));
            }
        }
    };
    for (const id of ids) {
        elements.set(id, makeElement(id, register));
    }

    view.isConnected = true;
    view.classList = {
        contains: name => classes.has(name),
        add: name => classes.add(name),
        remove: name => classes.delete(name),
    };
    view.querySelector = selector => (selector.startsWith('#') ? elements.get(selector.slice(1)) ?? null : null);
    // Inputs a test registers to stand for rendered form fields: querySelectorAll('[data-tgj-input="x"]') finds them by x.
    view.fields = [];
    view.querySelectorAll = selector => {
        const name = /data-tgj-input="(\w+)"/.exec(selector)?.[1];
        return view.fields.filter(field => field.name === name);
    };
    view.elements = elements;
    return view;
}

const ENTITIES = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" };
const unescapeHtml = text => text.replace(/&(amp|lt|gt|quot|#39);/g, entity => ENTITIES[entity]);
const escapeAttr = value => String(value).replace(/[&<>"']/g, c => Object.keys(ENTITIES).find(k => ENTITIES[k] === c));

/** The value a field gets from markup: the value="…" of the input, the textarea's text, or the selected option's value. */
function valueIn(html, { tag, attributes }) {
    const tags = tag === 'textarea'
        ? html.matchAll(/<textarea\b([^>]*)>([\s\S]*?)<\/textarea>/g)
        : tag === 'select'
            ? html.matchAll(/<select\b([^>]*)>([\s\S]*?)<\/select>/g)
            : html.matchAll(/<input\b([^>]*)>()/g);
    for (const [, attrs, inner] of tags) {
        if (!Object.entries(attributes).every(([name, value]) => attrs.includes(` ${name}="${escapeAttr(value)}"`))) {
            continue;
        }

        if (tag === 'textarea') {
            return unescapeHtml(inner);
        }

        if (tag === 'select') {
            const option = /<option value="([^"]*)" selected/.exec(inner) ?? /<option value="([^"]*)"/.exec(inner);
            return option ? unescapeHtml(option[1]) : '';
        }

        const value = /\svalue="([^"]*)"/.exec(attrs);
        return value ? unescapeHtml(value[1]) : '';
    }

    return '';
}

/** Whether a field matches a selector made of [name="value"] parts only, as fieldSelector builds. */
function matchesSelector(field, selector) {
    const parts = [...selector.matchAll(/\[([\w-]+)="((?:[^"\\]|\\.)*)"\]/g)];
    if (parts.length === 0 || parts.map(p => p[0]).join('') !== selector) {
        return false;
    }

    return parts.every(([, name, value]) => field.getAttribute(name) === value.replace(/\\(.)/g, '$1'));
}

/**
 * Fake form fields inside a host element. Like a browser, every innerHTML write of the host replaces each field with
 * a new, unfocused one, whose value (and defaultValue) comes from the new markup; a focused field that goes leaves
 * doc.activeElement empty. The host's contains() and querySelector() find the current fields.
 * @param {ReturnType<typeof makeView>} view
 * @param {{ activeElement?: unknown }} doc the fake document
 * @param {string} hostId
 * @param {{ tag: 'input' | 'textarea' | 'select', attributes: Record<string, string> }[]} specs the fields, by the
 *   attributes that find each one in the markup
 * @returns {(index?: number) => object} the current field at index (default 0)
 */
export function liveFields(view, doc, hostId, specs) {
    const host = view.elements.get(hostId);
    const make = spec => {
        const value = valueIn(host.innerHTML, spec);
        return {
            tagName: spec.tag.toUpperCase(),
            type: spec.tag === 'input' ? spec.attributes.type ?? 'text' : spec.tag,
            value,
            defaultValue: value,
            checked: false,
            selectionStart: 0,
            selectionEnd: 0,
            selectionDirection: 'none',
            scrollTop: 0,
            scrollLeft: 0,
            focusCalls: 0,
            getAttribute: name => spec.attributes[name] ?? null,
            focus(options) {
                this.focusCalls++;
                this.focusOptions = options;
                doc.activeElement = this;
            },
            blur() {
                if (doc.activeElement === this) {
                    doc.activeElement = null;
                }
            },
            setSelectionRange(start, end, direction) {
                Object.assign(this, { selectionStart: start, selectionEnd: end, selectionDirection: direction });
            },
        };
    };

    let fields = specs.map(make);
    const { get, set } = Object.getOwnPropertyDescriptor(host, 'innerHTML');
    Object.defineProperty(host, 'innerHTML', {
        get,
        set(value) {
            set.call(host, value);
            if (fields.includes(doc.activeElement)) {
                doc.activeElement = null;
            }

            fields = specs.map(make);
        },
    });
    host.contains = node => fields.includes(node);
    host.querySelector = selector => fields.find(field => matchesSelector(field, selector)) ?? null;
    return (index = 0) => fields[index];
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

/**
 * Fake setInterval/clearInterval that count the live intervals and run them on tick(), and fake setTimeout/clearTimeout
 * whose pending callbacks run (once) on runTimeouts().
 */
export function fakeTimers() {
    const live = new Map();
    const timeouts = new Map();
    let next = 1;
    return {
        setTimeout: (fn, ms) => {
            const id = next++;
            timeouts.set(id, { fn, ms });
            return id;
        },
        clearTimeout: id => {
            timeouts.delete(id);
        },
        timeouts: () => [...timeouts.values()].map(v => v.ms),
        runTimeouts: () => {
            const due = [...timeouts.values()];
            timeouts.clear();
            for (const { fn } of due) {
                fn();
            }
        },
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
 * Dispatches an event on the view whose target is a fake element carrying the given attributes, the way an event on a
 * rendered element bubbles to the view's delegated listeners. closest('[attr]') finds the element when it has attr.
 * @param {EventTarget} view
 * @param {string} type e.g. 'click', 'change', 'input'
 * @param {Record<string, string>} attributes
 * @param {object} [extra] more properties for the target, e.g. { value: 'x' }
 */
export function dispatchOn(view, type, attributes, extra = {}) {
    const target = {
        disabled: false,
        ...extra,
        getAttribute: name => attributes[name] ?? null,
        closest: selector => {
            const match = /^\[([\w-]+)\]$/.exec(selector);
            return match && match[1] in attributes ? target : null;
        },
    };
    const event = new Event(type, { cancelable: true });
    Object.defineProperty(event, 'target', { value: target });
    view.dispatchEvent(event);
    return { event, target };
}

/** A click on a rendered [data-tgj-act] element; see dispatchOn. */
export function clickOn(view, attributes) {
    return dispatchOn(view, 'click', attributes);
}
