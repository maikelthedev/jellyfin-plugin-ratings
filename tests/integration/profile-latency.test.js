'use strict';

/**
 * A profile page fans out to a handful of endpoints at once, so one of them going quadratic is
 * what "profiles are really slow" has meant before: ratings used to be enriched with a library
 * lookup per rating, which is fine for a dozen and painful for a few hundred.
 *
 * The budget is deliberately loose - this is not a benchmark, it is a tripwire for that shape of
 * regression, and it only ever runs against a real server on request.
 */

const test = require('node:test');
const assert = require('node:assert');

const SERVER = process.env.RATINGS_TEST_SERVER || '';
const TOKEN = process.env.RATINGS_TEST_TOKEN || '';
const USER = process.env.RATINGS_TEST_USER || '';
const enabled = Boolean(SERVER && TOKEN && USER);

const headers = { Authorization: 'MediaBrowser Token="' + TOKEN + '"' };
const BUDGET_MS = 2000;

test('a profile\'s data loads without a per-rating stall',
    { skip: enabled ? false : 'set RATINGS_TEST_SERVER, RATINGS_TEST_TOKEN and RATINGS_TEST_USER' },
    async () => {
        // The ratings list is the one that walks the viewer's whole rating set, so it is asked for
        // more than the page needs: a per-rating cost shows up here first.
        const endpoints = [
            '/Ratings/Users/' + USER + '/Ratings?limit=500',
            '/Social/Profile/' + USER,
            '/Social/Profile/' + USER + '/Stats',
            '/Social/Profile/' + USER + '/SimilarUsers?limit=5',
            '/Ratings/MostActiveUsers?limit=10'
        ];

        const slow = [];
        for (const path of endpoints) {
            const started = Date.now();
            const res = await fetch(SERVER + path, { headers });
            const ms = Date.now() - started;
            assert.strictEqual(res.status, 200, path + ' should answer 200');
            await res.json();
            if (ms > BUDGET_MS) {
                slow.push(path + ' took ' + ms + 'ms');
            }
        }

        assert.deepStrictEqual(slow, [], 'over the ' + BUDGET_MS + 'ms budget:\n  ' + slow.join('\n  '));
    });
