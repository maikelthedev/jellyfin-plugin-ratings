'use strict';

/**
 * The plugin's UI calls the Jellyfin API with a MediaBrowser auth header that carries a client
 * version. That value was hardcoded to "10.11.0" in 46 call sites, and nothing noticed when the
 * server moved to 12.1 - the kind of drift that only shows up as a mysterious session list, or
 * worse, a client-version-gated code path on the server.
 *
 * The version now comes from our own script URL's ?v=<pluginVersion> (see CLIENT_VERSION), so no
 * call site may carry a literal version again.
 */

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..');
const FILES = ['Web/ratings.js', 'Pages/ratings_dashboard.js'];

test('no auth header hardcodes a Jellyfin version', () => {
    for (const rel of FILES) {
        const src = fs.readFileSync(path.join(ROOT, rel), 'utf8');
        const literal = /Version="\d+\.\d+\.\d+"/g.exec(src);
        assert.strictEqual(literal, null,
            rel + ' hardcodes ' + (literal && literal[0]) + ' - use CLIENT_VERSION instead');
    }
});

test('the client version comes from the script URL, with a fallback', () => {
    const src = fs.readFileSync(path.join(ROOT, 'Web/ratings.js'), 'utf8');
    assert.match(src, /const CLIENT_VERSION = /, 'CLIENT_VERSION should be defined');
    assert.match(src, /\[\?&\]v=/, 'CLIENT_VERSION should read the ?v= param the server injects');
    const fallback = /decodeURIComponent\(m\[1\]\)\) \|\| '([^']+)'/.exec(src);
    assert.ok(fallback, 'CLIENT_VERSION needs a fallback for when ?v= is missing');
    const csproj = fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.Ratings.csproj'), 'utf8');
    const version = /<Version>([^<]+)<\/Version>/.exec(csproj)[1];
    assert.strictEqual(fallback[1], version,
        'the fallback must match the csproj version, or it silently goes stale again');
});

test('the version actually resolves from a served script URL', () => {
    const src = fs.readFileSync(path.join(ROOT, 'Web/ratings.js'), 'utf8');
    const body = /const CLIENT_VERSION = \(function \(\) \{[\s\S]*?\}\)\(\);/.exec(src)[0];
    const resolve = (SCRIPT_SRC) => eval('(function(){' + body + 'return CLIENT_VERSION;})()');
    assert.strictEqual(resolve('/Ratings/ratings.js?v=9.9.9'), '9.9.9');
    assert.strictEqual(resolve('https://host/jf/Ratings/ratings.js?v=1.2.3&x=1'), '1.2.3');
    assert.ok(/^\d+\.\d+\.\d+\.\d+$/.test(resolve('')), 'falls back to the plugin version');
});
