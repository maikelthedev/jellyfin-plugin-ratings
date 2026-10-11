'use strict';

/**
 * The profile page's right-hand column lists other users ("Similar taste"). Those rows were drawn
 * with the initial letter only - no picture was ever requested - so every profile in that sidebar
 * looked like a placeholder even when the user has a Jellyfin profile image.
 *
 * The picture goes over the initial, which stays behind it as the fallback for someone who has no
 * image (the delegated .plugin-img-fallback handler hides a broken one).
 */

const test = require('node:test');
const assert = require('node:assert');
const { loadPlugin } = require('./support/harness');

const VIEWED = '11111111111111111111111111111111';
const OTHER = '22222222222222222222222222222222';
const SERIES = 'b'.repeat(32);

function loadWithSimilar(matches) {
    const harness = loadPlugin({ responses: { '/SimilarUsers': { matches } } });
    harness.plugin._viewingProfileUserId = VIEWED;
    return harness;
}

const settle = () => new Promise((resolve) => setImmediate(resolve));

test('a similar user in the sidebar gets their profile picture, over the initial', async () => {
    const harness = loadWithSimilar([
        { userId: OTHER, username: 'vicente', sharedGenres: ['Romance'], isOnline: true }
    ]);
    const el = harness.document.__set('lbSimilarUsers');

    harness.plugin.loadProfileSimilarUsers();
    await settle();
    await settle();

    assert.ok(
        el.innerHTML.includes('/Users/' + OTHER + '/Images/Primary'),
        'should request the user picture, got: ' + el.innerHTML);
    assert.ok(/>V\b/.test(el.innerHTML), 'should keep the initial behind it, got: ' + el.innerHTML);
});

test('a similar user with no picture still shows the initial', async () => {
    const harness = loadWithSimilar([
        { userId: '', username: 'jani', sharedGenres: ['Drama'], isOnline: false }
    ]);
    const el = harness.document.__set('lbSimilarUsers');

    harness.plugin.loadProfileSimilarUsers();
    await settle();
    await settle();

    assert.ok(/>J\b/.test(el.innerHTML), 'should show the initial, got: ' + el.innerHTML);
    assert.ok(!el.innerHTML.includes('/Images/Primary'), 'should not request a picture without a user id');
});

test('review posters load lazily, so a long review list does not fetch every poster at once', async () => {
    const harness = loadPlugin({
        responses: {
            '/Ratings/Users/': [{
                ItemId: SERIES,
                ItemName: 'A Show',
                Rating: 8,
                ReviewText: 'Good',
                InLibrary: true,
                PosterUrl: '/Items/' + SERIES + '/Images/Primary'
            }]
        }
    });
    harness.plugin._viewingProfileUserId = VIEWED;
    const el = harness.document.__set('lbProfileContent');

    harness.plugin.renderProfileReviewsTab();
    await settle();
    await settle();

    assert.ok(/<img[^>]+loading="lazy"/.test(el.innerHTML), 'poster should be a lazy <img>, got: ' + el.innerHTML);
});
