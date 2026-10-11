'use strict';

/**
 * A deliberately small browser shim, just big enough to load Web/ratings.js in Node and drive the
 * functions under test. It is NOT a DOM implementation: elements record the HTML handed to them so
 * a test can assert on markup, and nothing is laid out, painted or animated.
 *
 * The plugin boots at the bottom of its IIFE and calls RatingsPlugin.init() as soon as
 * isUserAuthenticated() is true. The shim leaves ApiClient without a current user by default, so
 * loading is inert and a test decides what to call. Pass { authenticated: true } to walk the boot
 * path when that is what is being tested.
 */

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SOURCE_PATH = path.join(__dirname, '..', '..', 'Web', 'ratings.js');

/**
 * What a browser's HTML fragment serialiser does to a text node: & < > escaped, quotes left alone.
 * escapeHtml() in the plugin relies on exactly this, so the shim has to model it or that function
 * under test would appear to return nothing.
 */
function escapeText(value) {
    return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Crude inverse of escapeText, good enough for assertions on text content. */
function unescapeText(value) {
    return String(value)
        .replace(/<[^>]*>/g, '')
        .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
}

/** Minimal element: records attributes, class names and innerHTML. */
function createElement(tagName) {
    const el = {
        tagName: String(tagName || 'div').toUpperCase(),
        nodeType: 1,
        id: '',
        className: '',
        textContent: '',
        value: '',
        checked: false,
        disabled: false,
        hidden: false,
        children: [],
        parentElement: null,
        parentNode: null,
        style: { setProperty() {}, removeProperty() {}, cssText: '' },
        dataset: {},
        attributes: {},
        _html: '',
        _text: '',
        _listeners: {},
        get innerHTML() { return this._html; },
        set innerHTML(value) { this._html = String(value); this._text = unescapeText(value); },
        get textContent() { return this._text; },
        set textContent(value) { this._text = value === null || value === undefined ? '' : String(value); this._html = escapeText(this._text); },
        get outerHTML() { return '<' + this.tagName.toLowerCase() + '>' + this._html + '</' + this.tagName.toLowerCase() + '>'; },
        get firstChild() { return this.children[0] || null; },
        get firstElementChild() { return this.children[0] || null; },
        get lastElementChild() { return this.children[this.children.length - 1] || null; },
        get nextElementSibling() { return null; },
        get previousElementSibling() { return null; },
        get childElementCount() { return this.children.length; },
        classList: {
            _set: new Set(),
            add(...names) { names.forEach((n) => this._set.add(n)); },
            remove(...names) { names.forEach((n) => this._set.delete(n)); },
            toggle(name, force) { if (force === undefined ? !this._set.has(name) : force) { this._set.add(name); } else { this._set.delete(name); } },
            contains(name) { return this._set.has(name); }
        },
        appendChild(child) { this.children.push(child); if (child) { child.parentElement = this; } return child; },
        insertBefore(child) { this.children.unshift(child); if (child) { child.parentElement = this; } return child; },
        removeChild(child) { this.children = this.children.filter((c) => c !== child); return child; },
        replaceChild(next, old) { this.children = this.children.map((c) => (c === old ? next : c)); return old; },
        remove() { if (this.parentElement) { this.parentElement.removeChild(this); } },
        setAttribute(name, value) { this.attributes[name] = String(value); if (name === 'class') { this.className = String(value); } if (name === 'id') { this.id = String(value); } },
        getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null; },
        removeAttribute(name) { delete this.attributes[name]; },
        hasAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name); },
        addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); },
        removeEventListener(type, fn) { this._listeners[type] = (this._listeners[type] || []).filter((f) => f !== fn); },
        dispatch(type, event) { (this._listeners[type] || []).forEach((fn) => fn(event || { target: this })); },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        closest() { return null; },
        contains() { return false; },
        matches() { return false; },
        focus() {},
        blur() {},
        click() { this.dispatch('click'); },
        play() { return Promise.resolve(); },
        pause() {},
        load() {},
        scrollIntoView() {},
        animate() { return { cancel() {}, finished: Promise.resolve() }; },
        getBoundingClientRect() { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; }
    };
    return el;
}

/** Document shim with an id registry so a test can plant the element a function looks for. */
function createDocument() {
    const byId = new Map();
    const doc = {
        readyState: 'complete',
        currentScript: { src: 'https://jellyfin.test/Ratings/ratings.js?v=test' },
        title: '',
        hidden: false,
        visibilityState: 'visible',
        documentElement: Object.assign(createElement('html'), { lang: 'en' }),
        head: createElement('head'),
        body: createElement('body'),
        cookie: '',
        createElement: (tag) => createElement(tag),
        createTextNode: (text) => ({ nodeType: 3, textContent: String(text), nodeValue: String(text) }),
        createDocumentFragment: () => createElement('fragment'),
        getElementById(id) { return byId.get(id) || null; },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        getElementsByClassName() { return []; },
        getElementsByTagName() { return []; },
        addEventListener() {},
        removeEventListener() {},
        dispatchEvent() { return true; },
        /** Plant an element so getElementById() finds it. */
        __set(id, el) { const node = el || createElement('div'); node.id = id; byId.set(id, node); return node; },
        __clear() { byId.clear(); }
    };
    doc.documentElement.ownerDocument = doc;
    doc.body.ownerDocument = doc;
    return doc;
}

/**
 * Build a fresh sandbox and evaluate Web/ratings.js inside it.
 *
 * @param {object} [options] Test-supplied globals.
 * @param {object} [options.apiClient] ApiClient stub (serverAddress, getCurrentUserId, accessToken...).
 * @param {function} [options.fetch] fetch stub; defaults to one that records calls and rejects.
 * @param {boolean} [options.authenticated] Report a signed-in user, so the plugin boots.
 * @returns {object} The sandbox: { plugin, window, document, fetchCalls, timers }.
 */
function loadPlugin(options = {}) {
    const source = fs.readFileSync(SOURCE_PATH, 'utf8');
    const document = options.document || createDocument();
    const fetchCalls = [];
    const timers = [];

    const apiClient = Object.assign({
        serverAddress: () => 'https://jellyfin.test',
        getCurrentUserId: () => (options.authenticated === false ? '' : '11111111111111111111111111111111'),
        accessToken: () => 'test-token',
        deviceId: () => 'test-device',
        getCurrentUser: () => Promise.resolve({ Id: '11111111111111111111111111111111', Name: 'tester' }),
        serverInfo: () => ({ ServerName: 'Test Server' }),
        getItem: () => Promise.resolve(null)
    }, options.apiClient || {});

    if (options.authenticated === false) {
        apiClient.getCurrentUserId = () => '';
    }

    /** fetch stub: records {url, init} and answers from options.responses (url substring -> body). */
    const fetchStub = options.fetch || function (url, init) {
        fetchCalls.push({ url: String(url), init: init || {} });
        const responses = options.responses || {};
        const key = Object.keys(responses).find((k) => String(url).indexOf(k) !== -1);
        if (!key) {
            return Promise.reject(new Error('unexpected fetch in test: ' + url));
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
        getComputedStyle: () => ({ getPropertyValue: () => '', display: 'block', visibility: 'visible' }),
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
        MutationObserver: class { observe() {} disconnect() {} takeRecords() { return []; } },
        IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
        ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
        CustomEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init || {}); } },
        Event: class { constructor(type) { this.type = type; } },
        AudioContext: class { constructor() { this.state = 'running'; this.destination = {}; } close() { return Promise.resolve(); } },
        MutationEvent: class {},
        Image: function Image() { return createElement('img'); },
        HTMLMediaElement: class {},
        TextEncoder: globalThis.TextEncoder,
        URL: globalThis.URL,
        URLSearchParams: globalThis.URLSearchParams,
        JSON,
        Promise,
        Date,
        Math,
        Set,
        Map,
        Array,
        Object,
        String,
        Number,
        Boolean,
        RegExp,
        Error,
        isNaN,
        parseInt,
        parseFloat,
        encodeURIComponent,
        decodeURIComponent
    };

    window.window = window;
    window.self = window;
    window.globalThis = window;
    document.defaultView = window;
    document.ownerDocument = document;
    // Nested lookups the plugin uses directly.
    document.body.ownerDocument = document;
    document.documentElement.ownerDocument = document;

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
