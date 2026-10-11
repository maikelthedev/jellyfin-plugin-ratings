'use strict';

/**
 * Server-backed contract tests. They are the ones that catch the failure a unit test cannot see:
 * the API handing the UI an image URL that Jellyfin answers with a 404.
 *
 * Skipped unless a server is configured, so `npm test` stays hermetic:
 *
 *   RATINGS_TEST_SERVER=http://127.0.0.1:8096 \
 *   RATINGS_TEST_TOKEN=$(cat /tmp/jf.token) \
 *   RATINGS_TEST_USER=<a userId with ratings> \
 *   npm run test:integration
 */

const test = require('node:test');
const assert = require('node:assert');

const SERVER = process.env.RATINGS_TEST_SERVER || '';
const TOKEN = process.env.RATINGS_TEST_TOKEN || '';
const USER = process.env.RATINGS_TEST_USER || '';
const enabled = Boolean(SERVER && TOKEN && USER);

const headers = { Authorization: 'MediaBrowser Token="' + TOKEN + '"' };

test('every library rating the profile API returns has artwork that resolves',
    { skip: enabled ? false : 'set RATINGS_TEST_SERVER, RATINGS_TEST_TOKEN and RATINGS_TEST_USER' },
    async () => {
        const res = await fetch(SERVER + '/Ratings/Users/' + USER + '/Ratings?limit=500', { headers });
        assert.strictEqual(res.status, 200, 'ratings list should load');
        const ratings = await res.json();
        assert.ok(Array.isArray(ratings) && ratings.length > 0, 'expected the user to have ratings');

        const inLibrary = ratings.filter((r) => r.InLibrary || r.inLibrary);
        assert.ok(inLibrary.length > 0, 'expected at least one rating for a title still in the library');

        // Fetch one poster per distinct URL: the same season-stored show repeats across a profile.
        const urls = [...new Set(inLibrary.map((r) => r.PosterUrl || r.posterUrl).filter(Boolean))];
        assert.ok(urls.length > 0, 'a library rating must come back with a poster');

        const broken = [];
        for (const url of urls) {
            const absolute = /^https?:\/\//i.test(url) ? url : SERVER + url;
            const image = await fetch(absolute, { headers, method: 'GET' });
            if (image.status !== 200) {
                broken.push(image.status + ' ' + url);
            }
        }

        assert.deepStrictEqual(broken, [], 'Jellyfin could not serve these posters:\n  ' + broken.join('\n  '));
    });

test('a rating stored on a season still reports the show as its poster',
    { skip: enabled ? false : 'set RATINGS_TEST_SERVER, RATINGS_TEST_TOKEN and RATINGS_TEST_USER' },
    async () => {
        const res = await fetch(SERVER + '/Ratings/Users/' + USER + '/Ratings?limit=500', { headers });
        const ratings = await res.json();

        // A season id is what the UI must NOT be told to fetch artwork from.
        const posts = await Promise.all(ratings.map(async (r) => {
            const type = String(r.Type || r.type || '');
            return { type, id: r.ItemId || r.itemId, poster: r.PosterUrl || r.posterUrl || '', name: r.ItemName || r.itemName };
        }));
        const seasons = posts.filter((p) => /season/i.test(p.type));
        assert.ok(seasons.length > 0, 'this profile has no season-stored ratings to check');

        const wrong = seasons.filter((p) => p.poster && p.poster.indexOf(p.id.toString()) !== -1);
        assert.deepStrictEqual(
            wrong.map((p) => p.name + ' -> ' + p.poster), [],
            'a season-stored rating still points at the season for its artwork');
    });
