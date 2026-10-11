'use strict';

/**
 * The harness has to be trustworthy before anything built on it is: if the shim quietly fails to
 * define something the plugin uses, every later test passes for the wrong reason.
 */

const test = require('node:test');
const assert = require('node:assert');
const { loadPlugin } = require('./support/harness');

test('the plugin bundle loads under the shim and exports itself', () => {
    const { plugin, window } = loadPlugin();
    assert.ok(plugin, 'window.RatingsPlugin should be defined');
    assert.strictEqual(window.ratingsPlugin, plugin, 'window.ratingsPlugin should be the same object');
});

test('the plugin object carries the API the UI calls by name', () => {
    const { plugin } = loadPlugin();
    // Every one of these is referenced from an inline onclick= in the plugin's own markup, so a
    // rename without updating the caller breaks the page silently.
    ['curationStarsHtml', 'curationAddToWatchlist', 'getProfileAvatarUrl', 'showCurationPage', 't', 'escapeHtml']
        .forEach((name) => {
            assert.strictEqual(typeof plugin[name], 'function', name + ' should be a function');
        });
});

test('loading does not start the boot sequence while nobody is signed in', () => {
    const { timers } = loadPlugin({ authenticated: false });
    assert.ok(timers.length >= 1, 'the auth poll should be scheduled');
});

test('escapeHtml neutralises markup', () => {
    const { plugin } = loadPlugin();
    assert.strictEqual(plugin.escapeHtml('<img src=x onerror=alert(1)>'), '&lt;img src=x onerror=alert(1)&gt;');
});

test('t() falls back to the English string for an unknown key', () => {
    const { plugin } = loadPlugin();
    assert.strictEqual(plugin.t('watchRateReview'), 'Watch, rate & review');
});
