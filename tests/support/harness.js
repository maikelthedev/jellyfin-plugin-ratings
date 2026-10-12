'use strict';

/**
 * A deliberately small browser shim, just big enough to load Web/ratings.js in Node and drive the
 * functions under test.
 *
 * It is not a browser: nothing is laid out, painted, animated or fetched. What it does model is the
 * part the plugin leans on hardest - handing a function an element, letting it assign innerHTML and
 * then asking that element for the things it just wrote, with querySelector/querySelectorAll. So
 * innerHTML is parsed into a real (if minimal) tree, and there is a small selector engine
 * underneath. Without that, a renderer that writes a page and immediately queries into it cannot be
 * tested at all.
 *
 * Class names, ids and attributes are modelled because the plugin reads them back.
 *
 * The plugin boots at the bottom of its IIFE and calls RatingsPlugin.init() as soon as
 * isUserAuthenticated() is true. ApiClient has no current user by default, so loading is inert and
 * a test decides what to call. Pass { authenticated: false } explicitly for that; pass a real user
 * id to walk the boot path.
 */

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SOURCE_PATH = path.join(__dirname, '..', '..', 'Web', 'ratings.js');

/** Elements that never have children, so the parser must not wait for a closing tag. */
const VOID_TAGS = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);

/**
 * What a browser's HTML fragment serialiser does to a text node: & < > escaped, quotes left alone.
 * escapeHtml() in the plugin relies on exactly this, so the shim has to model it or that function
 * under test would appear to return nothing.
 */
function escapeText(value) {
    return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Crude inverse, good enough for assertions on text content. */
function unescapeText(value) {
    return String(value)
        .replace(/<[^>]*>/g, '')
        .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
}

/**
 * Serialise a node back to markup, so innerHTML can be read as well as written.
 *
 * A renderer writes a shell into one element and then fills a child element, so the element a test
 * was handed does not hold the finished markup as a string any more - reading it back means walking
 * the tree that was built. Values are emitted exactly as they were parsed or assigned, because
 * parseHtml does not decode entities and there is no unescaping on the way back: re-encoding here
 * would turn every `&amp;` in an attribute into `&amp;amp;` and break an assign-then-read round trip.
 *
 * @param {object} node Element or text node to serialise.
 * @returns {string} Markup for the node and its children.
 */
function serialise(node) {
    if (node.nodeType === 3) {
        return node.textContent;
    }

    const attributes = Object.assign({}, node.attributes || {});
    // className and id are set directly as often as they are set through setAttribute.
    if (node.className && !Object.prototype.hasOwnProperty.call(attributes, 'class')) {
        attributes.class = node.className;
    }
    if (node._id && !Object.prototype.hasOwnProperty.call(attributes, 'id')) {
        attributes.id = node._id;
    }

    const name = node.tagName.toLowerCase();
    const attrs = Object.keys(attributes).map((key) => ' ' + key + '="' + attributes[key] + '"').join('');
    if (VOID_TAGS.has(name)) {
        return '<' + name + attrs + ' />';
    }
    return '<' + name + attrs + '>' + node.childNodes.map(serialise).join('') + '</' + name + '>';
}

/** Turn an attribute string into a map. */
function parseAttrs(text) {
    const attrs = {};
    const re = /([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*("[^"]*"|'[^']*'|[^\s"'>]+))?/g;
    let m;
    while ((m = re.exec(text)) !== null) {
        let value = m[2] === undefined ? '' : m[2];
        if (value.length > 1 && (value[0] === '"' || value[0] === "'")) {
            value = value.slice(1, -1);
        }
        attrs[m[1].toLowerCase()] = value;
    }
    return attrs;
}

/**
 * Parse an HTML fragment into nodes. Text, comments, nested elements, and style/script bodies
 * (whose contents may contain "<" that is not markup) are all handled; anything more exotic is not.
 *
 * @param {string} html Markup to parse.
 * @param {function} makeElement Factory for a new element.
 * @param {function} makeText Factory for a new text node.
 * @returns {Array} Child nodes.
 */
function parseHtml(html, makeElement, makeText) {
    const roots = [];
    const stack = [];
    let i = 0;

    const push = (node) => {
        const parent = stack[stack.length - 1];
        if (parent) {
            node.parentElement = parent;
            node.parentNode = parent;
            // Only elements belong in children; text nodes are child nodes, as in the DOM.
            if (node.nodeType === 1) { parent.children.push(node); }
            parent.childNodes.push(node);
        } else {
            roots.push(node);
        }
    };

    while (i < html.length) {
        const lt = html.indexOf('<', i);
        if (lt === -1) {
            push(makeText(html.slice(i)));
            break;
        }
        if (lt > i) {
            push(makeText(html.slice(i, lt)));
        }
        if (html.startsWith('<!--', lt)) {
            const end = html.indexOf('-->', lt);
            i = end === -1 ? html.length : end + 3;
            continue;
        }
        const gt = html.indexOf('>', lt);
        if (gt === -1) {
            push(makeText(html.slice(lt)));
            break;
        }
        const tag = html.slice(lt + 1, gt);
        if (tag.startsWith('/')) {
            // Close: unwind to the matching element, tolerating a missing open tag.
            const name = tag.slice(1).trim().toLowerCase();
            for (let s = stack.length - 1; s >= 0; s--) {
                if (stack[s].tagName.toLowerCase() === name) {
                    stack.length = s;
                    break;
                }
            }
            i = gt + 1;
            continue;
        }

        const selfClosing = tag.endsWith('/');
        const nameMatch = /^([a-zA-Z][-a-zA-Z0-9]*)/.exec(tag);
        if (!nameMatch) {
            i = gt + 1;
            continue;
        }
        const name = nameMatch[1];
        const el = makeElement(name);
        const attrs = parseAttrs(tag.slice(name.length).replace(/\/$/, ''));
        Object.keys(attrs).forEach((key) => el.setAttribute(key, attrs[key]));
        push(el);
        i = gt + 1;

        if (VOID_TAGS.has(name.toLowerCase()) || selfClosing) {
            continue;
        }
        // A style/script body is data, not markup: take it verbatim up to its closing tag.
        if (name.toLowerCase() === 'style' || name.toLowerCase() === 'script') {
            const close = html.toLowerCase().indexOf('</' + name.toLowerCase(), i);
            const end = close === -1 ? html.length : close;
            const body = html.slice(i, end);
            if (body) {
                push(makeText(body));
            }
            i = end;
            continue;
        }
        stack.push(el);
    }

    return roots;
}

/** Does this element match a single compound selector such as "div.lb-tab#x[data-y=z]"? */
function matchesCompound(el, token) {
    const re = /([a-zA-Z][-a-zA-Z0-9]*)|\.([-a-zA-Z0-9_]+)|#([-a-zA-Z0-9_]+)|\[([-a-zA-Z0-9_:-]+)(?:\s*=\s*("[^"]*"|'[^']*'|[^\]]+))?\]/g;
    let m;
    let matchedAny = false;
    while ((m = re.exec(token)) !== null) {
        matchedAny = true;
        if (m[1]) {
            if (el.tagName.toLowerCase() !== m[1].toLowerCase()) { return false; }
        } else if (m[2]) {
            if (!el.classList.contains(m[2])) { return false; }
        } else if (m[3]) {
            if (el.id !== m[3]) { return false; }
        } else if (m[4]) {
            const value = el.getAttribute(m[4]);
            if (value === null) { return false; }
            if (m[5] !== undefined) {
                const want = m[5].replace(/^["']|["']$/g, '');
                if (value !== want) { return false; }
            }
        }
    }
    return matchedAny;
}

/** Split a selector into steps of { combinator, token }. Supports " ", ">" and "," outside use. */
function selectorSteps(selector) {
    const steps = [];
    const parts = selector.trim().split(/\s*(>)\s*|\s+/).filter((p) => p !== undefined && p !== '');
    let combinator = ' ';
    parts.forEach((part) => {
        if (part === '>') {
            combinator = '>';
        } else {
            steps.push({ combinator, token: part });
            combinator = ' ';
        }
    });
    return steps;
}

/** Match an element against the steps, right to left. */
function matchesSteps(el, steps, index) {
    if (index < 0) { return true; }
    const step = steps[index];
    if (!matchesCompound(el, step.token)) { return false; }
    if (index === 0) { return true; }
    if (step.combinator === '>') {
        return Boolean(el.parentElement) && matchesSteps(el.parentElement, steps, index - 1);
    }
    let parent = el.parentElement;
    while (parent) {
        if (matchesSteps(parent, steps, index - 1)) { return true; }
        parent = parent.parentElement;
    }
    return false;
}

/**
 * Minimal element: records attributes, classes and children, and can be queried.
 *
 * A class rather than an object literal because the DOM surface is mostly accessors, and
 * Object.assign() would evaluate every getter while copying it.
 */
class Element {
    constructor(tagName) {
        this.tagName = String(tagName || 'div').toUpperCase();
        this.nodeType = 1;
        this._id = '';
        this.className = '';
        this.value = '';
        this.checked = false;
        this.disabled = false;
        this.hidden = false;
        this.open = false;
        this.children = [];     // elements only
        this.childNodes = [];   // elements and text (like the DOM, text is a child node)
        this.parentElement = null;
        this.parentNode = null;
        this.style = { setProperty() {}, removeProperty() {}, getPropertyValue: () => '', cssText: '' };
        this.dataset = {};
        this.attributes = {};
        this._html = '';
        this._text = '';
        this._listeners = {};
        this._makeElement = null;
        this._makeText = null;
        const set = new Set();
        this.classList = {
            add: (...names) => names.forEach((n) => set.add(n)),
            remove: (...names) => names.forEach((n) => set.delete(n)),
            toggle: (name, force) => {
                const on = force === undefined ? !set.has(name) : force;
                if (on) { set.add(name); } else { set.delete(name); }
                return on;
            },
            contains: (name) => set.has(name)
        };
    }

    get id() { return this._id; }

    set id(value) {
        this._id = String(value);
        if (this.ownerDocument && this.ownerDocument.__register) { this.ownerDocument.__register(this._id, this); }
    }

    /**
     * The markup as it stands now, not the string last assigned: a renderer writes a shell here and
     * then fills a child element, so the finished page can only be read back from the tree.
     */
    get innerHTML() { return this.childNodes.map(serialise).join('') || this._html; }

    set innerHTML(value) {
        this._html = String(value === null || value === undefined ? '' : value);
        this.children = [];
        this.childNodes = [];
        this._text = '';
        if (!this._makeElement) { return; }
        parseHtml(this._html, this._makeElement, this._makeText).forEach((node) => {
            node.parentElement = this;
            node.parentNode = this;
            if (node.nodeType === 1) { this.children.push(node); }
            this.childNodes.push(node);
        });
    }

    get textContent() {
        // Like the DOM: the text of this node's descendants, unless textContent was assigned here.
        if (this._text) { return this._text; }
        return this.childNodes.map((child) => (child.nodeType === 3 ? child.textContent : child.textContent)).join('');
    }

    set textContent(value) {
        this._text = value === null || value === undefined ? '' : String(value);
        this._html = escapeText(this._text);
        this.children = [];
        this.childNodes = [];
    }

    get outerHTML() { return '<' + this.tagName.toLowerCase() + '>' + this.innerHTML + '</' + this.tagName.toLowerCase() + '>'; }

    get firstChild() { return this.childNodes[0] || null; }

    get firstElementChild() { return this.children[0] || null; }

    get lastElementChild() { return this.children[this.children.length - 1] || null; }

    get nextElementSibling() {
        if (!this.parentElement) { return null; }
        const siblings = this.parentElement.children;
        return siblings[siblings.indexOf(this) + 1] || null;
    }

    get previousElementSibling() {
        if (!this.parentElement) { return null; }
        const siblings = this.parentElement.children;
        return siblings[siblings.indexOf(this) - 1] || null;
    }

    get childElementCount() { return this.children.length; }

    appendChild(child) {
        if (child) {
            child.parentElement = this;
            child.parentNode = this;
            this.childNodes.push(child);
            if (child.nodeType === 1) { this.children.push(child); }
        }
        return child;
    }

    insertBefore(child) {
        if (child) {
            child.parentElement = this;
            child.parentNode = this;
            this.childNodes.unshift(child);
            if (child.nodeType === 1) { this.children.unshift(child); }
        }
        return child;
    }

    removeChild(child) {
        this.childNodes = this.childNodes.filter((c) => c !== child);
        this.children = this.children.filter((c) => c !== child);
        return child;
    }

    replaceChild(next, old) {
        this.childNodes = this.childNodes.map((c) => (c === old ? next : c));
        this.children = this.children.map((c) => (c === old ? next : c));
        return old;
    }

    remove() { if (this.parentElement) { this.parentElement.removeChild(this); } }

    setAttribute(name, value) {
        this.attributes[name] = String(value);
        if (name === 'class') {
            this.className = String(value);
            const set = new Set(String(value).split(/\s+/).filter(Boolean));
            this.classList = {
                add: (...names) => names.forEach((n) => set.add(n)),
                remove: (...names) => names.forEach((n) => set.delete(n)),
                toggle: (n, force) => {
                    const on = force === undefined ? !set.has(n) : force;
                    if (on) { set.add(n); } else { set.delete(n); }
                    return on;
                },
                contains: (n) => set.has(n)
            };
        }
        if (name === 'id') { this.id = String(value); }   // registers with the document
    }

    getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null; }

    removeAttribute(name) { delete this.attributes[name]; }

    hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name); }

    addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); }

    removeEventListener(type, fn) { this._listeners[type] = (this._listeners[type] || []).filter((f) => f !== fn); }

    dispatch(type, event) { (this._listeners[type] || []).forEach((fn) => fn(event || { target: this })); }

    querySelector(selector) { return queryAll(this, selector)[0] || null; }

    querySelectorAll(selector) { return queryAll(this, selector); }

    getElementsByClassName(name) { return this.querySelectorAll('.' + name); }

    getElementsByTagName(name) { return this.querySelectorAll(name); }

    matches(selector) {
        return selector.split(',').some((part) => {
            const steps = selectorSteps(part);
            return matchesSteps(this, steps, steps.length - 1);
        });
    }

    closest(selector) {
        let node = this;
        while (node) {
            if (node.nodeType === 1 && typeof node.matches === 'function' && node.matches(selector)) { return node; }
            node = node.parentElement;
        }
        return null;
    }

    contains(other) {
        let node = other;
        while (node) {
            if (node === this) { return true; }
            node = node.parentElement;
        }
        return false;
    }

    focus() {}

    blur() {}

    click() { this.dispatch('click'); }

    play() { return Promise.resolve(); }

    pause() {}

    load() {}

    scrollIntoView() {}

    scrollBy() {}

    animate() { return { cancel() {}, finished: Promise.resolve() }; }

    getBoundingClientRect() { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; }
}

/** Text node, so that a rendered tree reads back like the DOM's. */
class TextNode {
    constructor(text) {
        this.nodeType = 3;
        this.textContent = String(text);
        this.nodeValue = String(text);
        this.parentElement = null;
        this.parentNode = null;
        this.children = [];
        this.classList = { contains: () => false, add() {}, remove() {}, toggle() { return false; } };
    }
}

function createElement(tagName) { return new Element(tagName); }

/** All elements under (and including) a root, in document order, matching a selector. */
function queryAll(root, selector, limit) {
    const found = [];
    const seen = new Set();
    const cap = limit || 200000;
    const walk = (node) => {
        if (found.length > cap) { return; }
        node.children.forEach((child) => {
            if (child.nodeType !== 1 || seen.has(child)) { return; }
            seen.add(child);
            if (selector.split(',').some((part) => {
                const steps = selectorSteps(part);
                return matchesSteps(child, steps, steps.length - 1);
            })) {
                found.push(child);
            }
            walk(child);
        });
    };
    walk(root);
    return found;
}

/** Document shim with an id registry so a test can plant the element a function looks for. */
function createDocument() {
    const makeElement = (tag) => {
        const el = createElement(tag);
        el.ownerDocument = doc;
        el._makeElement = makeElement;
        el._makeText = makeText;
        return el;
    };
    const makeText = (text) => new TextNode(text);

    const byId = new Map();
    const doc = {
        readyState: 'complete',
        currentScript: { src: 'https://jellyfin.test/Ratings/ratings.js?v=test' },
        title: '',
        hidden: false,
        visibilityState: 'visible',
        documentElement: null,
        head: null,
        body: null,
        cookie: '',
        createElement: (tag) => makeElement(tag),
        createTextNode: (text) => makeText(text),
        createDocumentFragment: () => makeElement('fragment'),
        // Elements register themselves when their id is assigned (see Element.id), so this is a
        // map lookup. The bounded scan is only for anything created before that registration.
        getElementById(id) {
            return byId.get(id) || queryAll(doc.documentElement, '#' + id, 5000)[0] || null;
        },
        __register(id, el) { byId.set(id, el); },
        querySelector(selector) { return queryAll(doc.documentElement, selector)[0] || null; },
        querySelectorAll(selector) { return queryAll(doc.documentElement, selector); },
        getElementsByClassName(name) { return queryAll(doc.documentElement, '.' + name); },
        getElementsByTagName(name) { return queryAll(doc.documentElement, name); },
        addEventListener() {},
        removeEventListener() {},
        dispatchEvent() { return true; },
        /** Plant an element so getElementById() finds it, and register it in the tree. */
        __set(id, el) {
            const node = el || makeElement('div');
            node.id = id;
            byId.set(id, node);
            if (!node.parentElement) { doc.body.appendChild(node); }
            return node;
        },
        __clear() { byId.clear(); }
    };

    doc.documentElement = makeElement('html');
    doc.head = makeElement('head');
    doc.body = makeElement('body');
    doc.documentElement.appendChild(doc.head);
    doc.documentElement.appendChild(doc.body);
    return doc;
}

/**
 * Build a fresh sandbox and evaluate Web/ratings.js inside it.
 *
 * @param {object} [options] Test-supplied globals.
 * @param {object} [options.apiClient] ApiClient stub (serverAddress, getCurrentUserId, accessToken...).
 * @param {object} [options.responses] URL substring -> body. The LONGEST matching key wins, so
 *   "/Social/Profile/<id>/Stats" can sit beside "/Social/Profile/<id>".
 * @param {function} [options.fetch] fetch stub, replacing the table entirely.
 * @param {boolean} [options.authenticated] Report a signed-in user id, or '' for nobody.
 * @returns {object} The sandbox: { plugin, window, document, fetchCalls, timers }.
 */
function loadPlugin(options = {}) {
    const source = fs.readFileSync(SOURCE_PATH, 'utf8');
    const document = options.document || createDocument();
    const fetchCalls = [];
    const timers = [];
    const responses = options.responses || {};

    const apiClient = Object.assign({
        serverAddress: () => 'https://jellyfin.test',
        getCurrentUserId: () => '11111111111111111111111111111111',
        accessToken: () => 'test-token',
        deviceId: () => 'test-device',
        getCurrentUser: () => Promise.resolve({ Id: '11111111111111111111111111111111', Name: 'tester' }),
        serverInfo: () => ({ ServerName: 'Test Server' }),
        getItem: () => Promise.resolve(null)
    }, options.apiClient || {});

    const fetchStub = options.fetch || function (url, init) {
        const target = String(url);
        fetchCalls.push({ url: target, init: init || {} });
        const key = Object.keys(responses)
            .filter((k) => target.indexOf(k) !== -1)
            .sort((a, b) => b.length - a.length)[0];
        if (!key) {
            return Promise.reject(new Error('unexpected fetch in test: ' + target));
        }
        const body = responses[key];
        return Promise.resolve({
            ok: true,
            status: 200,
            json: () => Promise.resolve(body),
            text: () => Promise.resolve(typeof body === 'string' ? body : JSON.stringify(body))
        });
    };

    const window = {
        document,
        location: { hash: '', href: 'https://jellyfin.test/web/index.html', pathname: '/web/index.html', origin: 'https://jellyfin.test', search: '' },
        localStorage: {
            _data: {},
            getItem(k) { return Object.prototype.hasOwnProperty.call(this._data, k) ? this._data[k] : null; },
            setItem(k, v) { this._data[k] = String(v); },
            removeItem(k) { delete this._data[k]; },
            clear() { this._data = {}; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'node-test', maxTouchPoints: 0, language: 'en' },
        matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }),
        getComputedStyle: () => ({ getPropertyValue: () => '', display: 'block', visibility: 'visible', position: 'static' }),
        // Timers are recorded, never run: the plugin's poll loops would otherwise keep Node alive.
        setTimeout: (fn) => { timers.push(fn); return timers.length; },
        clearTimeout() {},
        setInterval: () => 0,
        clearInterval() {},
        requestAnimationFrame: () => 0,
        cancelAnimationFrame() {},
        addEventListener() {},
        removeEventListener() {},
        dispatchEvent() { return true; },
        scrollTo() {},
        open() { return null; },
        close() {},
        focus() {},
        print() {},
        fetch: fetchStub,
        ApiClient: apiClient,
        console,
        history: { state: null, replaceState() {}, pushState() {} },
        MutationObserver: class { observe() {} disconnect() {} takeRecords() { return []; } },
        IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
        ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
        CustomEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init || {}); } },
        Event: class { constructor(type) { this.type = type; } },
        AudioContext: class { constructor() { this.state = 'running'; this.destination = {}; } close() { return Promise.resolve(); } },
        Image: function Image() { return document.createElement('img'); },
        TextEncoder: globalThis.TextEncoder,
        URL: globalThis.URL,
        URLSearchParams: globalThis.URLSearchParams,
        JSON, Promise, Date, Math, Set, Map, Array, Object, String, Number, Boolean, RegExp, Error,
        isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent
    };

    window.window = window;
    window.self = window;
    window.globalThis = window;
    document.defaultView = window;
    document.ownerDocument = document;

    if (options.authenticated === false) {
        apiClient.getCurrentUserId = () => '';
    }

    const sandbox = vm.createContext(window);
    vm.runInContext(source, sandbox, { filename: SOURCE_PATH });

    return {
        sandbox,
        window,
        document,
        fetchCalls,
        timers,
        plugin: window.RatingsPlugin,
        run: (code) => vm.runInContext(code, sandbox)
    };
}

module.exports = { loadPlugin, createDocument, createElement };
